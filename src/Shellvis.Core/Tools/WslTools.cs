using System.Text;
using Shellvis.Core.Shell;

namespace Shellvis.Core.Tools;

/// <summary>
/// WSL as tools.
///
/// Kept deliberately thin. The Linux side of this machine already has a capable agent
/// living on it, so Shellvis does not try to be one; it needs enough reach to run a
/// command, find out which distributions exist, and translate a path across the
/// boundary. Path translation matters more than it looks: the single most common way
/// to break a cross-boundary task is to hand a Windows path to a Linux tool.
/// </summary>
public sealed class WslTools
{
    [ShellvisTool(
        "wsl_distros",
        SideEffect.ReadOnly,
        Description =
            "List the installed WSL distributions with their state and WSL version. "
            + "Use it before running a command if you need to pick a specific one.",
        Glyph = "penguin")]
    public async Task<string> ListDistros(CancellationToken cancellationToken = default)
    {
        if (!WslRunner.IsAvailable)
            return "WSL is not installed on this machine.";

        IReadOnlyList<WslDistro> distros = await WslRunner
            .ListDistrosAsync(cancellationToken)
            .ConfigureAwait(false);

        if (distros.Count == 0)
            return "WSL is installed but no distributions are registered.";

        var sb = new StringBuilder();
        sb.Append(distros.Count).AppendLine(" WSL distribution(s):");
        foreach (WslDistro distro in distros)
            sb.Append("  ").AppendLine(distro.ToString());

        return sb.ToString();
    }

    [ShellvisTool(
        "wsl_run",
        SideEffect.Mutating,
        Description =
            "Run a shell command inside WSL through bash, so pipes, redirection and "
            + "globs all work. Use it for Linux tooling; prefer powershell_run for "
            + "anything about Windows itself.",
        PreviewParameter = "command",
        Glyph = "penguin")]
    public async Task<string> Run(
        string command,
        string? distro = null,
        string? workingDirectory = null,
        int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "error: a command is required.";

        WslResult result = await WslRunner.RunAsync(
            command,
            distro,
            workingDirectory,
            TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 900)),
            cancellationToken).ConfigureAwait(false);

        return result.ToToolText();
    }

    [ShellvisTool(
        "wsl_path",
        SideEffect.ReadOnly,
        Description =
            "Translate a path between Windows and Linux form using the distribution's "
            + "own mount configuration. Do this rather than guessing at /mnt/c: a "
            + "Windows path handed to a Linux tool is the usual cause of a "
            + "cross-boundary task failing.",
        PreviewParameter = "path",
        Glyph = "path")]
    public async Task<string> TranslatePath(
        string path,
        bool toWindows = false,
        string? distro = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "error: a path is required.";

        return await WslRunner
            .TranslatePathAsync(path, toWindows, distro, cancellationToken)
            .ConfigureAwait(false);
    }
}
