using System.Diagnostics;
using System.Text;

namespace Shellvis.Core.Shell;

/// <summary>One installed WSL distribution.</summary>
/// <param name="Name">Distribution name, as wsl.exe uses it.</param>
/// <param name="State">Running, Stopped, or whatever wsl reports.</param>
/// <param name="Version">WSL version, 1 or 2.</param>
/// <param name="IsDefault">Whether this is the default distribution.</param>
public sealed record WslDistro(string Name, string State, string Version, bool IsDefault)
{
    public override string ToString() =>
        $"{Name}  {State}  WSL{Version}{(IsDefault ? "  (default)" : string.Empty)}";
}

/// <summary>Result of running something inside WSL.</summary>
public sealed record WslResult(string Output, string Errors, int ExitCode, TimeSpan Duration)
{
    public string ToToolText()
    {
        var sb = new StringBuilder();

        if (Output.Length > 0)
            sb.AppendLine(Output.TrimEnd());

        if (Errors.Length > 0)
            sb.Append("stderr: ").AppendLine(Errors.TrimEnd());

        // A non-zero exit is the single most useful fact about a shell command and is
        // easy to lose when only stdout is reported.
        if (ExitCode != 0)
            sb.Append("exit code ").Append(ExitCode).AppendLine();

        if (sb.Length == 0)
            sb.AppendLine("(no output)");

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Runs commands inside WSL.
///
/// One thing dominates the implementation: <c>wsl.exe</c> writes its own output as
/// UTF-16LE by default, so reading it as UTF-8 yields strings with a null byte between
/// every character. It is immediately visible once you see it ("D e b i a n") and
/// completely baffling until you do. Setting WSL_UTF8=1 is the documented fix and is
/// applied to every invocation here.
///
/// Note that this only affects wsl.exe's OWN messages, such as the distribution list.
/// Output from a command running inside the distribution is whatever that command
/// produced, normally UTF-8.
/// </summary>
public static class WslRunner
{
    /// <summary>Whether WSL is present at all.</summary>
    public static bool IsAvailable => File.Exists(WslPath);

    private static string WslPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    /// <summary>List installed distributions.</summary>
    public static async Task<IReadOnlyList<WslDistro>> ListDistrosAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return [];

        WslResult result = await RunProcessAsync(
            ["--list", "--verbose"], null, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var distros = new List<WslDistro>();

        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();

            // Skip the header row, whatever language Windows is set to.
            if (trimmed.Length == 0 || trimmed.StartsWith("NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            // The default distribution is marked with a leading asterisk.
            bool isDefault = trimmed.StartsWith('*');
            if (isDefault)
                trimmed = trimmed[1..].Trim();

            string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            // Take the version and state from the END: a distribution name may contain
            // spaces, so counting from the front would mis-split it.
            distros.Add(new WslDistro(
                Name: string.Join(' ', parts[..^2]),
                State: parts[^2],
                Version: parts[^1],
                IsDefault: isDefault));
        }

        return distros;
    }

    /// <summary>
    /// Run a bash command inside a distribution.
    /// </summary>
    /// <param name="command">Shell command. Run through bash -lc, so pipes and redirection work.</param>
    /// <param name="distro">Distribution name, or null for the default one.</param>
    /// <param name="workingDirectory">Linux path to start in, or null.</param>
    public static async Task<WslResult> RunAsync(
        string command,
        string? distro = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return new WslResult(string.Empty, "WSL is not installed on this machine.", -1, TimeSpan.Zero);

        var args = new List<string>();

        if (!string.IsNullOrWhiteSpace(distro))
        {
            args.Add("--distribution");
            args.Add(distro);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            args.Add("--cd");
            args.Add(workingDirectory);
        }

        // -l makes it a login shell so the user's profile and PATH apply; without it a
        // command that works in the user's terminal mysteriously does not work here.
        args.Add("--");
        args.Add("bash");
        args.Add("-lc");
        args.Add(command);

        return await RunProcessAsync(args, null, timeout ?? TimeSpan.FromMinutes(2), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Translate a path between Windows and Linux form using wslpath, which is the
    /// only translator that accounts for the distribution's own mount configuration.
    /// </summary>
    public static async Task<string> TranslatePathAsync(
        string path,
        bool toWindows,
        string? distro = null,
        CancellationToken cancellationToken = default)
    {
        string flag = toWindows ? "-w" : "-u";

        // Single-quoted for the Linux shell, with embedded quotes escaped the POSIX
        // way, so a path with spaces or apostrophes survives.
        string escaped = path.Replace("'", "'\\''", StringComparison.Ordinal);

        WslResult result = await RunAsync(
            $"wslpath {flag} '{escaped}'", distro, null, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0
            ? result.Output.Trim()
            : $"error: {result.Errors.Trim()}";
    }

    private static async Task<WslResult> RunProcessAsync(
        IReadOnlyList<string> arguments,
        string? stdin,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WslPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // The whole reason this class needs care: without WSL_UTF8, wsl.exe emits its
        // own messages as UTF-16LE, and reading them as UTF-8 gives "D e b i a n".
        startInfo.Environment["WSL_UTF8"] = "1";

        var clock = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new WslResult(string.Empty, $"could not start wsl.exe: {ex.Message}", -1, clock.Elapsed);
        }

        // Read both streams concurrently. Reading one to completion first deadlocks as
        // soon as the other fills its pipe buffer, which happens with any real output.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            string partial = stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty;
            return new WslResult(
                partial,
                $"the command was still running after {timeout.TotalSeconds:F0}s and was stopped",
                -1,
                clock.Elapsed);
        }

        clock.Stop();

        return new WslResult(
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false),
            process.ExitCode,
            clock.Elapsed);
    }

    private static void TryKill(Process process)
    {
        try
        {
            // entireProcessTree: wsl.exe spawns the distribution's shell, and killing
            // only the parent leaves the real work running.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Already gone.
        }
    }
}
