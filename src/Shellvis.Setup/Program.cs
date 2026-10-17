namespace Shellvis.Setup;

/// <summary>
/// The installer's command line.
///
/// Deliberately explicit: there is no interactive default that picks a mode, because the
/// two modes differ in whether a service runs as LocalSystem on the machine. That is not
/// a decision to make on someone's behalf by guessing from whether the prompt happened to
/// be elevated.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var installer = new Installer(Console.WriteLine);

        string? command = args.FirstOrDefault();

        if (command is null or "--help" or "-h" or "/?")
        {
            Usage();
            return 0;
        }

        string? target = Value(args, "--target");
        bool noAutostart = args.Contains("--no-autostart", StringComparer.OrdinalIgnoreCase);

        switch (command.ToLowerInvariant())
        {
            case "--status":
                Console.Write(installer.Status());
                return 0;

            case "--mode":
            {
                string? mode = Value(args, "--mode");

                if (!TryMode(mode, out InstallMode parsed))
                {
                    Console.WriteLine($"'{mode}' is not a mode. Use user or service.");
                    return 2;
                }

                string source = Value(args, "--source") ?? AppContext.BaseDirectory;

                return installer.Install(parsed, source, target, !noAutostart) ? 0 : 1;
            }

            // Run from the last page of the .msi, in the session that clicked Finish. See
            // ModelFetch for why it is here and not inside the install transaction.
            case "--fetch-model":
                return ModelFetch.RunAsync(Value(args, "--fetch-model")).GetAwaiter().GetResult();

            case "--uninstall":
            {
                string? mode = Value(args, "--uninstall");

                if (!TryMode(mode, out InstallMode parsed))
                {
                    Console.WriteLine($"'{mode}' is not a mode. Use --uninstall user or --uninstall service.");
                    return 2;
                }

                return installer.Uninstall(parsed, target) ? 0 : 1;
            }

            default:
                Console.WriteLine($"unknown option '{command}'.");
                Usage();
                return 2;
        }
    }

    private static bool TryMode(string? text, out InstallMode mode)
    {
        mode = InstallMode.User;

        return text?.ToLowerInvariant() switch
        {
            "user" => true,
            "service" => (mode = InstallMode.Service) == InstallMode.Service,
            _ => false,
        };
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void Usage()
    {
        Console.WriteLine("""
        Shellvis installer.

          --mode user             Install for the current user. No administrator rights
                                  needed, and no privileged service: everything works
                                  except operations that require elevation.
                                  -> %LOCALAPPDATA%\Programs\Shellvis

          --mode service          Install machine-wide and register the broker as a
                                  Windows service running as LocalSystem. Requires
                                  administrator rights. The interactive app still runs
                                  as the user and asks the service for the few things
                                  that need privilege.
                                  -> %ProgramFiles%\Shellvis

          --uninstall user|service    Remove an installation. Configuration and
                                      conversation history are left in place.

          --status                Report what is installed and whether the service runs.

          --fetch-model [id]      Download the speech model for dictation into
                                  %LOCALAPPDATA%\Shellvis\Models. Without an id it takes
                                  the one from config.yaml, or the one chosen during
                                  setup. Does nothing when the model is already there or
                                  when "none" was chosen. The .msi runs this from its last
                                  page; on its own it is also the way to fetch a model
                                  before travelling.

        Options:
          --source <dir>          Where to copy from. Defaults to the installer's folder.
          --target <dir>          Override the destination.
          --no-autostart          Do not add a startup entry.
        """);
    }
}
