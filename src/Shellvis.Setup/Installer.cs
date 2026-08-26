using System.Reflection;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Shellvis.Contracts;

namespace Shellvis.Setup;

/// <summary>Which of the two installation modes.</summary>
public enum InstallMode
{
    /// <summary>Per user, no administrator rights, no privileged service.</summary>
    User,

    /// <summary>Machine-wide, with the broker registered as a Windows service.</summary>
    Service,
}

/// <summary>
/// Installs Shellvis in one of the two modes the requirements name.
///
/// A single-file console installer rather than a WiX/MSI toolchain. The deciding argument
/// is the user-mode case: it must work with no administrator rights and no installer
/// infrastructure, which an MSI per-user install can do only awkwardly. The service mode
/// then needs exactly three privileged actions -- copy into Program Files, register a
/// service, tighten ACLs -- and all three are a few lines each.
/// </summary>
public sealed class Installer(Action<string> log)
{
    /// <summary>Files that make up an installation.</summary>
    private static readonly string[] Patterns = ["*.dll", "*.exe", "*.json", "*.pri", "*.xbf"];

    public string DefaultTarget(InstallMode mode) => mode switch
    {
        InstallMode.User => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Shellvis"),

        _ => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Shellvis"),
    };

    /// <summary>Perform an installation.</summary>
    /// <param name="mode">Which mode.</param>
    /// <param name="source">Where the built files are.</param>
    /// <param name="target">Override for the destination, for testing.</param>
    /// <param name="autostart">Whether to add a startup entry.</param>
    public bool Install(InstallMode mode, string source, string? target = null, bool autostart = true)
    {
        if (mode == InstallMode.Service && !IsElevated())
        {
            // Refused up front rather than half-installed. A service install that copies
            // files and then fails at `sc create` leaves a directory nobody knows about
            // and no working installation.
            log("Service mode needs administrator rights. Right-click the installer and "
                + "choose 'Run as administrator', or install with --mode user instead "
                + "(everything works except operations that need elevation).");

            return false;
        }

        string destination = target ?? DefaultTarget(mode);

        if (!Directory.Exists(source))
        {
            log($"the source directory {source} does not exist.");
            return false;
        }

        log($"installing {mode} mode into {destination}");

        try
        {
            Directory.CreateDirectory(destination);
            int copied = CopyTree(source, destination);
            log($"copied {copied} file(s)");
        }
        catch (Exception ex)
        {
            log($"copy failed: {ex.Message}");
            return false;
        }

        if (mode == InstallMode.Service)
        {
            // ACLs before the service is registered: a window in which the service
            // binary sits in a world-writable directory is a window in which anyone can
            // replace what LocalSystem is about to run.
            if (!Harden(destination))
                return false;

            if (!RegisterService(destination))
                return false;
        }

        // Registered in both modes: Thunderbird integration needs no privilege, and the
        // host runs as whoever Thunderbird runs as.
        RegisterThunderbirdHost(destination);

        if (autostart)
            SetAutostart(mode, destination);

        log("done. " + (mode == InstallMode.Service
            ? $"The {BrokerProtocol.ServiceName} service is registered and running."
            : "No privileged service was installed; broker tools will report as unavailable."));

        return true;
    }

    private int CopyTree(string source, string destination)
    {
        int count = 0;

        foreach (string pattern in Patterns)
        {
            foreach (string file in Directory.GetFiles(source, pattern, SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string to = Path.Combine(destination, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(to)!);

                // The installer may be running from the directory it is copying, and it
                // must not fail on its own locked file.
                try
                {
                    File.Copy(file, to, overwrite: true);
                    count++;
                }
                catch (IOException) when (File.Exists(to))
                {
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Restrict the installation directory.
    ///
    /// The requirement this satisfies is the Windows equivalent of Hermes' 0700/0600:
    /// nothing that LocalSystem executes may be writable by a non-administrator. Program
    /// Files already inherits a suitable DACL, so this removes inheritance and states the
    /// intent explicitly rather than relying on the parent staying that way.
    /// </summary>
    private bool Harden(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            DirectorySecurity security = directory.GetAccessControl();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            foreach (SecurityIdentifier full in (SecurityIdentifier[])[system, administrators])
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    full,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            // Users may read and run, never write. The interactive shell runs as the
            // user, so it has to be able to start.
            security.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            directory.SetAccessControl(security);
            log("tightened the install directory: users may read and execute, not write");

            return true;
        }
        catch (Exception ex)
        {
            // Fatal on purpose. Continuing would register LocalSystem to run a binary in
            // a directory whose permissions are unknown.
            log($"could not set permissions on {path}: {ex.Message}. Aborting: a service "
                + "binary in a directory anyone can write is worse than no service.");

            return false;
        }
    }

    /// <summary>
    /// Register and start the broker service.
    ///
    /// The installing user's SID is baked into the command line, which is what the pipe
    /// ACL is built from. Without it the service would grant only administrators and the
    /// interactive shell -- running as the ordinary user -- could not reach it, which
    /// presents as "the service is not running" while it is running perfectly.
    /// </summary>
    private bool RegisterService(string destination)
    {
        string exe = Path.Combine(destination, "Shellvis.Broker.exe");

        if (!File.Exists(exe))
        {
            log($"{exe} is missing; the broker was not built.");
            return false;
        }

        string sid = CurrentUserSid() ?? string.Empty;

        if (sid.Length == 0)
            log("WARNING: could not determine the installing user's SID; only administrators "
                + "will be able to reach the broker.");

        // binPath is one string to sc.exe and its quoting is notoriously strict: the
        // whole value is quoted, the exe path inside it too, and there must be a space
        // after binPath=.
        string binPath = $"\"\\\"{exe}\\\" --allow-sid {sid}\"";

        if (!Run("sc.exe", $"create {BrokerProtocol.ServiceName} binPath= {binPath} "
            + $"start= auto DisplayName= \"Shellvis Broker\""))
        {
            return false;
        }

        Run("sc.exe", $"description {BrokerProtocol.ServiceName} "
            + "\"Carries out the privileged operations Shellvis asks for. Runs no UI.\"");

        // Restart twice with a delay, then give up. A broker that crash-loops forever
        // would fill the event log; one that never restarts leaves the user with a dead
        // feature and no hint.
        Run("sc.exe", $"failure {BrokerProtocol.ServiceName} reset= 86400 actions= restart/60000/restart/60000//");

        if (!Run("sc.exe", $"start {BrokerProtocol.ServiceName}"))
        {
            log("the service was registered but did not start. Check "
                + @"%ProgramData%\Shellvis for its log.");

            return false;
        }

        return true;
    }

    /// <summary>
    /// Make Thunderbird able to find the native messaging host.
    ///
    /// Two things have to be in place and both are easy to get wrong silently. The
    /// manifest must carry the ABSOLUTE path to the host executable -- a stale path means
    /// the extension simply never connects, with no error anywhere. And Thunderbird finds
    /// the manifest through a registry key whose *default value* is the manifest's path,
    /// not the manifest itself.
    ///
    /// Written per user under HKCU, because that is where a per-user Thunderbird looks and
    /// because it needs no privilege -- so this works identically in both install modes.
    /// </summary>
    private void RegisterThunderbirdHost(string destination)
    {
        string host = Path.Combine(destination, "Shellvis.Thunderbird.Host.exe");

        if (!File.Exists(host))
        {
            log("Thunderbird bridge: the host executable is not in this build; skipped.");
            return;
        }

        try
        {
            string manifestDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Shellvis");

            Directory.CreateDirectory(manifestDirectory);

            string manifestPath = Path.Combine(
                manifestDirectory, ThunderbirdProtocol.HostName + ".json");

            File.WriteAllText(
                manifestPath,
                ThunderbirdProtocol.BuildManifest(host, "shellvis-bridge@ippen.media"));

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                ThunderbirdProtocol.ManifestRegistryKey);

            // The default value, not a named one. A named value is ignored and the
            // failure is silence.
            key.SetValue(string.Empty, manifestPath, RegistryValueKind.String);

            log($"Thunderbird bridge registered ({manifestPath}). Install the extension "
                + @"from ext\thunderbird-bridge to enable mail tools.");
        }
        catch (Exception ex)
        {
            // Not fatal: everything else works without Thunderbird.
            log($"could not register the Thunderbird bridge: {ex.Message}");
        }
    }

    private void SetAutostart(InstallMode mode, string destination)
    {
        string exe = Path.Combine(destination, "Shellvis.Shell.exe");

        try
        {
            // HKCU in both modes. Even in service mode the interactive shell is
            // per-user: it starts in the user's session and connects to the service, and
            // an HKLM Run entry would launch a copy for every account that logs in.
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);

            key?.SetValue("Shellvis", $"\"{exe}\"");
            log("added a startup entry for this user");
        }
        catch (Exception ex)
        {
            // Not fatal: an installation without autostart is still an installation.
            log($"could not set autostart: {ex.Message}");
        }
    }

    /// <summary>Remove an installation.</summary>
    public bool Uninstall(InstallMode mode, string? target = null)
    {
        string destination = target ?? DefaultTarget(mode);

        if (mode == InstallMode.Service)
        {
            if (!IsElevated())
            {
                log("removing the service needs administrator rights.");
                return false;
            }

            Run("sc.exe", $"stop {BrokerProtocol.ServiceName}");
            Run("sc.exe", $"delete {BrokerProtocol.ServiceName}");
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);

            key?.DeleteValue("Shellvis", throwOnMissingValue: false);

            // The native messaging registration too, or Thunderbird keeps launching a
            // host executable that is no longer there.
            Registry.CurrentUser.DeleteSubKeyTree(
                ThunderbirdProtocol.ManifestRegistryKey, throwOnMissingSubKey: false);

            // And the manifest itself. Inert without the registry key, but it names a
            // path that no longer exists, and a leftover file that looks like
            // configuration is the kind of thing someone debugs for an hour later.
            string manifest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Shellvis",
                ThunderbirdProtocol.HostName + ".json");

            if (File.Exists(manifest))
                File.Delete(manifest);
        }
        catch (Exception)
        {
        }

        if (Directory.Exists(destination))
        {
            try
            {
                Directory.Delete(destination, recursive: true);
                log($"removed {destination}");
            }
            catch (Exception ex)
            {
                log($"could not remove {destination}: {ex.Message}. Close Shellvis and retry.");
                return false;
            }
        }

        // Configuration and history are left alone. Deleting someone's conversation
        // history as part of an uninstall would be a surprise, and reinstalling is the
        // common reason to uninstall.
        log($"configuration and history under {"%USERPROFILE%\\.shellvis"} were left in place.");

        return true;
    }

    /// <summary>Report what is installed, without changing anything.</summary>
    public string Status()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Shellvis {OwnVersion()}");
        sb.AppendLine($"elevated: {IsElevated()}");

        foreach (InstallMode mode in (InstallMode[])[InstallMode.User, InstallMode.Service])
        {
            string path = DefaultTarget(mode);
            bool present = File.Exists(Path.Combine(path, "Shellvis.Shell.exe"));
            sb.AppendLine($"{mode,-8} {(present ? "installed" : "not installed")}  {path}");
        }

        sb.AppendLine($"service:  {ServiceState()}");

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");

            sb.AppendLine($"autostart: {key?.GetValue("Shellvis") ?? "not set"}");
        }
        catch (Exception)
        {
        }

        return sb.ToString();
    }

    private static string ServiceState()
    {
        try
        {
            var startInfo = new ProcessStartInfo("sc.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("query");
            startInfo.ArgumentList.Add(BrokerProtocol.ServiceName);

            using Process? process = Process.Start(startInfo);

            if (process is null)
                return "unknown";

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            if (process.ExitCode != 0)
                return "not registered";

            foreach (string line in output.Split('\n'))
            {
                if (line.Contains("STATE", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("STATUS", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Trim();
                }
            }

            return "registered";
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.Message})";
        }
    }

    private bool Run(string exe, string arguments)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            // A raw argument string, because sc.exe's binPath= quoting does not survive
            // .NET's argument escaping -- the same trap the hook runner hit with cmd.
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process? process = Process.Start(startInfo);

        if (process is null)
        {
            log($"could not start {exe}");
            return false;
        }

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        if (process.ExitCode != 0)
        {
            log($"{exe} {arguments.Split(' ')[0]} failed ({process.ExitCode}): {output.Trim()}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// This installer's own version.
    ///
    /// Read from its own assembly rather than from Shellvis.Core, deliberately. The number
    /// comes from the same Directory.Build.props either way, and referencing Core for one
    /// string would pull the PowerShell SDK, FlaUI and everything else into what is meant
    /// to be a single-file installer.
    /// </summary>
    private static string OwnVersion()
    {
        string? informational = typeof(Installer).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (informational is not { Length: > 0 })
            return "unknown";

        int plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }

    public static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? CurrentUserSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
