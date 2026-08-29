using Shellvis.Core.Shell;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// The two shell capabilities the original plan listed and never built.
///
/// Both are exercised for real: a genuine Windows PowerShell 5.1 process and genuine
/// background children. There is nothing worth mocking here, because everything that can go
/// wrong is about how a real process behaves. The two failures this guards against have both
/// happened in this project already: an encoding mismatch that turned every umlaut into a
/// replacement character, and a command line quoted by .NET's rules instead of the receiving
/// program's, which made a path with a space arrive as a name nothing could find.
///
/// The harness leaves no children behind. That is checked, not assumed: an orphaned
/// background process holding a port, with nothing left that can show or stop it, is worse
/// than losing the run.
/// </summary>
internal static class ProcessProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        void Check(string what, bool passed, string detail = "")
        {
            if (!passed)
                failures++;

            Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        using var processes = new BackgroundProcesses();
        var tools = new ProcessTools(processes);

        Console.WriteLine("windows powershell 5.1, out of process\n");

        Check("the old engine is present on this machine", WindowsPowerShell.IsAvailable);

        if (WindowsPowerShell.IsAvailable)
        {
            string version = await tools
                .RunWindowsPowerShell("$PSVersionTable.PSVersion.Major")
                .ConfigureAwait(false);

            Check("it answers, and it really is version 5",
                version.TrimStart().StartsWith('5'), Summarise(version));

            // The whole reason this tool exists: it must NOT be the hosted engine.
            string edition = await tools
                .RunWindowsPowerShell("$PSVersionTable.PSEdition")
                .ConfigureAwait(false);

            Check("and the Desktop edition, not the hosted Core one",
                edition.Contains("Desktop", StringComparison.Ordinal), Summarise(edition));

            // The encoding trap. 5.1 emits in the console OEM code page unless told
            // otherwise, and on a German machine that turns every umlaut into a
            // replacement character. Checked with the characters that actually break.
            string umlauts = await tools
                .RunWindowsPowerShell("Write-Output 'Grüße aus München: Äpfel, Öl, Straße'")
                .ConfigureAwait(false);

            Check("umlauts survive the pipe",
                umlauts.Contains("Grüße aus München", StringComparison.Ordinal), Summarise(umlauts));
            Check("and so does an eszett", umlauts.Contains("Straße", StringComparison.Ordinal));

            // The quoting trap, twice met in this project: .NET's argument quoting is not
            // the receiving program's. EncodedCommand takes the script as data, so a script
            // full of quotes must arrive intact.
            string quoted = await tools
                .RunWindowsPowerShell("Write-Output \"a 'b' \\\"c\\\" & d | e\"")
                .ConfigureAwait(false);

            Check("quotes and shell metacharacters survive",
                quoted.Contains("a 'b'", StringComparison.Ordinal)
                && quoted.Contains("& d | e", StringComparison.Ordinal), Summarise(quoted));

            string failed = await tools
                .RunWindowsPowerShell("throw 'deliberate'")
                .ConfigureAwait(false);

            Check("an error comes back as text rather than as an exception",
                failed.Contains("deliberate", StringComparison.Ordinal), Summarise(failed));
            Check("and the exit code is reported",
                failed.Contains("exit 1", StringComparison.Ordinal), Summarise(failed));

            // The error must not arrive as the serialised object graph. 5.1 always writes
            // CLIXML to a redirected error stream whatever -OutputFormat says, and several
            // hundred characters of XML in a tool result is worse than silence because it
            // looks like output.
            Check("an error is not raw CLIXML",
                !failed.Contains("#< CLIXML", StringComparison.Ordinal)
                && !failed.Contains("<Objs Version", StringComparison.Ordinal), Summarise(failed));

            Check("and its line breaks are line breaks, not escapes",
                !failed.Contains("_x000D_", StringComparison.Ordinal));

            // The position must point at the caller's script, not at the encoding prelude
            // this class prepends.
            Check("the error points at the script that was passed, not at the prelude",
                failed.Contains("throw 'deliberate'", StringComparison.Ordinal), Summarise(failed));

            // A script that never ends must be stopped, not waited on forever.
            var clock = System.Diagnostics.Stopwatch.StartNew();

            string slow = await tools
                .RunWindowsPowerShell("Start-Sleep -Seconds 30", timeoutSeconds: 3)
                .ConfigureAwait(false);

            clock.Stop();

            Check("a script that overruns is stopped",
                slow.Contains("was stopped", StringComparison.Ordinal), Summarise(slow));
            Check($"and stopped near its timeout, not after 30s ({clock.Elapsed.TotalSeconds:F0}s)",
                clock.Elapsed.TotalSeconds < 12);

            Check("an empty script is refused",
                (await tools.RunWindowsPowerShell("  ").ConfigureAwait(false))
                    .StartsWith("error:", StringComparison.Ordinal));
        }

        Console.WriteLine("\nbackground processes:");

        Check("nothing is running before anything is started",
            (await tools.Manage("list").ConfigureAwait(false))
                .Contains("nothing has been started", StringComparison.Ordinal));

        Check("start without a command is refused",
            (await tools.Manage("start").ConfigureAwait(false))
                .StartsWith("error:", StringComparison.Ordinal));

        Check("an unknown action is refused with the list of real ones",
            (await tools.Manage("frobnicate").ConfigureAwait(false))
                .Contains("Use start, list, poll", StringComparison.Ordinal));

        Check("poll without an id is refused",
            (await tools.Manage("poll").ConfigureAwait(false))
                .StartsWith("error:", StringComparison.Ordinal));

        Check("an id nobody started is an answer, not a crash",
            (await tools.Manage("poll", id: "p999").ConfigureAwait(false))
                .Contains("no background process", StringComparison.Ordinal));

        string started = await tools
            .Manage("start", command: "echo hallo && ping -n 4 127.0.0.1")
            .ConfigureAwait(false);

        Check("a command starts and returns an id",
            started.StartsWith("started p", StringComparison.Ordinal), Summarise(started));

        string id = started.Split(' ')[1];

        Check("it appears in the list",
            (await tools.Manage("list").ConfigureAwait(false)).Contains(id, StringComparison.Ordinal));

        // The point of the whole tool: this returns while the command is still going.
        string polled = await tools.Manage("poll", id: id).ConfigureAwait(false);

        Check("polling reports it as running",
            polled.Contains("running for", StringComparison.Ordinal), Summarise(polled));

        // A wait that runs out is NOT a failure and must not read like one, or a model
        // kills a build that was going perfectly well.
        string waited = await tools.Manage("wait", id: id, timeoutSeconds: 1).ConfigureAwait(false);

        Check("a wait that runs out says so and suggests waiting again",
            waited.Contains("still running", StringComparison.Ordinal)
            && waited.Contains("Wait again", StringComparison.Ordinal), Summarise(waited));

        string finished = await tools.Manage("wait", id: id, timeoutSeconds: 30).ConfigureAwait(false);

        Check("waiting long enough sees it finish",
            finished.Contains("exited 0", StringComparison.Ordinal), Summarise(finished));

        string log = await tools.Manage("log", id: id).ConfigureAwait(false);

        Check("the output was buffered while nobody was looking",
            log.Contains("hallo", StringComparison.Ordinal), Summarise(log));

        // cmd quoting, the mismatch that has bitten this project twice.
        string quotedRun = await tools
            .Manage("start", command: "echo \"a b\" & echo c|findstr c")
            .ConfigureAwait(false);

        string quotedId = quotedRun.Split(' ')[1];
        await tools.Manage("wait", id: quotedId, timeoutSeconds: 20).ConfigureAwait(false);

        string quotedLog = await tools.Manage("log", id: quotedId).ConfigureAwait(false);

        Check("a command with quotes and a pipe runs as written",
            quotedLog.Contains("a b", StringComparison.Ordinal)
            && quotedLog.Contains('c', StringComparison.Ordinal), Summarise(quotedLog));

        Console.WriteLine("\nstopping:");

        string longRun = await tools
            .Manage("start", command: "ping -n 60 127.0.0.1")
            .ConfigureAwait(false);

        string longId = longRun.Split(' ')[1];

        string killed = await tools.Manage("kill", id: longId).ConfigureAwait(false);

        Check("killing reports it as no longer running",
            !killed.Contains("running for", StringComparison.Ordinal), Summarise(killed));

        Check("killing something already gone is an answer, not a crash",
            (await tools.Manage("kill", id: id).ConfigureAwait(false)).Length > 0);

        Console.WriteLine("\nin the catalog:");

        var registry = new ToolRegistry();
        registry.RegisterFrom(tools);

        foreach (string name in new[] { "powershell_run_winps", "process" })
        {
            ToolEntry? entry = registry.Tools.FirstOrDefault(t => t.Name == name);

            Check($"{name} is registered", entry is not null);
            Check($"{name} is Mutating, so the classifier decides per call",
                entry?.SideEffect == SideEffect.Mutating);
        }

        // Without this the classifier never looks at a 5.1 script, and every provable read
        // against a legacy module raises a prompt: the exact way a user learns to click
        // Allow without reading.
        var assessor = new Shellvis.Core.Agent.PowerShellRiskAssessor();

        ToolEntry winps = registry.Tools.First(t => t.Name == "powershell_run_winps");

        Check("a provable read under 5.1 is downgraded to silent",
            assessor.Assess(winps, Args("Get-Process")) == SideEffect.ReadOnly);

        Check("and a write under 5.1 still prompts",
            assessor.Assess(winps, Args("Remove-Item x")) != SideEffect.ReadOnly);

        Console.WriteLine("\nleaving nothing behind:");

        // Dispose is what runs when the application closes.
        string before = await tools.Manage("start", command: "ping -n 60 127.0.0.1").ConfigureAwait(false);
        string orphanId = before.Split(' ')[1];
        int orphanPid = PidOf(processes, orphanId);

        processes.Dispose();

        await Task.Delay(500).ConfigureAwait(false);

        Check("closing kills what was still running",
            !IsAlive(orphanPid), $"pid {orphanPid}");

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: 5.1 answers as Desktop edition with its umlauts and quotes intact,\n"
                + "a background command returns immediately and can be polled, logged, waited\n"
                + "on and killed, and nothing is left running afterwards."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    private static IReadOnlyDictionary<string, object?> Args(string script) =>
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["script"] = script };

    private static int PidOf(BackgroundProcesses processes, string id) =>
        processes.Poll(id)?.ProcessId ?? 0;

    private static bool IsAlive(int pid)
    {
        if (pid == 0)
            return false;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process, which is the answer being looked for.
            return false;
        }
    }

    private static string Summarise(string text)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 90 ? flat : flat[..90] + "...";
    }
}
