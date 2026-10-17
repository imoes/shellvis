using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using Shellvis.Contracts;

namespace Shellvis.Broker;

/// <summary>
/// Carries out the operations.
///
/// Every method validates its own arguments rather than trusting the caller, even though
/// the caller is Shellvis' own shell. The shell runs a language model that composes these
/// arguments, so "our own client" is not the same as "a trusted input": the values
/// crossing this boundary were written by a model reacting to text it read somewhere.
/// </summary>
public sealed class BrokerOperations(Action<string> log)
{
    /// <summary>
    /// Registry roots the broker will touch.
    ///
    /// An allowlist, because the point of the broker is HKLM. Letting it write HKCU would
    /// add nothing -- the interactive app can already do that itself -- while widening
    /// what a compromised shell could reach.
    /// </summary>
    private static readonly Dictionary<string, RegistryHive> Hives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HKLM"] = RegistryHive.LocalMachine,
        ["HKEY_LOCAL_MACHINE"] = RegistryHive.LocalMachine,
        ["HKCR"] = RegistryHive.ClassesRoot,
        ["HKEY_CLASSES_ROOT"] = RegistryHive.ClassesRoot,
    };

    /// <summary>
    /// Registry paths that are refused outright.
    ///
    /// These are the places where a write is not a configuration change but a persistence
    /// mechanism or a security downgrade. A model that has been talked into "just add this
    /// registry key" by a web page is exactly the scenario, and an approval prompt showing
    /// a long path is not reliable protection -- so these are simply not available.
    /// </summary>
    private static readonly string[] ForbiddenPaths =
    [
        @"SYSTEM\CurrentControlSet\Services",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
        @"SOFTWARE\Microsoft\Windows Defender",
        @"SYSTEM\CurrentControlSet\Control\SafeBoot",
        @"SOFTWARE\Policies",
    ];

    public async Task<BrokerResponse> ExecuteAsync(
        BrokerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Operation switch
            {
                BrokerOperation.Ping => Ping(),
                BrokerOperation.RunElevated =>
                    await RunElevatedAsync(request, cancellationToken).ConfigureAwait(false),
                BrokerOperation.ServiceControl => ServiceControl(request),
                BrokerOperation.ServiceList => ServiceList(request),
                BrokerOperation.RegistryRead => RegistryRead(request),
                BrokerOperation.RegistryWrite => RegistryWrite(request),
                _ => BrokerResponse.Failed($"operation {request.Operation} is not implemented."),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The type as well as the message: an UnauthorizedAccessException from the
            // broker means the SERVICE lacks the right, which is a different problem from
            // the request being wrong, and the reader needs to be able to tell.
            return BrokerResponse.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static BrokerResponse Ping()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

        var principal = new System.Security.Principal.WindowsPrincipal(identity);

        bool elevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

        // Says plainly whether it actually has privilege. A broker running as the plain
        // user answers requests and then fails them one by one, which reads like a broken
        // machine rather than a broker started the wrong way.
        return BrokerResponse.Succeeded(
            $"broker v{BrokerProtocol.Version} running as {identity.Name}, "
            + $"elevated: {elevated}, session {Process.GetCurrentProcess().SessionId}");
    }

    /// <summary>
    /// Run a PowerShell script with the broker's rights.
    ///
    /// Out-of-process against the installed Windows PowerShell rather than hosting a
    /// runspace: the broker should carry as little as possible, and a script that hangs
    /// must be killable as a process tree rather than needing a runspace to co-operate.
    /// </summary>
    private async Task<BrokerResponse> RunElevatedAsync(
        BrokerRequest request, CancellationToken cancellationToken)
    {
        string? script = request.Get("script");

        if (string.IsNullOrWhiteSpace(script))
            return BrokerResponse.Failed("no script was given.");

        if (SelfPreservation(script) is { } refusal)
            return BrokerResponse.Failed(refusal);

        int timeout = int.TryParse(request.Get("timeoutSeconds"), out int seconds)
            ? Math.Clamp(seconds, 1, 600)
            : 120;

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        // Not Bypass. A broker that ran unsigned script from anywhere would defeat
        // whatever policy the machine's owner chose; the caller can sign or inline.
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("RemoteSigned");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = new Process { StartInfo = startInfo };

        process.Start();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            return BrokerResponse.Failed($"the script did not finish within {timeout}s and was killed.");
        }

        string output = await stdout.ConfigureAwait(false);
        string errors = await stderr.ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append("exit code ").Append(process.ExitCode).AppendLine();

        if (output.Length > 0)
            sb.AppendLine(output.TrimEnd());

        if (errors.Length > 0)
            sb.AppendLine("stderr:").AppendLine(errors.TrimEnd());

        return new BrokerResponse(process.ExitCode == 0, sb.ToString().TrimEnd(), null);
    }

    /// <summary>
    /// Refuse anything that would disable the broker or the machine's defences.
    ///
    /// Self-preservation is not vanity: if the agent can stop its own broker, a failed
    /// operation becomes an unrecoverable one -- the interactive app can no longer ask for
    /// the privilege needed to start it again, and the user is left with an application
    /// that reports a missing service and cannot fix it.
    /// </summary>
    private static string? SelfPreservation(string script)
    {
        string flat = script.ToLowerInvariant();

        if (flat.Contains(BrokerProtocol.ServiceName.ToLowerInvariant()))
        {
            return $"refused: the script names the {BrokerProtocol.ServiceName} service. "
                + "The broker will not modify or stop itself -- use Windows' own service "
                + "management for that.";
        }

        (string Pattern, string Why)[] forbidden =
        [
            ("set-mppreference", "disabling Defender"),
            ("add-mppreference -exclusionpath", "adding a Defender exclusion"),
            ("bcdedit", "changing boot configuration"),
            ("vssadmin delete", "deleting shadow copies"),
            ("set-executionpolicy", "changing execution policy machine-wide"),
            ("netsh advfirewall set allprofiles state off", "turning the firewall off"),
            ("cipher /w", "wiping free space"),
            ("format-volume", "formatting a volume"),
        ];

        foreach ((string pattern, string why) in forbidden)
        {
            if (flat.Contains(pattern))
            {
                // Named rather than silently stripped: the model must learn that the
                // action did not happen, or it reports success.
                return $"refused: {why} is not available through the broker.";
            }
        }

        return null;
    }

    private BrokerResponse ServiceControl(BrokerRequest request)
    {
        string? name = request.Get("service");
        string? action = request.Get("action")?.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(name))
            return BrokerResponse.Failed("no service name was given.");

        if (name.Equals(BrokerProtocol.ServiceName, StringComparison.OrdinalIgnoreCase))
        {
            return BrokerResponse.Failed(
                "refused: the broker will not control its own service. Stopping it would "
                + "leave Shellvis unable to ask for the privilege needed to start it again.");
        }

        if (action is not ("start" or "stop" or "restart" or "status"))
            return BrokerResponse.Failed("action must be start, stop, restart or status.");

        using var controller = new ServiceController(name);

        // Reading Status on a name that does not exist throws here rather than at
        // construction, which is why the check is a read and not a lookup.
        ServiceControllerStatus before;

        try
        {
            before = controller.Status;
        }
        catch (InvalidOperationException)
        {
            return BrokerResponse.Failed($"there is no service called '{name}'.");
        }

        if (action == "status")
            return BrokerResponse.Succeeded($"{controller.DisplayName} ({name}) is {before}.");

        var wait = TimeSpan.FromSeconds(30);

        switch (action)
        {
            case "stop" or "restart" when before != ServiceControllerStatus.Stopped:
                if (!controller.CanStop)
                    return BrokerResponse.Failed($"{name} does not accept a stop request.");

                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, wait);
                break;
        }

        if (action is "start" or "restart")
        {
            controller.Refresh();

            if (controller.Status != ServiceControllerStatus.Running)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, wait);
            }
        }

        controller.Refresh();
        log($"service {name}: {before} -> {controller.Status}");

        return BrokerResponse.Succeeded($"{name}: {before} -> {controller.Status}");
    }

    private static BrokerResponse ServiceList(BrokerRequest request)
    {
        string? filter = request.Get("filter");

        ServiceController[] services = ServiceController.GetServices();

        IEnumerable<ServiceController> matches = services;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            matches = matches.Where(s =>
                s.ServiceName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || s.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var sb = new StringBuilder();
        List<ServiceController> list = [.. matches.OrderBy(s => s.ServiceName, StringComparer.OrdinalIgnoreCase)];

        sb.Append(list.Count).Append(" of ").Append(services.Length).AppendLine(" service(s):");

        foreach (ServiceController service in list.Take(200))
            sb.Append("  ").Append(service.ServiceName).Append("  ").Append(service.Status)
                .Append("  ").AppendLine(service.DisplayName);

        foreach (ServiceController service in services)
            service.Dispose();

        return BrokerResponse.Succeeded(sb.ToString());
    }

    private static BrokerResponse RegistryRead(BrokerRequest request)
    {
        if (!TryResolveKey(request, out RegistryHive hive, out string path, out string? problem))
            return BrokerResponse.Failed(problem!);

        string? name = request.Get("name");

        using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using RegistryKey? key = root.OpenSubKey(path);

        if (key is null)
            return BrokerResponse.Failed($"the key {path} does not exist.");

        if (string.IsNullOrEmpty(name))
        {
            var sb = new StringBuilder();
            sb.Append(path).AppendLine(":");

            foreach (string value in key.GetValueNames())
                sb.Append("  ").Append(value.Length == 0 ? "(default)" : value)
                    .Append(" = ").AppendLine(key.GetValue(value)?.ToString());

            foreach (string sub in key.GetSubKeyNames())
                sb.Append("  [").Append(sub).AppendLine("]");

            return BrokerResponse.Succeeded(sb.ToString());
        }

        object? single = key.GetValue(name);

        return single is null
            ? BrokerResponse.Failed($"{path} has no value called '{name}'.")
            : BrokerResponse.Succeeded($"{name} = {single}");
    }

    private BrokerResponse RegistryWrite(BrokerRequest request)
    {
        if (!TryResolveKey(request, out RegistryHive hive, out string path, out string? problem))
            return BrokerResponse.Failed(problem!);

        string? name = request.Get("name");
        string? value = request.Get("value");

        if (string.IsNullOrEmpty(name))
            return BrokerResponse.Failed("a value name is required.");

        if (value is null)
            return BrokerResponse.Failed("a value is required.");

        using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using RegistryKey key = root.CreateSubKey(path, writable: true);

        string kind = request.Get("kind")?.ToLowerInvariant() ?? "string";

        object typed = kind switch
        {
            "dword" when int.TryParse(value, out int number) => number,
            "dword" => throw new ArgumentException($"'{value}' is not a DWORD."),
            _ => value,
        };

        key.SetValue(name, typed, kind == "dword" ? RegistryValueKind.DWord : RegistryValueKind.String);

        log($"registry write {path}\\{name} = {value}");

        return BrokerResponse.Succeeded($"wrote {path}\\{name} = {value} ({kind}).");
    }

    /// <summary>
    /// Resolve and vet a registry target.
    ///
    /// Traversal is checked after normalising, because <c>SOFTWARE\..\SYSTEM\…</c> would
    /// otherwise pass a prefix check and land somewhere else entirely.
    /// </summary>
    private static bool TryResolveKey(
        BrokerRequest request, out RegistryHive hive, out string path, out string? problem)
    {
        hive = RegistryHive.LocalMachine;
        path = string.Empty;
        problem = null;

        string? raw = request.Get("path");

        if (string.IsNullOrWhiteSpace(raw))
        {
            problem = "a registry path is required, for example HKLM\\SOFTWARE\\Shellvis.";
            return false;
        }

        string[] parts = raw.Replace('/', '\\').Split('\\', 2, StringSplitOptions.TrimEntries);

        if (parts.Length != 2 || !Hives.TryGetValue(parts[0], out hive))
        {
            problem = $"'{raw}' does not start with a supported hive. "
                + $"Supported: {string.Join(", ", Hives.Keys.Distinct())}.";

            return false;
        }

        path = parts[1].Trim('\\');

        if (path.Length == 0)
        {
            problem = "the path names a hive root, which is not a key.";
            return false;
        }

        if (path.Contains("..", StringComparison.Ordinal))
        {
            problem = "the path contains '..', which is not a registry path component.";
            return false;
        }

        foreach (string forbidden in ForbiddenPaths)
        {
            if (path.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"refused: {forbidden} is not available through the broker. "
                    + "It is a persistence or security-policy location, not configuration.";

                return false;
            }
        }

        return true;
    }
}
