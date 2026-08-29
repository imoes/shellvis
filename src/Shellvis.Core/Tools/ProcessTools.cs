using System.Globalization;
using System.Text;

using Shellvis.Core.Shell;

namespace Shellvis.Core.Tools;

/// <summary>
/// The two shell capabilities the original plan listed and never built.
///
/// <b><c>powershell_run_winps</c></b> reaches Windows PowerShell 5.1 out of process, for the
/// modules that only load under the .NET Framework engine. Without it those modules are
/// unreachable and the failure arrives as an obscure type-load error mid-task rather than as
/// "that one needs the old engine".
///
/// <b><c>process</c></b> starts something and lets go of it. Every other shell tool here
/// blocks until its command finishes, which is right for a query and wrong for a build or a
/// server: a model with only blocking tools runs the build in the foreground and the user
/// watches a frozen pill for four minutes.
/// </summary>
public sealed class ProcessTools(BackgroundProcesses processes)
{
    private const int DefaultTimeoutSeconds = 120;

    [ShellvisTool(
        "powershell_run_winps",
        SideEffect.Mutating,
        Description =
            "Run a script under Windows PowerShell 5.1 instead of the hosted PowerShell 7. "
            + "Use it ONLY for modules that will not load under 7. IMPORTANT: this is a "
            + "separate process with NO shared state -- variables, imported modules and "
            + "drives from powershell_run do not exist here, and nothing this leaves behind "
            + "survives the call. Put the whole job in one script.",
        PreviewParameter = "script",
        Glyph = "terminal")]
    public async Task<string> RunWindowsPowerShell(
        string script,
        int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(script))
            return "error: a script is required.";

        if (!WindowsPowerShell.IsAvailable)
        {
            return "Windows PowerShell 5.1 is not present on this machine. Use powershell_run, "
                + "which hosts PowerShell 7 in process.";
        }

        ExternalResult result = await WindowsPowerShell
            .RunAsync(
                script,
                TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)),
                cancellationToken)
            .ConfigureAwait(false);

        var sb = new StringBuilder();

        if (result.Output.Trim().Length > 0)
            sb.AppendLine(result.Output.TrimEnd());

        if (result.Errors.Trim().Length > 0)
            sb.AppendLine("errors:").AppendLine(result.Errors.TrimEnd());

        if (sb.Length == 0)
            sb.AppendLine("(no output)");

        sb.Append(string.Create(
            CultureInfo.InvariantCulture,
            $"[Windows PowerShell 5.1, exit {result.ExitCode}, {result.Duration.TotalMilliseconds:F0} ms]"));

        return sb.ToString();
    }

    [ShellvisTool(
        "process",
        SideEffect.Mutating,
        Description =
            "Run something in the background and check on it later. action is one of: "
            + "start (needs command), list, poll (needs id), log (needs id), wait (needs "
            + "id), kill (needs id). Use start for anything that takes minutes -- a build, "
            + "a copy, a server -- instead of blocking the conversation on it. Output is "
            + "buffered and the last lines are returned by log. Everything started this way "
            + "is killed when Shellvis closes.",
        PreviewParameter = "command",
        Glyph = "terminal")]
    public async Task<string> Manage(
        string action,
        string? command = null,
        string? id = null,
        string? workingDirectory = null,
        int timeoutSeconds = 30,
        int tail = 100,
        CancellationToken cancellationToken = default)
    {
        switch ((action ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "start":
                return Start(command, workingDirectory);

            case "list":
            {
                IReadOnlyList<ProcessStatus> all = processes.List();

                if (all.Count == 0)
                    return "nothing has been started in the background this session.";

                var sb = new StringBuilder();
                sb.Append(all.Count).AppendLine(" background process(es):");

                foreach (ProcessStatus status in all)
                    sb.Append("  ").AppendLine(status.ToString());

                return sb.ToString();
            }

            case "poll":
                return Require(id, out string? pollId)
                    ?? (processes.Poll(pollId!)?.ToString() ?? Unknown(pollId!));

            case "log":
                return Require(id, out string? logId)
                    ?? (processes.Log(logId!, Math.Clamp(tail, 1, 400)) is { } log
                        ? (log.Trim().Length > 0 ? log : "(that process has printed nothing yet)")
                        : Unknown(logId!));

            case "wait":
            {
                if (Require(id, out string? waitId) is { } refusal)
                    return refusal;

                ProcessStatus? status = await processes
                    .WaitAsync(
                        waitId!,
                        TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600)),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (status is null)
                    return Unknown(waitId!);

                // A wait that runs out is not a failure and must not read like one, or the
                // model kills a build that was going perfectly well.
                return status.Running
                    ? $"still running after the wait: {status}. Wait again or use log."
                    : status.ToString();
            }

            case "kill":
                return Require(id, out string? killId)
                    ?? (processes.Kill(killId!)?.ToString() ?? Unknown(killId!));

            default:
                return $"error: '{action}' is not an action. Use start, list, poll, log, "
                    + "wait or kill.";
        }
    }

    private string Start(string? command, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "error: start needs a command.";

        // Through cmd rather than parsed here. A command line is what the model wrote, and
        // splitting it into a file name and arguments means reimplementing the quoting rules
        // of whatever it names. This project has been bitten twice by exactly that mismatch:
        // .NET's argument quoting is not cmd's, and it is not sc.exe's either.
        //
        // /s /c is the documented pairing that makes cmd strip only the outermost quotes and
        // take the rest literally.
        try
        {
            ProcessStatus started = processes.Start(
                "cmd.exe",
                "/s /c \"" + command.Trim() + "\"",
                workingDirectory);

            return $"started {started.Id} (pid {started.ProcessId}). Use process(action: "
                + $"\"poll\", id: \"{started.Id}\") to check on it, or \"log\" to read what it "
                + "has printed.";
        }
        catch (Exception ex)
        {
            return $"the command could not be started: {ex.Message}";
        }
    }

    private static string? Require(string? id, out string? value)
    {
        value = id?.Trim();

        return string.IsNullOrWhiteSpace(value)
            ? "error: that action needs an id. Get one from start, or from list."
            : null;
    }

    private static string Unknown(string id) =>
        $"there is no background process with id '{id}'. Use list to see what is running.";
}
