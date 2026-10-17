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

        RegisterWithExplorer();
    }

    /// <summary>
    /// Put "Ask Shellvis" in Explorer's menu and make shellvis: links open here, when nothing
    /// usable is registered yet. A working registration -- from the installer, or from
    /// another copy -- is left alone; see <see cref="Core.Desktop.ShellIntegration"/>.
    /// </summary>
    private void RegisterWithExplorer()
    {
        string executable = Environment.ProcessPath ?? string.Empty;
        bool german = L("en", "de") == "de";

        _ = Task.Run(() =>
        {
            try
            {
                if (Core.Desktop.ShellIntegration.EnsureRegistered(executable, german) is { } done)
                    DispatcherQueue.TryEnqueue(() => AddRow(GlyphTool, done, "explorer"));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // A policy that locks HKCU\Software\Classes. Said once, not retried.
                DispatcherQueue.TryEnqueue(() => AddRow(GlyphWarning,
                    $"Explorer's menu could not be extended: {ex.Message}", "explorer"));
            }
        });
    }

    /// <summary>An errand this instance was started for, handed over once the window exists.</summary>
    /// <remarks>
    /// Queued rather than run at once: it is called straight after Activate, and the window
    /// it opens beside the bar should not be placed before the bar has a position.
    /// </remarks>
    internal void TakeErrand(Errand errand) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => OnErrand(errand));

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

        if (errand.Link is { Length: > 0 } link)
        {
            _ = FollowOutsideLinkAsync(link);
            return;
        }

        if (errand.File is { Length: > 0 } file)
        {
            _ = TakeFileAsync(file, errand.Sort);
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
    /// A shellvis: link followed from a browser, a document or a note.
    ///
    /// <b>Only the internal scheme, and only its closed set.</b> The registration hands this
    /// whatever address was clicked, and a web page can put any address it likes behind a
    /// shellvis: link. So the address goes to the same small set of link actions an answer's
    /// link reaches -- opening a mail -- and anything else is named and dropped. Nothing that
    /// arrives this way becomes a prompt.
    /// </summary>
    private async Task FollowOutsideLinkAsync(string link)
    {
        string href = link.Trim();

        if (!href.StartsWith(InternalScheme, StringComparison.OrdinalIgnoreCase))
        {
            AddRow(GlyphWarning, $"ignored an outside link that is not shellvis: — {Oneline(href)}", "link");
            return;
        }

        await AwaitSessionAsync();
        RunInternalLink(href[InternalScheme.Length..].TrimStart('/'));
    }

    /// <summary>
    /// A file from Explorer's menu: put it on the workbench and either wait for the question
    /// ("Ask Shellvis") or ask the filing question straight away ("File with Shellvis").
    ///
    /// The workbench comes forward in both cases. Somebody right-clicked a file and chose
    /// Shellvis; the discipline of not taking the screen is for things nobody asked for.
    /// </summary>
    private async Task TakeFileAsync(string path, bool sort)
    {
        Core.Office.DocumentContext context;

        try
        {
            context = await Task.Run(() => Core.Office.DocumentContextReader.ReadOrDescribe(path));
        }
        catch (Exception ex)
        {
            ShowVorzimmer();
            _vorzimmer?.ShowDocumentError(DocumentError(ex));
            return;
        }

        SetDocumentContext(context);
        ShowVorzimmer();

        if (_vorzimmer is null)
            return;

        _vorzimmer.ShowSection("conversation");

        if (!sort)
        {
            _vorzimmer.FocusPrompt();
            return;
        }

        await SubmitPromptAsync(L(
            "File this document for me. Say in two sentences what it is. Then look on the desk and "
                + "in the mailbox for the mail, ticket or appointment it belongs to, and link it. "
                + "Suggest where to keep it, but move nothing.",
            "Ordne dieses Dokument für mich ein. Sag in zwei Sätzen, was es ist. Dann such auf dem "
                + "Schreibtisch und im Postfach nach der Mail, dem Ticket oder dem Termin, zu dem es "
                + "gehört, und verlinke es. Schlag vor, wo es abgelegt werden sollte, aber verschiebe nichts."));
    }

    /// <summary>Wait for the model session when it is still starting, as a cold start from a click is.</summary>
    private async Task AwaitSessionAsync()
    {
        if (_session is not null || _sessionTask is null)
            return;

        try
        {
            _session = await _sessionTask;
        }
        catch (Exception)
        {
            // Already reported in the transcript by AnnounceWhenReadyAsync.
        }
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
