using System.Diagnostics;
using System.Text;

namespace Shellvis.Core.Shell;

/// <summary>The outcome of one out-of-process run.</summary>
public sealed record ExternalResult(string Output, string Errors, int ExitCode, TimeSpan Duration);

/// <summary>
/// Windows PowerShell 5.1, out of process.
///
/// <b>Why this exists at all, when a PowerShell 7 runspace is already hosted.</b> A real
/// number of Windows modules only load under the .NET Framework engine. Some ship no CLR 4
/// assemblies, some P/Invoke into things PowerShell 7 does not carry, and some simply were
/// never updated. Without a way to reach 5.1 those modules are unreachable, and the failure
/// arrives as an obscure type-load error in the middle of a task rather than as "that one
/// needs the old engine".
///
/// <b>Why out of process and not <c>Import-Module -UseWindowsPowerShell</c>.</b> That
/// compatibility mode exists and is the better answer when it works, but it proxies objects
/// across a remoting boundary and a good number of modules behave differently or not at all
/// under it. This is the blunt, reliable fallback: a real 5.1 process, its own session, and
/// the text it printed.
///
/// <b>The cost, stated plainly in the tool description.</b> No shared state. Variables,
/// imported modules and PSDrives from the hosted runspace do not exist here, and nothing
/// this leaves behind survives the call. That is the whole difference from
/// <c>powershell_run</c>, and a caller who does not know it will write a two-step script
/// whose second half sees nothing.
/// </summary>
public static class WindowsPowerShell
{
    /// <summary>Where Windows PowerShell lives, on every supported Windows.</summary>
    private static string Executable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    /// <summary>Whether the old engine is present at all.</summary>
    public static bool IsAvailable => File.Exists(Executable);

    public static async Task<ExternalResult> RunAsync(
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();

        if (!IsAvailable)
        {
            return new ExternalResult(
                string.Empty,
                $"Windows PowerShell was not found at {Executable}.",
                -1,
                clock.Elapsed);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // 5.1 emits in the console's OEM code page unless told otherwise, which turns
            // every umlaut in a German environment into a replacement character. Asking for
            // UTF-8 on both ends is the only way the text arrives as it was printed.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // -NoProfile because a user profile can print banners, change the prompt, or take
        // seconds to load, and none of that belongs in a tool result. -NonInteractive so a
        // cmdlet that would prompt fails instead of waiting forever: this project has met
        // that hang twice already, with PSGallery and with Excel's DisplayAlerts.
        //
        // ExecutionPolicy Bypass is deliberately NOT passed. The script arrives as text on
        // the command line rather than as a file, so no policy applies to it anyway, and a
        // tool that routinely disabled the machine owner's signing policy would be a way
        // around it rather than a fallback engine.
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");

        // Base64 rather than -Command with the script inline. The command line is parsed
        // by powershell.exe with its own quoting rules, which are not .NET's: the same
        // mismatch that made a hook path with a space arrive as ""C:\...\x.cmd"" and made
        // sc.exe reject a binPath. EncodedCommand takes the script as data and there is
        // nothing left to quote.
        //
        // The encoding is set INSIDE the script, and setting it on the reader alone is not
        // enough. StandardOutputEncoding says how this process decodes the pipe; it does not
        // change what 5.1 writes into it, and 5.1 writes in the console OEM code page. The
        // harness caught exactly that: "Grüße aus München" arrived as "Gr??e aus M?nchen",
        // every non-ASCII character replaced. The engine has to be told first.
        //
        // It ends with a NEWLINE, not a semicolon, and that is not cosmetic. PowerShell
        // reports an error as "In Zeile:1 Zeichen:107" and echoes the offending source line.
        // Joined with a semicolon, every error in the caller's script points at column 107
        // of a line that is mostly this prelude, and the echoed line is this code rather than
        // theirs. On its own line, the position is one line further down and the echo is the
        // line they actually wrote.
        const string Prelude =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n"
            + "$OutputEncoding = [System.Text.Encoding]::UTF8\n";

        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(
            Convert.ToBase64String(Encoding.Unicode.GetBytes(Prelude + script)));

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ExternalResult(
                string.Empty, $"could not start Windows PowerShell: {ex.Message}", -1, clock.Elapsed);
        }

        // Both streams at once. Draining one to completion first deadlocks as soon as the
        // other fills its pipe buffer, which any real output does.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);

            return new ExternalResult(
                stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty,
                $"the script was still running after {timeout.TotalSeconds:F0}s and was stopped",
                -1,
                clock.Elapsed);
        }

        clock.Stop();

        return new ExternalResult(
            await stdout.ConfigureAwait(false),
            Readable(await stderr.ConfigureAwait(false)),
            process.ExitCode,
            clock.Elapsed);
    }

    /// <summary>
    /// Turn 5.1's serialised error stream back into sentences.
    ///
    /// <b>Why this is needed and cannot be switched off.</b> When its error stream is
    /// redirected, Windows PowerShell always serialises it as CLIXML, whatever
    /// -OutputFormat says: that switch governs stdout. So a one-line failure arrives as
    /// several hundred characters of XML beginning "#&lt; CLIXML", and handing that to a model
    /// as a tool result is worse than saying nothing, because it looks like output.
    ///
    /// Only the text nodes are wanted, so the parse is deliberately shallow rather than a
    /// full deserialisation of the PowerShell object graph. If the shape is not what is
    /// expected, the original is returned untouched: an unreadable error is still better
    /// than a silently emptied one.
    /// </summary>
    private static string Readable(string errors)
    {
        const string Marker = "#< CLIXML";

        int start = errors.IndexOf(Marker, StringComparison.Ordinal);

        if (start < 0)
            return errors;

        string prefix = errors[..start];
        string xml = errors[(start + Marker.Length)..].Trim();

        var text = new StringBuilder();

        try
        {
            var document = System.Xml.Linq.XDocument.Parse(xml);

            foreach (System.Xml.Linq.XElement element in document.Descendants())
            {
                if (element.Name.LocalName != "S" || element.IsEmpty)
                    continue;

                text.Append(element.Value);
            }
        }
        catch (System.Xml.XmlException)
        {
            // Not the shape expected. Better to hand back the raw blob than to lose the
            // failure entirely: this project has an explicit rule that a tool error must
            // reach the model as readable text, and half of that is not losing it.
            return errors;
        }

        // CLIXML escapes the line breaks it carries. Left alone, the whole error arrives as
        // one run-on line with literal _x000D_ in it.
        string flattened = text.ToString()
            .Replace("_x000D__x000A_", "\n", StringComparison.Ordinal)
            .Replace("_x000D_", "\n", StringComparison.Ordinal)
            .Replace("_x000A_", "\n", StringComparison.Ordinal)
            .Replace("_x0020_", " ", StringComparison.Ordinal);

        return (prefix + flattened).Trim();
    }

    private static void TryKill(Process process)
    {
        try
        {
            // The whole tree: powershell.exe may itself have started something, and killing
            // only the parent leaves the child holding the console it was writing to.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone between the timeout and the kill. Nothing to do and nothing
            // worth reporting.
        }
    }
}
