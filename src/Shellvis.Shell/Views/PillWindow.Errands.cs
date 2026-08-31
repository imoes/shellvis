using Shellvis.Core.Cron;
using Shellvis.Shell.Agent;

namespace Shellvis.Shell.Views;

/// <summary>
/// Work arriving from outside the window: a Windows task, a script, another shell.
///
/// <b>Why the running instance is the right place for it.</b> A Windows task calls
/// <c>Shellvis.Shell.exe --job briefing</c>. That process finds this one and hands the errand
/// over rather than becoming a second Shellvis, and everything that makes the result useful
/// lives here: the console that records it, the conversation that holds it, and the desktop
/// alert -- which cannot exist without a window to belong to. A headless run can only leave a
/// note in the store for later.
///
/// <b>A job and a prompt are not the same errand, and the difference is deliberate.</b> A
/// prompt is a turn in the user's conversation, exactly as if it had been typed. A job is a
/// scheduled run: its own loop, its own history, approvals refused, and its report kept out
/// of the conversation's context -- because the summary a job wrote at three in the morning
/// must not become part of what the model is thinking about at nine.
/// </summary>
public sealed partial class PillWindow
{
    private CancellationTokenSource? _errands;

    /// <summary>
    /// Start listening for errands.
    ///
    /// Failure is reported and survivable. Another instance already listening is the ordinary
    /// case -- two Shellvis processes are allowed, and only the first one owns the channel;
    /// the second simply cannot be reached from outside, which is a limitation and not a
    /// fault.
    /// </summary>
    private void StartErrandListener()
    {
        _errands = new CancellationTokenSource();

        _ = Task.Run(() => PromptChannel.ListenAsync(
            errand => DispatcherQueue.TryEnqueue(() => OnErrand(errand)),
            _errands.Token));
    }

    private void StopErrandListener()
    {
        _errands?.Cancel();
        _errands?.Dispose();
        _errands = null;
    }

    private void OnErrand(Errand errand)
    {
        if (errand.Job is { Length: > 0 } name)
        {
            RunErrandJob(name);
            return;
        }

        if (errand.Prompt is { Length: > 0 } prompt)
            RunErrandPrompt(prompt);
    }

    /// <summary>
    /// A prompt from outside, asked as if it had been typed.
    ///
    /// <b>It does not raise the window, and that is the whole discipline of this project.</b>
    /// Something that arrives on a timer must not take the screen from whoever is working:
    /// the prompt goes into the transcript and the conversation, the answer follows the same
    /// path an answer always does, and the user finds it when they look. What they get in the
    /// meantime is the quiet mark on the bar.
    /// </summary>
    private void RunErrandPrompt(string prompt)
    {
        AddRow(GlyphPerson, Oneline(prompt), "asked");
        RecordPrompt(prompt);

        _ = RunErrandTurnAsync(prompt);
    }

    private async Task RunErrandTurnAsync(string prompt)
    {
        if (_session is null && _sessionTask is not null)
        {
            try
            {
                _session = await _sessionTask;
            }
            catch (Exception)
            {
                // Already reported in the transcript by AnnounceWhenReadyAsync.
            }
        }

        if (_session is null)
        {
            NoteQuietly("a prompt arrived from outside but there is no model session", "errand", true);
            return;
        }

        await _session.RunTurnAsync(prompt, Render);
    }

    /// <summary>
    /// A named job, run here as the scheduled run it is.
    ///
    /// Reported through the same quiet path the in-process scheduler uses, so a job triggered
    /// by Windows and a job triggered by the loop are indistinguishable from the outside --
    /// including the alert, which is the reason this instance was preferred over a headless
    /// run in the first place.
    /// </summary>
    private void RunErrandJob(string name)
    {
        _ = RunErrandJobAsync(name);
    }

    private async Task RunErrandJobAsync(string name)
    {
        var store = new CronStore();

        CronJob? job = store.Load().FirstOrDefault(
            j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (job is null)
        {
            NoteQuietly($"a Windows task asked for job '{name}', which is not in {store.Path}", "errand", true);
            return;
        }

        if (_session is null && _sessionTask is not null)
        {
            try
            {
                _session = await _sessionTask;
            }
            catch (Exception)
            {
            }
        }

        if (_session is null)
        {
            NoteQuietly($"job '{name}' was asked for but there is no model session", "errand", true);
            return;
        }

        AddRow(GlyphTool, $"running '{job.Name}', asked for by Windows", "cron");

        try
        {
            CronRunResult result = await _session.RunScheduledJobAsync(job, CancellationToken.None);

            store.RecordRun(job.Name, DateTimeOffset.Now, result.Summary);

            if (result.Headline is { Length: > 0 })
                RecordScheduledReport(result.Job, result.Summary, result.Headline);

            NoteQuietly(
                $"cron '{result.Job}' {(result.Succeeded ? "ran" : "failed")} in "
                    + $"{result.Duration.TotalSeconds:F1}s: {result.Summary}",
                "cron",
                !result.Succeeded,
                result.Headline);
        }
        catch (Exception ex)
        {
            NoteQuietly($"job '{job.Name}' threw: {ex.Message}", "cron", true,
                $"The scheduled job '{job.Name}' failed.");
        }
    }
}
