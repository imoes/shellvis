using System.Text.Json;

using Shellvis.Core.Agent;
using Shellvis.Core.Config;
using Shellvis.Core.Office;

using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Shellvis.Shell.Views;

/// <summary>
/// Watching Outlook, and asking the model whether anything that arrived deserves your
/// attention.
///
/// <b>What this is not.</b> It is not Outlook's own notification, which already exists and
/// tells you that mail arrived. The thing that is missing is the judgement: of the eighty
/// things that come in during a morning, which one changes what you should do in the next
/// hour. So a look at the mailbox is cheap and silent, and only something new buys a question
/// to the model, and only an answer that is not SILENCE reaches the desktop.
///
/// <b>Three separate restraints, and each one earns its place.</b>
///
/// <list type="bullet">
/// <item>Nothing new, no question. Obvious, and it is what makes a three-minute interval
/// affordable.</item>
/// <item>Never while a turn is running. On this machine a turn against the local model takes
/// one to three minutes, and <c>RunTurnAsync</c> serialises: a watcher that queued its own
/// turn would make somebody wait for an answer they did not ask for.</item>
/// <item>A floor between questions, ten minutes by default. Without it a busy hour would be
/// a question every three minutes, which is the model talking to itself while the user
/// waits.</item>
/// </list>
///
/// <b>And the alert itself is not shown directly.</b> It goes through
/// <see cref="NoteQuietly"/> like every other announcement, so the transcript always gets the
/// line and the desktop alert waits for a moment when Windows says an interruption is
/// allowed. A watcher that could put a toast over a presentation would be the first thing
/// switched off.
/// </summary>
public sealed partial class PillWindow
{
    private DispatcherQueueTimer? _watchTimer;
    private WatchState _watchState = new();
    private WatchSection _watchSettings = new();
    private bool _watchLooking;

    private static string WatchStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".shellvis",
        "watch.json");

    private void RegisterMailboxWatch()
    {
        _watchSettings = ConfigStore.Load().Config.Watch ?? new WatchSection();

        if (!_watchSettings.Enabled)
        {
            AddRow(GlyphTool, "Outlook is not being watched (watch.enabled is false).", "watch");
            return;
        }

        if (!OutlookClient.IsAvailable)
        {
            // Said once, at startup, rather than every three minutes. A machine without
            // classic Outlook is a legitimate configuration and not a fault.
            AddRow(
                GlyphTool,
                "Outlook is not available for automation, so nothing is being watched.",
                "watch");

            return;
        }

        _watchState = LoadWatchState();

        _watchTimer = DispatcherQueue.CreateTimer();
        _watchTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_watchSettings.EveryMinutes, 1, 60));
        _watchTimer.IsRepeating = true;
        _watchTimer.Tick += (_, _) => _ = LookAtOutlookAsync();
        _watchTimer.Start();
    }

    private void UnregisterMailboxWatch()
    {
        _watchTimer?.Stop();
        _watchTimer = null;
    }

    private async Task LookAtOutlookAsync()
    {
        // Re-entrancy, and it is not theoretical: a look that has to start Outlook takes
        // seconds, and the timer does not wait for the previous tick to finish.
        if (_watchLooking || _session is null)
            return;

        _watchLooking = true;

        try
        {
            DateTime now = DateTime.Now;

            WatchFindings findings = await _session.Outlook!
                .LookAsync(_watchState, now, TimeSpan.FromMinutes(Math.Clamp(_watchSettings.LeadMinutes, 1, 240)))
                .ConfigureAwait(true);

            // Marked as seen HERE, after the look and before the question.
            //
            // The look succeeded, so the state should say so: leaving it unmarked until the
            // model has answered means a crash or a restart re-announces the same mail, and
            // being told twice about something is what makes an alert worthless. The cost is
            // that a question that fails loses that one piece of news, which is the better
            // of the two failures.
            if (findings.Arrivals.Count > 0)
                _watchState.SeenUpTo = findings.Arrivals.Max(a => a.Received);
            else
                _watchState.SeenUpTo ??= now;

            foreach (Upcoming appointment in findings.Appointments)
                _watchState.Remember(appointment.EntryId);

            SaveWatchState();

            if (!MailboxWatch.ShouldAsk(
                findings, now, _watchState.LastAsked,
                TimeSpan.FromMinutes(Math.Clamp(_watchSettings.QuietMinutes, 0, 240))))
            {
                return;
            }

            // The user's own turn always wins, checked as late as possible because one may
            // have started while the look was running. IsBusy is the session's existing
            // flag; a second one of my own would be a second answer to the same question.
            if (_session.IsBusy)
                return;

            _watchState.LastAsked = now;
            SaveWatchState();

            await AskAboutArrivalsAsync(findings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A watcher that throws must not take the window with it, and must not fill the
            // transcript either: one line, and the next tick tries again.
            AddRow(GlyphWarning, $"could not look at Outlook: {ex.Message}", "watch", isWarning: true);
        }
        finally
        {
            _watchLooking = false;
        }
    }

    /// <summary>
    /// Put the arrivals to the model and turn its answer into an alert, or into nothing.
    /// </summary>
    /// <remarks>
    /// <b>A look nobody asked for leaves no trace in the conversation, and that took two
    /// goes to get right.</b>
    ///
    /// The first version rendered every event as it arrived and only then decided whether to
    /// raise an alert -- so a silent look still wrote the word SILENCE into the answer
    /// window, and the streaming delta pulled the console open on its way past. Holding the
    /// prose back fixed what was on screen.
    ///
    /// It did not fix the record. The turn still went through <c>RunTurnAsync</c>, which
    /// records the prompt as a user line, so the history held the entire watch briefing every
    /// three minutes with SILENCE under it -- and all of it was context for whatever the user
    /// asked next. That is why this now goes through <c>AskAsideAsync</c>: its own history,
    /// the same tools, nothing recorded.
    ///
    /// Tool events are still rendered as they happen. They are the record of what was
    /// actually read, they are what makes an unprompted look accountable, and they were never
    /// the noise.
    /// </remarks>
    private async Task AskAboutArrivalsAsync(WatchFindings findings)
    {
        // One line in the console, and it is the only thing a look writes down of its own
        // accord. AskSelf, which the menu items use, writes the question as though the user
        // had typed it -- right for something they clicked, wrong for this.
        AddRow(GlyphTool, WatchLine(findings), "watch");

        // Not RunTurnAsync. That records the prompt as a user line and the answer as the
        // assistant's, so every three minutes the conversation grew a page of instructions
        // and the word SILENCE -- visible in the history window, and in the context of
        // whatever the user asked next. AskAsideAsync runs the same tools over a history of
        // its own and records nothing.
        string said = await _session!.AskAsideAsync(
            MailboxWatch.Prompt(findings),
            agentEvent =>
            {
                switch (agentEvent)
                {
                    // Swallowed. The verdict decides whether there is anything to show, and
                    // showing it as it streams commits to an answer before that is known.
                    case AgentEvent.AssistantMessage:
                    case AgentEvent.AssistantDelta:
                    case AgentEvent.ReasoningDelta:
                        return;

                    // The turn's ending would move the status line, and nobody asked.
                    case AgentEvent.TurnFinished:
                        return;

                    default:
                        Render(agentEvent);
                        return;
                }
            },
            CancellationToken.None).ConfigureAwait(true);

        if (MailboxWatch.IsSilence(said))
        {
            // Nothing reaches the desktop and nothing reaches the conversation. The
            // "looking at ..." line above already says a look happened; this says how it
            // came out, so a reader can tell a silent look from one that never finished.
            AddRow(GlyphTool, "nothing there was worth interrupting for", "watch");
            return;
        }

        string headline = MailboxWatch.Headline(said);

        // Now it is worth saying, so it goes where an answer goes: the conversation gets the
        // full text, the console gets a line saying it happened.
        RecordAnswer(Tidy(said));

        AddRow(GlyphSpeaker, $"answered, {WordCount(said)} words", "answer", isAnnouncement: true);

        // NoteQuietly rather than the toast directly: the transcript gets the line, and the
        // alert waits until Windows says an interruption is allowed.
        NoteQuietly(headline, "watch", isProblem: false, headline: headline);
    }

    /// <summary>One line saying what was found, for the console record.</summary>
    private static string WatchLine(WatchFindings findings)
    {
        var parts = new List<string>();

        if (findings.Appointments.Count > 0)
            parts.Add($"{findings.Appointments.Count} appointment(s) starting soon");

        int requests = findings.Arrivals.Count(a => a.Kind == ArrivalKind.MeetingRequest);
        int tickets = findings.Arrivals.Count(a => a.Kind == ArrivalKind.TicketNotification);
        int ordinary = findings.Arrivals.Count - requests - tickets;

        if (ordinary > 0)
            parts.Add($"{ordinary} new mail");

        if (requests > 0)
            parts.Add($"{requests} meeting request(s)");

        if (tickets > 0)
            parts.Add($"{tickets} ticket notification(s)");

        return "looking at " + string.Join(", ", parts) + " to see whether any of it matters";
    }

    private static WatchState LoadWatchState()
    {
        try
        {
            if (!File.Exists(WatchStatePath))
                return new WatchState();

            return JsonSerializer.Deserialize<WatchState>(
                File.ReadAllText(WatchStatePath),
                WatchStateFormat) ?? new WatchState();
        }
        catch (Exception)
        {
            // A corrupt or hand-edited file is not worth failing over. Starting fresh costs
            // one quiet first look, which is exactly what a first run does anyway.
            return new WatchState();
        }
    }

    private void SaveWatchState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WatchStatePath)!);

            File.WriteAllText(
                WatchStatePath,
                JsonSerializer.Serialize(_watchState, WatchStateFormat));
        }
        catch (Exception)
        {
            // Losing the mark means the next start is quiet once more. Not worth a warning
            // in the transcript every three minutes.
        }
    }

    private static readonly JsonSerializerOptions WatchStateFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
}
