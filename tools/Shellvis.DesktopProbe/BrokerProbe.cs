using System.Diagnostics;
using Shellvis.Contracts;
using Shellvis.Core.Broker;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Exercises the broker over a real named pipe.
///
/// The broker is started as a console process in this user's context, not installed as a
/// service, because registering a service needs administrator rights this machine does not
/// grant. What that means honestly: the PROTOCOL, the ACL and every refusal are verified
/// here; the service HOSTING -- session 0, LocalSystem, automatic start -- is not, and
/// cannot be until someone installs it with elevation.
///
/// That split is worth stating rather than glossing over, because the part left untested
/// is the part that changes the process's rights.
/// </summary>
internal static class BrokerProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        Console.WriteLine("=== Broker ===");
        Console.WriteLine();

        failures += await UnreachableAsync().ConfigureAwait(false);

        string exe = FindBroker();

        if (exe.Length == 0)
        {
            Console.WriteLine("  FAIL the broker executable was not found; build Shellvis.Broker first.");
            return failures + 1;
        }

        Console.WriteLine($"  starting {Path.GetFileName(exe)} in console mode");

        using Process? broker = Start(exe);

        if (broker is null)
        {
            Console.WriteLine("  FAIL the broker did not start.");
            return failures + 1;
        }

        try
        {
            var client = new BrokerClient();

            if (!await WaitForBrokerAsync(client).ConfigureAwait(false))
            {
                Console.WriteLine("  FAIL the broker never answered a ping.");
                return failures + 1;
            }

            failures += await HandshakeAsync(client).ConfigureAwait(false);
            failures += await RegistryGuardsAsync(client).ConfigureAwait(false);
            failures += await SelfPreservationAsync(client).ConfigureAwait(false);
            failures += await ServicesAsync(client).ConfigureAwait(false);
            failures += await ToolSurfaceAsync(client).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                broker.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: the pipe protocol works, and every guard refuses. Service hosting is untested here."
            : $"{failures} broker check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// With no broker running, the client must say so quickly and helpfully.
    ///
    /// This is the state most machines will be in -- user-mode install, no service -- so
    /// it is the message most people will see.
    /// </summary>
    private static async Task<int> UnreachableAsync()
    {
        Console.WriteLine("-- with no broker running --");
        int failures = 0;

        var client = new BrokerClient();
        var clock = Stopwatch.StartNew();

        BrokerResponse response = await client
            .SendAsync(BrokerOperation.Ping, [])
            .ConfigureAwait(false);

        clock.Stop();

        Console.WriteLine($"    {response.Error}");
        Console.WriteLine($"    answered in {clock.ElapsedMilliseconds} ms");

        failures += Check("an absent broker is a failure, not a hang", !response.Ok);

        // A ten-second stall for the ordinary case would look like a broken application.
        failures += Check("and it answers within a few seconds", clock.Elapsed.TotalSeconds < 6);

        // The INTENT, not the wording.
        //
        // This used to assert the literal text "--mode service", and that pinned the one part
        // of the sentence that turned out to be wrong: Shellvis.Setup.exe is a developer path,
        // and nobody who installs from a release has that file. Telling them to run it sent
        // them looking for something they do not have. Changing the message therefore broke a
        // check that was, on its face, still describing what the message should do.
        //
        // So the check now asks the two things that actually matter and neither of which is a
        // phrase: does it point at installing, and does it avoid naming the developer binary.
        failures += Check(
            "the message says how to get one",
            response.Error?.Contains("install", StringComparison.OrdinalIgnoreCase) == true);

        failures += Check(
            "and does not send the reader after a file they do not have",
            response.Error?.Contains("Shellvis.Setup", StringComparison.OrdinalIgnoreCase) == false);

        failures += Check("IsAvailable reports false", !await client.IsAvailableAsync().ConfigureAwait(false));

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> HandshakeAsync(BrokerClient client)
    {
        Console.WriteLine("-- handshake --");
        int failures = 0;

        BrokerResponse ping = await client.SendAsync(BrokerOperation.Ping, []).ConfigureAwait(false);

        Console.WriteLine("    " + ping.Output);

        failures += Check("ping succeeds", ping.Ok);
        failures += Check("it reports the protocol version", ping.Output.Contains($"v{BrokerProtocol.Version}"));

        // A broker without privilege answers every call and fails each one. Saying so up
        // front is the difference between "started the wrong way" and "broken machine".
        failures += Check("it reports whether it is elevated", ping.Output.Contains("elevated:"));
        failures += Check("it reports which account it runs as", ping.Output.Contains(@"\"));

        // The whole point of the design: a script runs with the BROKER's rights, which is
        // how the interactive app can hold none.
        BrokerResponse who = await client.SendAsync(
            BrokerOperation.RunElevated,
            new Dictionary<string, string> { ["script"] = "[Security.Principal.WindowsIdentity]::GetCurrent().Name" })
            .ConfigureAwait(false);

        Console.WriteLine("    script ran as: " + who.Output.ReplaceLineEndings(" ").Trim());

        failures += Check("a script runs and returns output", who.Ok && who.Output.Contains("exit code 0"));

        BrokerResponse timedOut = await client.SendAsync(
            BrokerOperation.RunElevated,
            new Dictionary<string, string>
            {
                ["script"] = "Start-Sleep -Seconds 30",
                ["timeoutSeconds"] = "2",
            }).ConfigureAwait(false);

        Console.WriteLine("    " + timedOut.Error);

        failures += Check(
            "a script that overruns its timeout is killed and reported",
            !timedOut.Ok && timedOut.Error?.Contains("did not finish") == true);

        BrokerResponse empty = await client.SendAsync(
            BrokerOperation.RunElevated,
            new Dictionary<string, string> { ["script"] = "  " }).ConfigureAwait(false);

        failures += Check("an empty script is refused", !empty.Ok);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> RegistryGuardsAsync(BrokerClient client)
    {
        Console.WriteLine("-- registry guards --");
        int failures = 0;

        BrokerResponse read = await client.SendAsync(
            BrokerOperation.RegistryRead,
            new Dictionary<string, string>
            {
                ["path"] = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                ["name"] = "ProductName",
            }).ConfigureAwait(false);

        Console.WriteLine("    " + read.Output.Trim());
        failures += Check("a permitted HKLM read works", read.Ok && read.Output.Contains("Windows"));

        // The forbidden list is not about approval: these are persistence and
        // security-policy locations, and an approval dialog showing a long path is not
        // reliable protection.
        (string Path, string Why)[] forbidden =
        [
            (@"HKLM\SYSTEM\CurrentControlSet\Services\Anything", "service definitions"),
            (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "autostart"),
            (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "logon"),
            (@"HKLM\SOFTWARE\Microsoft\Windows Defender\X", "Defender"),
            (@"HKLM\SOFTWARE\Policies\Anything", "policy"),
            (@"HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot", "safe boot"),
        ];

        foreach ((string path, string why) in forbidden)
        {
            BrokerResponse refused = await client.SendAsync(
                BrokerOperation.RegistryWrite,
                new Dictionary<string, string>
                {
                    ["path"] = path,
                    ["name"] = "probe",
                    ["value"] = "1",
                }).ConfigureAwait(false);

            failures += Check($"{why} is refused", !refused.Ok);
        }

        // Reading is refused there too. A model that can enumerate autostart entries has
        // been handed a reconnaissance tool, and there is no reason the broker should be
        // the one to provide it.
        BrokerResponse forbiddenRead = await client.SendAsync(
            BrokerOperation.RegistryRead,
            new Dictionary<string, string>
            {
                ["path"] = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            }).ConfigureAwait(false);

        failures += Check("and the same paths are refused for reading", !forbiddenRead.Ok);

        // Traversal, checked after normalising: a prefix test alone would let
        // SOFTWARE\..\SYSTEM\... through.
        BrokerResponse traversal = await client.SendAsync(
            BrokerOperation.RegistryWrite,
            new Dictionary<string, string>
            {
                ["path"] = @"HKLM\SOFTWARE\..\SYSTEM\CurrentControlSet\Services\X",
                ["name"] = "probe",
                ["value"] = "1",
            }).ConfigureAwait(false);

        Console.WriteLine("    " + traversal.Error);
        failures += Check("a traversal attempt is refused", !traversal.Ok);

        BrokerResponse hkcu = await client.SendAsync(
            BrokerOperation.RegistryWrite,
            new Dictionary<string, string>
            {
                ["path"] = @"HKCU\SOFTWARE\Shellvis",
                ["name"] = "probe",
                ["value"] = "1",
            }).ConfigureAwait(false);

        // HKCU is not on the allowlist: the interactive app can already write it, so
        // routing it through a privileged service adds risk and no capability.
        failures += Check("HKCU is not reachable through the broker", !hkcu.Ok);

        BrokerResponse bogus = await client.SendAsync(
            BrokerOperation.RegistryRead,
            new Dictionary<string, string> { ["path"] = "NOTAHIVE\\Something" }).ConfigureAwait(false);

        failures += Check(
            "an unknown hive is refused with the supported list",
            !bogus.Ok && bogus.Error?.Contains("HKLM") == true);

        BrokerResponse rootOnly = await client.SendAsync(
            BrokerOperation.RegistryRead,
            new Dictionary<string, string> { ["path"] = "HKLM" }).ConfigureAwait(false);

        failures += Check("a hive root alone is refused", !rootOnly.Ok);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> SelfPreservationAsync(BrokerClient client)
    {
        Console.WriteLine("-- self preservation --");
        int failures = 0;

        // If the agent can stop its own broker, a failed operation becomes an
        // unrecoverable one: the interactive app can no longer ask for the privilege
        // needed to start it again.
        BrokerResponse ownService = await client.SendAsync(
            BrokerOperation.ServiceControl,
            new Dictionary<string, string>
            {
                ["service"] = BrokerProtocol.ServiceName,
                ["action"] = "stop",
            }).ConfigureAwait(false);

        Console.WriteLine("    " + ownService.Error);

        failures += Check(
            "the broker refuses to control its own service",
            !ownService.Ok && ownService.Error?.Contains("own service") == true);

        BrokerResponse viaScript = await client.SendAsync(
            BrokerOperation.RunElevated,
            new Dictionary<string, string>
            {
                ["script"] = $"Stop-Service {BrokerProtocol.ServiceName}",
            }).ConfigureAwait(false);

        // The script path is the obvious way around the service check, so it is closed
        // too. A guard that only covers the front door is not a guard.
        failures += Check(
            "and refuses a script that names it",
            !viaScript.Ok && viaScript.Error?.Contains(BrokerProtocol.ServiceName) == true);

        (string Script, string Why)[] dangerous =
        [
            ("Set-MpPreference -DisableRealtimeMonitoring $true", "disabling Defender"),
            ("bcdedit /set testsigning on", "boot configuration"),
            ("vssadmin delete shadows /all", "shadow copies"),
            ("Set-ExecutionPolicy Bypass -Scope LocalMachine", "execution policy"),
            ("netsh advfirewall set allprofiles state off", "firewall off"),
            ("Format-Volume -DriveLetter X", "formatting"),
        ];

        foreach ((string script, string why) in dangerous)
        {
            BrokerResponse refused = await client.SendAsync(
                BrokerOperation.RunElevated,
                new Dictionary<string, string> { ["script"] = script }).ConfigureAwait(false);

            failures += Check($"{why} is refused", !refused.Ok);
        }

        // Case is not a bypass: the check normalises first.
        BrokerResponse upper = await client.SendAsync(
            BrokerOperation.RunElevated,
            new Dictionary<string, string> { ["script"] = "BCDEDIT /SET TESTSIGNING ON" })
            .ConfigureAwait(false);

        failures += Check("and uppercase does not evade the check", !upper.Ok);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> ServicesAsync(BrokerClient client)
    {
        Console.WriteLine("-- services --");
        int failures = 0;

        BrokerResponse listed = await client.SendAsync(
            BrokerOperation.ServiceList,
            new Dictionary<string, string> { ["filter"] = "spooler" }).ConfigureAwait(false);

        Console.WriteLine("    " + listed.Output.ReplaceLineEndings(" ").Trim());

        failures += Check("a filtered service list works", listed.Ok);

        BrokerResponse status = await client.SendAsync(
            BrokerOperation.ServiceControl,
            new Dictionary<string, string> { ["service"] = "Spooler", ["action"] = "status" })
            .ConfigureAwait(false);

        Console.WriteLine("    " + (status.Ok ? status.Output : status.Error));

        failures += Check("querying a real service works", status.Ok);

        BrokerResponse missing = await client.SendAsync(
            BrokerOperation.ServiceControl,
            new Dictionary<string, string> { ["service"] = "NoSuchServiceXyz", ["action"] = "status" })
            .ConfigureAwait(false);

        failures += Check(
            "an unknown service is reported clearly",
            !missing.Ok && missing.Error?.Contains("no service called") == true);

        BrokerResponse badAction = await client.SendAsync(
            BrokerOperation.ServiceControl,
            new Dictionary<string, string> { ["service"] = "Spooler", ["action"] = "obliterate" })
            .ConfigureAwait(false);

        failures += Check("an unknown action is refused", !badAction.Ok);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> ToolSurfaceAsync(BrokerClient client)
    {
        Console.WriteLine("-- tool surface --");
        int failures = 0;

        var registry = new ToolRegistry();
        registry.RegisterFrom(new BrokerTools(client));

        failures += Check("six broker tools register", registry.Count == 6);

        // Every one of them, including the reads. What makes these different is not what
        // they do but where they run: as LocalSystem. "The agent asked the privileged
        // service for something" deserves a human's attention even when harmless, and a
        // "do not ask again" answer must never cover this boundary.
        failures += Check(
            "every broker tool is AlwaysAsk, including the read-only ones",
            registry.Tools.All(t => t.SideEffect == SideEffect.AlwaysAsk));

        var tools = new BrokerTools(client);

        string status = await tools.Status().ConfigureAwait(false);
        failures += Check("broker_status reports through the tool surface", status.Contains("broker v"));

        string refused = await tools.ServiceControl(BrokerProtocol.ServiceName, "stop")
            .ConfigureAwait(false);

        Console.WriteLine("    " + refused);

        // A refusal must not read like output, or the model reports the action as done.
        failures += Check(
            "a refusal is prefixed so it cannot be mistaken for a result",
            refused.StartsWith("The broker did not do this:"));

        Console.WriteLine();
        return failures;
    }

    private static async Task<bool> WaitForBrokerAsync(BrokerClient client)
    {
        for (int i = 0; i < 40; i++)
        {
            if (await client.IsAvailableAsync().ConfigureAwait(false))
                return true;

            await Task.Delay(250).ConfigureAwait(false);
        }

        return false;
    }

    private static Process? Start(string exe)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--console");

        Process? process = Process.Start(startInfo);

        // Drained so the broker never blocks on a full stdout pipe, which would look
        // like the broker hanging.
        _ = process?.StandardOutput.ReadToEndAsync();
        _ = process?.StandardError.ReadToEndAsync();

        return process;
    }

    private static string FindBroker()
    {
        // Walk up from the probe's own output directory: the two projects sit side by
        // side under src/ and tools/, and hard-coding a relative depth breaks whenever
        // the configuration or TFM changes.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "src", "Shellvis.Broker", "bin");

            if (Directory.Exists(candidate))
            {
                string[] found = Directory.GetFiles(
                    candidate, "Shellvis.Broker.exe", SearchOption.AllDirectories);

                if (found.Length > 0)
                    return found.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }

            directory = directory.Parent;
        }

        return string.Empty;
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        return ok ? 0 : 1;
    }
}
