using System.Security.Principal;
using Shellvis.Contracts;

namespace Shellvis.Broker;

/// <summary>
/// Entry point for the privileged half.
///
/// The same executable runs as a Windows service and as a console process. That is not a
/// convenience: a service cannot be debugged or smoke-tested without either an
/// installation and an elevated prompt, or a way to run it in the foreground. The console
/// mode is how the protocol and the pipe ACL get exercised on a machine where nobody has
/// the rights to register a service.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool console = args.Contains("--console", StringComparer.OrdinalIgnoreCase);

        string? allowedSid = ReadArgument(args, "--allow-sid")
            // A NAME rather than a SID, for the MSI.
            //
            // Windows Installer exposes the installing user's name as [LogonUser] and does
            // not expose their SID at all, so an MSI can only pass a SID by way of a custom
            // action -- native code shipped inside the package, for one lookup. Resolving
            // the name here instead is a few lines and keeps the package free of custom
            // actions entirely.
            ?? Resolve(ReadArgument(args, "--allow-user"))
            ?? Environment.GetEnvironmentVariable("SHELLVIS_BROKER_ALLOW_SID")
            // Default to the account the broker is started under. Correct in console
            // mode, and correct in service mode only if the installer wrote the
            // installing user's SID -- which is why the installer passes it explicitly.
            ?? CurrentUserSid();

        using var stopping = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        void Log(string message)
        {
            string line = $"{DateTimeOffset.Now:HH:mm:ss} {message}";

            if (console)
                Console.WriteLine(line);

            Append(line);
        }

        if (!console && !IsElevated())
        {
            // Said plainly at startup rather than discovered per request. A broker without
            // privilege answers every call and fails each one, which reads like a broken
            // machine instead of a broker started the wrong way.
            Log("WARNING: the broker is not elevated. Privileged operations will fail.");
        }

        var server = new BrokerServer(allowedSid, Log);

        try
        {
            await server.RunAsync(stopping.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Turn an account name into a SID, or null if it does not resolve.
    ///
    /// Null rather than a throw, and null rather than a fallback to some other account: the
    /// SID decides who may talk to a LocalSystem service, so a name that cannot be resolved
    /// has to fall through to the next source rather than quietly granting somebody else.
    /// </summary>
    private static string? Resolve(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
            return null;

        try
        {
            var name = new NTAccount(account.Trim());
            return ((SecurityIdentifier)name.Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return null;
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

    private static bool IsElevated()
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

    /// <summary>
    /// Append to the broker's log file.
    ///
    /// Under ProgramData, not the user profile: the service runs as LocalSystem and has
    /// no user profile to write into. Failures are swallowed -- a broker that dies because
    /// it could not write a log line would be worse than one that loses the line.
    /// </summary>
    private static void Append(string line)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Shellvis");

            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, $"broker-{DateTimeOffset.Now:yyyyMMdd}.log"),
                line + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }
}
