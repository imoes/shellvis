using Shellvis.Core.Agent;
using Shellvis.Core.Shell;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// PowerShell Remoting, against a real WinRM listener.
///
/// The target is localhost, and that is not a shortcut: it exercises the whole WS-Man stack
/// -- the client library, the Kerberos handshake, session creation, script marshalling and
/// output formatting -- from inside the HOSTED runspace, which is the part that could not be
/// assumed to work. This project has already been caught twice by the hosted SDK behaving
/// unlike the console (the Modules layout, InvariantGlobalization); "remoting works in
/// PowerShell" says nothing about whether it works here.
///
/// What localhost cannot prove: that a firewall, a name that does not resolve or a machine
/// outside the domain produce useful messages. Those paths are checked against a name that
/// certainly does not exist, which is the honest half of that.
/// </summary>
internal static class RemoteProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        using var host = new PowerShellHost();
        var remote = new RemoteTools(host);

        Console.WriteLine();
        Console.WriteLine("-- before connecting --");

        string none = await remote.List().ConfigureAwait(false);
        Console.WriteLine($"    {Clip(none)}");

        failures += Expect(
            none.Contains("no remote sessions", StringComparison.Ordinal),
            "no sessions are reported when none are open");

        string orphan = await remote.Run("nowhere", "Get-Date").ConfigureAwait(false);
        Console.WriteLine($"    {Clip(orphan)}");

        failures += Expect(
            orphan.Contains("remote_connect", StringComparison.Ordinal),
            "running without a session says to connect first, by tool name");

        Console.WriteLine();
        Console.WriteLine("-- a name that cannot resolve --");

        string bad = await remote
            .Connect("shellvis-no-such-host-4711", timeoutSeconds: 30)
            .ConfigureAwait(false);

        Console.WriteLine($"    {Clip(bad, 300)}");

        failures += Expect(
            bad.StartsWith("could not connect", StringComparison.Ordinal),
            "an unreachable host fails rather than pretending");

        // The reason this check exists: a bare WS-Man error covers a firewall, a service
        // that was never enabled, a name that does not resolve and a machine outside the
        // domain, and the fix differs for each.
        failures += Expect(
            bad.Contains("Enable-PSRemoting", StringComparison.Ordinal)
                || bad.Contains("Test-WSMan", StringComparison.Ordinal),
            "and the failure names something to try");

        Console.WriteLine();
        Console.WriteLine("-- localhost over winrm --");

        string opened = await remote.Connect("localhost", timeoutSeconds: 60).ConfigureAwait(false);
        Console.WriteLine($"    {Clip(opened, 300)}");

        if (!opened.StartsWith("connected", StringComparison.Ordinal))
        {
            // Not a failure of the code: a machine with no listener, or one where policy
            // forbids loopback remoting, cannot run the rest. Said plainly rather than
            // reported as a pass or a bug.
            Console.WriteLine();
            Console.WriteLine("SKIPPED the live half: this machine would not open a session to "
                + "itself, so session reuse and copying are untested here. Everything above "
                + "passed.");

            return failures == 0 ? 0 : 1;
        }

        failures += Expect(
            opened.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase),
            "the session is proven by reading the remote machine's own name back");

        string listed = await remote.List().ConfigureAwait(false);
        Console.WriteLine($"    {Clip(listed)}");

        failures += Expect(
            listed.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                && listed.Contains("Opened", StringComparison.Ordinal),
            "the open session is listed as open");

        Console.WriteLine();
        Console.WriteLine("-- state survives between calls --");

        // The whole reason for a persistent session rather than a call per command. If this
        // failed, every remote call would start from nothing and the tool would be a worse
        // Invoke-Command.
        await remote.Run("localhost", "$global:ShellvisProbeMarker = 'kept'").ConfigureAwait(false);
        string recalled = await remote.Run("localhost", "$global:ShellvisProbeMarker")
            .ConfigureAwait(false);

        Console.WriteLine($"    {Clip(recalled)}");

        failures += Expect(
            recalled.Contains("kept", StringComparison.Ordinal),
            "a variable set in one call is still there in the next");

        Console.WriteLine();
        Console.WriteLine("-- quoting cannot escape the script --");

        // Base64 exists so that a script full of quotes, backticks and dollar signs
        // arrives intact. Three of this project's bugs came from nesting one language's
        // quoting inside another's, so it gets a test.
        const string awkward = "\"it's\" + '`$(1+1)' + \"$([char]34)\"";
        string quoted = await remote.Run("localhost", $"({awkward})").ConfigureAwait(false);

        Console.WriteLine($"    {Clip(quoted)}");

        failures += Expect(
            quoted.Contains("it's", StringComparison.Ordinal)
                && quoted.Contains("$(1+1)", StringComparison.Ordinal),
            "quotes, backticks and subexpressions survive verbatim");

        Console.WriteLine();
        Console.WriteLine("-- copying over the session --");

        string source = Path.Combine(Path.GetTempPath(), "shellvis-remote-probe.txt");
        string target = Path.Combine(Path.GetTempPath(), "shellvis-remote-probe-copy.txt");
        await File.WriteAllTextAsync(source, "Shellvis has entered the building.").ConfigureAwait(false);
        File.Delete(target);

        string copied = await remote.Copy("localhost", source, target).ConfigureAwait(false);
        Console.WriteLine($"    {Clip(copied)}");

        failures += Expect(File.Exists(target), "a file copied to the session arrives");

        Console.WriteLine();
        Console.WriteLine("-- and the risk classification --");

        var assessor = new PowerShellRiskAssessor();
        var registry = new ToolRegistry();
        registry.RegisterFrom(remote);

        ToolEntry run = registry.Tools.First(t => t.Name == "remote_run");

        failures += Expect(
            assessor.Assess(run, Args("Get-Service -Name Spooler")) == SideEffect.ReadOnly,
            "a provable read on a remote machine runs silently");

        // The escalation that matters: a write on someone else's machine asks even in yolo,
        // for the same reason every privileged broker call does. What differs from a local
        // write is not what it does but where.
        failures += Expect(
            assessor.Assess(run, Args("Restart-Service -Name Spooler")) == SideEffect.AlwaysAsk,
            "a remote write is escalated to always-ask, not merely mutating");

        failures += Expect(
            assessor.Assess(run, Args("Get-Date; Remove-Item C:\\x -Recurse -Force"))
                == SideEffect.AlwaysAsk,
            "and so is a read with a write hidden behind a semicolon");

        Console.WriteLine();
        Console.WriteLine("-- cleaning up --");

        string closed = await remote.Disconnect().ConfigureAwait(false);
        Console.WriteLine($"    {Clip(closed)}");

        string after = await remote.List().ConfigureAwait(false);

        failures += Expect(
            after.Contains("no remote sessions", StringComparison.Ordinal),
            "disconnecting leaves nothing behind");

        File.Delete(source);
        File.Delete(target);

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: sessions open, persist between calls, carry scripts verbatim, copy "
              + "files, and a remote write always asks."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    private static Dictionary<string, object?> Args(string script) =>
        new(StringComparer.Ordinal) { ["script"] = script };

    private static string Clip(string value, int limit = 160)
    {
        string flat = value.ReplaceLineEndings(" ").Trim();
        return flat.Length <= limit ? flat : flat[..limit] + "...";
    }

    private static int Expect(bool condition, string what)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }
}
