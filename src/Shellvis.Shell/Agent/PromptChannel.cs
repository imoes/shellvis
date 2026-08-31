using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Shellvis.Shell.Agent;

/// <summary>What a caller asked a running Shellvis to do.</summary>
/// <param name="Job">The scheduled job to run, or null.</param>
/// <param name="Prompt">A prompt to ask as an ordinary turn, or null.</param>
public sealed record Errand(string? Job, string? Prompt);

/// <summary>
/// Handing work to the Shellvis that is already running.
///
/// <b>Why this exists at all.</b> A Windows task calls this executable with a parameter, and
/// starting a second Shellvis would be wrong in every way that matters: two PowerShell
/// runspaces, two COM apartments, two tray icons, and -- worst -- a run whose result appears
/// nowhere the user is looking, because the instance they have open is not the one that did
/// the work. So the parameter is delivered to the instance that is already there, and only a
/// machine with none of them falls back to running it alone.
///
/// <b>Restricted to the current user, deliberately.</b> A named pipe with default security is
/// reachable by other accounts on the same machine, and what travels through this one is a
/// prompt for an agent that can drive the desktop, read mail and run PowerShell. Anyone able
/// to write here would be able to act as this user through their own agent. The ACL therefore
/// grants exactly one SID -- the account that created it -- and nothing else, not even
/// administrators, who do not need it and whose inclusion would widen the door for no gain.
///
/// <b>A prompt is text, and text from a pipe is not trusted content.</b> It is handed to the
/// same path a typed prompt takes, which means the model sees it as a user turn -- so a
/// caller can ask for anything the user could ask for, and nothing more. Approvals still
/// apply: a prompt arriving this way cannot grant itself permission, and the gate is the same
/// one the keyboard reaches.
/// </summary>
internal static class PromptChannel
{
    /// <summary>
    /// The pipe's name, scoped to the account.
    ///
    /// Per user rather than per machine: two people signed in at once each get their own
    /// Shellvis, and a shared name would mean whichever started first receives both.
    /// </summary>
    private static string PipeName =>
        $"Shellvis.Errand.{Environment.UserName}";

    /// <summary>How long a caller waits for the running instance before giving up on it.</summary>
    /// <remarks>
    /// Short. The question being asked is "is there one", not "will one appear", and a task
    /// that hangs for a minute deciding is a task that overlaps its own next run.
    /// </remarks>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Try to hand an errand to a running instance.
    /// </summary>
    /// <returns>True when a running Shellvis accepted it.</returns>
    public static bool TrySend(Errand errand)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.None);

            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

            // One line, one errand. A length-prefixed protocol would be more rigorous and is
            // not worth it: the only two forms are a job name and a prompt, and a newline is
            // enough to separate a verb from the rest of the line.
            writer.WriteLine(errand.Job is { Length: > 0 } job
                ? "job " + job.ReplaceLineEndings(" ")
                : "prompt " + (errand.Prompt ?? string.Empty).ReplaceLineEndings(" "));

            return true;
        }
        catch (Exception)
        {
            // No instance, a pipe belonging to another account, or a machine that refuses
            // named pipes. All of them mean the same thing to the caller: do it yourself.
            return false;
        }
    }

    /// <summary>
    /// Listen for errands until the token is cancelled, handing each to <paramref name="onErrand"/>.
    ///
    /// One connection at a time, sequentially. Concurrency would buy nothing: the agent
    /// serialises turns anyway, and a queue of one keeps the ordering obvious.
    /// </summary>
    public static async Task ListenAsync(Action<Errand> onErrand, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream pipe = Create();

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (Parse(line) is { } errand)
                    onErrand(errand);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A malformed caller, a pipe torn down mid-read, a transient failure. The
                // listener must outlive all of them: losing the channel would mean every
                // later scheduled task silently starting a second instance instead.
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Errand? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        string text = line.Trim();

        if (text.StartsWith("job ", StringComparison.OrdinalIgnoreCase))
            return new Errand(text[4..].Trim(), null);

        if (text.StartsWith("prompt ", StringComparison.OrdinalIgnoreCase))
            return new Errand(null, text[7..].Trim());

        return null;
    }

    private static NamedPipeServerStream Create()
    {
        var rules = new PipeSecurity();

        SecurityIdentifier me = WindowsIdentity.GetCurrent().User
            ?? new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        rules.AddAccessRule(new PipeAccessRule(
            me, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            rules);
    }
}
