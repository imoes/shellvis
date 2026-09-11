using System.Globalization;
using System.Text.Json;

using Shellvis.Core.Config;
using Shellvis.Core.Desk;
using Shellvis.Core.Office;

namespace Shellvis.Shell.Views;

/// <summary>
/// The desk, on a page, behind a button next to the answer.
///
/// <b>The page IS the desk. It does not describe one.</b> It began as a reference sheet -- a
/// lede, six professional rules with their sources, a worked morning, a list of what code
/// decides -- and every word of that was true and none of it was the desk. It was reported as
/// placeholder text five times before I understood the report. The rules are meant to SHAPE
/// the page: three trays because sorting comes before speaking, a handful in each because
/// three things beat thirty, a count for the rest, an empty tray that says so in words, and
/// no send button anywhere. A page that states them instead has put a description where the
/// thing should be.
///
/// <b>The sorting is the model's, and this is a reversal.</b> An earlier version of this
/// comment argued that which mail needs an answer cannot be computed, and put counts here
/// with a disclaimer -- and then sorted by whether the sender's address looked like a
/// machine, which is a guess wearing a triage's clothes exactly as described. The premise was
/// right and the conclusion was wrong: the answer is to ask the model, not to stop asking.
/// Each message is judged once, the verdict is kept, and the trays are a lookup. See
/// <c>PillWindow.Triage</c> and <c>DeskTriage</c>.
///
/// <b>A badge means "since you last opened this".</b> The comparison point is written to disk
/// when the window opens, so closing it and coming back an hour later shows what the hour
/// brought -- and a refresh while it is open keeps comparing against that same point, so the
/// badges accumulate rather than resetting every three minutes.
/// </summary>
public sealed partial class PillWindow
{
    private VorzimmerWindow? _vorzimmer;

    /// <summary>The desk as it was when this window was last opened.</summary>
    private DeskSnapshot? _deskBaseline;

    /// <summary>Whether a count is already in flight, so a timer cannot stack them up.</summary>
    private bool _counting;

    private static string DeskStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".shellvis",
        "desk.json");

    /// <summary>Open the reference page, or bring it back if it is already up.</summary>
    private void ShowVorzimmer()
    {
        bool first = _vorzimmer is null;

        if (_vorzimmer is null)
        {
            _vorzimmer = new VorzimmerWindow();

            // The button on the page. Subscribed once, when the window is made, so a second
            // open does not stack a second handler and count the desk twice.
            _vorzimmer.RefreshRequested += () => _ = CountTheDeskAsync(saveBaseline: false, thenSort: true);
            _vorzimmer.RememberDaysChanged += RememberOver;
            _vorzimmer.OpenRequested += OpenFromDesk;
            _vorzimmer.SearchRequested += words => _ = SearchTheDeskAsync(words);
            _vorzimmer.ExpandRequested += (id, again) => _ = ExpandAsync(id, again);
        }

        _vorzimmer.Reveal(WinRT.Interop.WindowNative.GetWindowHandle(this));

        // The stored baseline is read on the FIRST open of this session only. Later opens
        // keep the one in memory, so a window closed and reopened within a session does not
        // wipe the badges the session earned.
        if (first)
            _deskBaseline = LoadDesk();

        _ = CountTheDeskAsync(saveBaseline: true, thenSort: true);
    }

    /// <summary>
    /// Count the desk again, called from the watcher's own tick.
    ///
    /// Piggybacking on the watcher rather than running a timer of its own: it already looks
    /// at Outlook every few minutes, the COM apartment is single-threaded, and a second
    /// timer would mean two callers queueing behind each other for the same mailbox.
    ///
    /// <b>It no longer requires the page to be open, and that is deliberate.</b> The count
    /// is what fills the store the tools read and what raises the notification when the desk
    /// changes -- and a notification that only works while you are already looking at the
    /// page is a notification for the one case that does not need one. The cost is one
    /// restricted folder query and a bounded scan per tick, alongside the look the watcher
    /// was doing anyway.
    /// </summary>
    private void RefreshVorzimmer() => _ = CountTheDeskAsync(saveBaseline: false);

    /// <summary>
    /// Count the desk and hand it to the page.
    /// </summary>
    /// <param name="saveBaseline">
    /// True when this count establishes what "new" means from now on -- that is, when the
    /// window was just opened. A refresh must NOT save one: overwriting the comparison point
    /// on every tick would clear the badges three minutes after they appeared, which is
    /// exactly long enough for somebody to miss them.
    /// </param>
    /// <param name="thenSort">
    /// Whether to start a sorting pass straight afterwards.
    ///
    /// True when the count came from the page -- it was opened, or the button was pressed.
    /// Somebody looking at "noch unsortiert: 1" and pressing the only button on the page
    /// means "deal with it", and answering that with a recount and a three-minute wait is
    /// how it came to look as though nothing happens.
    ///
    /// False from the watcher's tick, which starts its own single pass. The distinction is
    /// deliberate: while the page is in front of somebody the backlog is worked down as fast
    /// as the model manages, and in the background it sips one batch every few minutes rather
    /// than holding the model for ten minutes on end.
    /// </param>
    private async Task CountTheDeskAsync(bool saveBaseline, bool thenSort = false)
    {
        // Already counting: say nothing and do nothing. The count in flight ends in a
        // render, which is what puts the button back -- so the press is not lost, it is
        // answered by the other one.
        if (_counting)
            return;

        if (_session?.Outlook is null)
        {
            _vorzimmer?.Trouble(Words.OutlookUnreachable);
            return;
        }

        _counting = true;

        try
        {
            DeskReading reading = await _session.Outlook
                .TakeSnapshotAsync(DateTime.Now)
                .ConfigureAwait(true);

            Remember(reading);

            // The trays come out of the store rather than out of the walk, and that is the
            // change that matters: the walk knows who sent a thing, the store knows what the
            // model decided it needs. Sorting by sender is what put a broadcast under
            // "braucht heute eine Antwort".
            // Two horizons, and running them together was the defect.
            //
            // COUNTING and SORTING reach as far back as the store keeps. Unread is unread
            // whatever its age, and bounding the sort by a shorter window left a backlog of
            // fifty-five messages permanently unjudged beside four zeroes.
            //
            // The TRAYS are a desk, and a desk is not an archive. A server alert from four
            // weeks ago has resolved itself twice over; a meeting that has been and gone
            // wants nothing. Listing them under "muss man wissen" is how a page fills up
            // with things that no longer ask for anything, which is what it did. So the
            // lists are bounded by the remembering window -- the one control the page
            // already carries -- and what falls outside it is counted, not listed.
            DateTime now = DateTime.Now;
            DateTime keeping = now - (_session.Desk?.Retention ?? DeskStore.DefaultRetention);

            DeskWindow window = _session.DeskWindow ?? new DeskWindow();
            DateTime fresh = window.Since(now);

            DeskStore? store = _session.Desk;

            DeskTally tally = store is { } counted ? counted.Tally(keeping) : DeskTally.Nothing;

            // The same query over the shorter horizon. Subtracting gives what is older than
            // the window per verdict, which is the number the page needs to say "and this
            // much is still lying there" -- derived from the totals rather than from what
            // the four-row list happened to show, because a tray can also be short simply
            // because there are more than four recent ones.
            DeskTally recent = store is { } lately ? lately.Tally(fresh) : DeskTally.Nothing;

            // The notification, before the page: it has to work whether or not anybody is
            // looking at the page, and the page is the case that needs it least.
            // Fresh first, older only when nothing fresh is waiting.
            //
            // One query per tray does it: the newest four with that verdict over everything
            // the store holds, each row knowing whether it falls inside the period. Newest
            // first means the fresh ones lead by themselves, and a tray only reaches back
            // when it would otherwise stand empty.
            //
            // Both of the earlier arrangements were wrong in opposite directions. Unbounded,
            // the trays filled with month-old alerts whose reasons were paraphrased subject
            // lines. Bounded hard, the summaries were finally worth reading and could not be
            // seen at all -- two rows inside a fortnight against fifty-three in the store.
            IReadOnlyList<VorzimmerWindow.DeskEntry> answers =
                Judged(store, keeping, fresh, DeskVerdict.Answer);

            IReadOnlyList<VorzimmerWindow.DeskEntry> notes =
                Judged(store, keeping, fresh, DeskVerdict.Information);

            AnnounceChange(reading.Counts, tally);

            // The day and what is late come straight out of the walk, not out of the store:
            // both are facts Outlook holds, nothing about them is judged, and the walk has
            // just read them. The trays are the lookup; these are the reading.
            IReadOnlyList<VorzimmerWindow.DayEntry> day = Day(reading, now);
            IReadOnlyList<VorzimmerWindow.DueEntry> late = Late(reading);

            // What the day list is missing until the look-ahead has run: the mail about each
            // meeting. Kept for the day rather than looked up on every tick, because it
            // costs a model call and a mailbox search and a meeting's post does not change
            // every three minutes.
            _dayToday = day;

            // What is already known about each meeting, folded back in. Without this a
            // count three minutes later would wipe the lines the look-ahead just drew.
            if (_dayAboutFor == now.Date && _dayAbout.Count > 0)
                day = WithAbout(day);

            _vorzimmer?.Show(
                reading.Counts,
                _deskBaseline,
                window.Describe(Words),
                tally,
                answers,
                notes,
                day,
                late,

                // What the four rows leave out, and where the line between fresh and older
                // falls. A truncation count now, not an age count: the trays reach back on
                // their own, so what is missing is simply everything past the fourth row.
                new VorzimmerWindow.Backlog(
                    Answer: Math.Max(0, tally.Answer - answers.Count),
                    Information: Math.Max(0, tally.Information - notes.Count),
                    Overdue: Math.Max(0, reading.Counts.OverdueTasks - late.Count),
                    Days: window.Days),

                // The watcher's own settings, from the same clamped values the timer uses.
                // Read here rather than restated on the page, which is where they were and
                // where they would have gone on saying three after somebody set ten.
                new VorzimmerWindow.WatchTiming(
                    Every: Math.Clamp(_watchSettings.EveryMinutes, 1, 60),
                    Lead: Math.Clamp(_watchSettings.LeadMinutes, 1, 240),
                    Quiet: Math.Clamp(_watchSettings.QuietMinutes, 0, 240)));

            if (saveBaseline)
                SaveDesk(reading.Counts);

            // The look-ahead: what came in about the meetings that have not happened yet.
            // Only when somebody is looking at the page -- the page opened, or the button
            // pressed -- because it is a model call and a search, and neither belongs on a
            // three-minute timer for a window nobody has open.
            if (thenSort)
                _ = LookAheadAsync(reading, now);

            if (thenSort)
                _ = JudgeSomeMailAsync();
        }
        catch (Exception ex)
        {
            // One line in the console and no further attempt. Outlook not running, or a
            // mailbox that has gone offline, is a normal state of the world; the page keeps
            // its dashes and the next tick tries again.
            AddRow(GlyphWarning, $"could not count the desk: {ex.Message}", "desk", isWarning: true);
            _vorzimmer?.Trouble(Words.CouldNotCount);
        }
        finally
        {
            _counting = false;
        }
    }

    /// <summary>
    /// A row was pressed: open that mail in Outlook.
    ///
    /// <b>The desk id arrives, never an Outlook handle.</b> The page is a web view; handing
    /// it an EntryID would be handing a live handle into somebody's mailbox to a document. It
    /// gets the cache's own id and this resolves it, which also means a stale row fails here,
    /// where there is something sensible to say about it, rather than there.
    ///
    /// <b>And EntryID is the field documented as going stale.</b> It changes when the item is
    /// filed, so a row that has been sitting on screen while the mail was moved will fail to
    /// open -- said plainly and followed by a fresh count, because the next thing the reader
    /// will do is look at the row again.
    /// </summary>
    private void OpenFromDesk(string id)
    {
        if (_session?.Outlook is null)
            return;

        // A mail the desk never saw, found by a search or by the look-ahead. The page was
        // given a token into that result rather than the handle itself, and this is where
        // the token is spent. Two pools, because a new search must not invalidate the
        // links under today's appointments.
        foreach ((string prefix, List<MailSummary> pool) in new[]
        {
            (FoundPrefix, _found),
            (DayFoundPrefix, _dayFound),
        })
        {
            if (!id.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (int.TryParse(id.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                && index >= 0
                && index < pool.Count)
            {
                _ = OpenItAsync(pool[index].EntryId, pool[index].Subject);
            }
            else
            {
                AddRow(GlyphWarning, $"nothing to open for '{id}': the result it came from has been replaced", "desk", isWarning: true);
            }

            return;
        }

        if (_session.Desk is not { } store)
            return;

        DeskObject? thing = store.Get(id);

        if (thing?.EntryId is not { Length: > 0 } entryId)
        {
            AddRow(GlyphWarning, $"nothing to open for '{id}'", "desk", isWarning: true);
            return;
        }

        _ = OpenItAsync(entryId, thing.Subject);
    }

    private async Task OpenItAsync(string entryId, string subject)
    {
        try
        {
            await _session!.Outlook!.OpenMailAsync(entryId).ConfigureAwait(true);

            AddRow(GlyphTool, $"opened in Outlook: {Oneline(subject)}", "desk");
        }
        catch (Exception ex)
        {
            AddRow(
                GlyphWarning,
                $"could not open it: {ex.Message}. The handle may be stale -- Outlook changes "
                    + "it when a message is filed. Counting again.",
                "desk",
                isWarning: true);

            _ = CountTheDeskAsync(saveBaseline: false);
        }
    }

    /// <summary>The rows whose long form is being written right now, so a double click is one call.</summary>
    private readonly HashSet<string> _expanding = new(StringComparer.Ordinal);

    /// <summary>
    /// The long form of one mail: the whole conversation as an overview, written once and
    /// kept.
    ///
    /// <b>On demand, one call, cached on the row.</b> The sentence beside the verdict costs
    /// a model call per unread mail and is paid on a timer; this costs one per mail that
    /// somebody actually opened, and is paid once. The row keeps the digest and the number
    /// of messages it covered: the second opening is a lookup, and a thread that has grown
    /// since is read again because the overview would otherwise stop before the reply.
    ///
    /// <b>The whole thread, both directions.</b> The conversation is read out of the inbox
    /// and the sent items together, so what was already answered from this desk is in the
    /// overview -- the "ZU TUN" block is wrong without it. Each message is read to a bound,
    /// because a reply quotes the thread beneath it and the twelfth copy of the first mail
    /// adds nothing but prompt time.
    /// </summary>
    private async Task ExpandAsync(string id, bool again)
    {
        if (_session?.Desk is not { } store || _vorzimmer is null)
            return;

        if (!_expanding.Add(id))
            return;

        try
        {
            DeskObject? mail = store.Get(id);

            if (mail is null || mail.EntryId is not { Length: > 0 } handle)
            {
                _vorzimmer.Digest(new VorzimmerWindow.DigestOutcome(id, "failed", string.Empty, Words.ThreadFailed));
                return;
            }

            if (_session.Outlook is not { } outlook)
            {
                _vorzimmer.Digest(new VorzimmerWindow.DigestOutcome(id, "failed", string.Empty, Words.OutlookUnreachable));
                return;
            }

            _vorzimmer.Digest(new VorzimmerWindow.DigestOutcome(id, "reading", string.Empty, Words.ReadingThread));

            IReadOnlyList<MailSummary> heads = await outlook
                .ReadThreadAsync(handle, DeskTriage.DigestMessages * 2)
                .ConfigureAwait(true);

            // Newest kept when the thread is longer than the bound: the end of a
            // conversation is where it stands, the beginning is quoted in the end anyway.
            if (heads.Count > DeskTriage.DigestMessages)
                heads = [.. heads.Skip(heads.Count - DeskTriage.DigestMessages)];

            // What is kept is good enough when the thread has not grown since it was
            // written. Checked after the thread is listed rather than before, because the
            // count is the check.
            if (!again
                && mail.Digest is { Length: > 0 } kept
                && mail.DigestMessages >= Math.Max(1, heads.Count))
            {
                _vorzimmer.Digest(new VorzimmerWindow.DigestOutcome(
                    id, "ready", kept, DigestNote(mail.DigestMessages, null)));
                return;
            }

            Shellvis.Core.Office.OutlookClient.Mailbox me = await outlook
                .OwnMailboxAsync()
                .ConfigureAwait(true);

            var thread = new List<DeskTriage.ThreadMessage>();

            foreach (MailSummary head in heads)
            {
                string body = string.Empty;

                try
                {
                    MailFacing facing = await outlook
                        .ReadFacingAsync(head.EntryId, maxChars: DeskTriage.DigestChars)
                        .ConfigureAwait(true);

                    body = facing.Body;
                }
                catch (Exception)
                {
                    // One message that will not open costs its text and not the overview.
                }

                bool own = me.Address is { Length: > 0 } mine
                    && (head.SenderAddress.Equals(mine, StringComparison.OrdinalIgnoreCase)
                        || head.From.Contains(me.Name, StringComparison.OrdinalIgnoreCase));

                thread.Add(new DeskTriage.ThreadMessage(head.From, head.Received, head.Subject, body, own));
            }

            if (thread.Count == 0)
            {
                _vorzimmer.Digest(new VorzimmerWindow.DigestOutcome(id, "failed", string.Empty, Words.ThreadFailed));
                return;
            }

            string owner = me.Address is { Length: > 0 } ? $"{me.Name} <{me.Address}>" : me.Name;

            var said = new System.Text.StringBuilder();

            await _session.AskAsideAsync(
                DeskTriage.Digest(mail, thread, owner, MailboxLanguage),
                agentEvent =>
                {
                    if (agentEvent is Shellvis.Core.Agent.AgentEvent.AssistantMessage message)
                        said.Append(message.Text);
                },
                CancellationToken.None,
                withTools: false).ConfigureAwait(true);

            string digest = said.ToString().Trim();

            if (digest.Length == 0)
            {
                _vorzimmer.Digest(new VorzimmerWindow.DigestOutcome(id, "failed", string.Empty, Words.ThreadFailed));
                return;
            }

            DateTime now = DateTime.Now;
            store.Digest(id, digest, thread.Count, now);

            AddRow(GlyphTool, $"read the conversation behind '{Oneline(mail.Subject)}': {thread.Count} message(s)", "desk");

            _vorzimmer.Digest(new VorzimmerWindow.DigestOutcome(
                id, "ready", digest, DigestNote(thread.Count, now)));
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"could not read the conversation for '{id}': {ex.Message}", "desk", isWarning: true);
            _vorzimmer?.Digest(new VorzimmerWindow.DigestOutcome(id, "failed", string.Empty, Words.ThreadFailed));
        }
        finally
        {
            _expanding.Remove(id);
        }
    }

    private static string DigestNote(int messages, DateTime? when) =>
        messages.ToString(CultureInfo.CurrentCulture) + Words.MessagesWord
            + (when is { } at ? " · " + Words.ReadAt + at.ToString("HH:mm", CultureInfo.CurrentCulture) : string.Empty);

    /// <summary>The prefix of a row id that points into the last search rather than the store.</summary>
    private const string FoundPrefix = "found:";

    /// <summary>The same for a mail the look-ahead found about an appointment.</summary>
    private const string DayFoundPrefix = "dayfound:";

    /// <summary>How many hits the search panel shows before the rest is a count.</summary>
    /// <remarks>
    /// Ten. More than a tray, because a search is a question somebody asked and the answer
    /// is allowed more room than a tray that is merely there; fewer than a listing, because
    /// the page is still a desk and not a mail client. The count says how much more there is.
    /// </remarks>
    private const int HitsShown = 10;

    /// <summary>
    /// The messages the last search found that the desk did not already know, so a row
    /// pressed on the page can be opened without the page ever holding the handle.
    /// </summary>
    private readonly List<MailSummary> _found = [];

    /// <summary>The same pool for the look-ahead, kept apart so a search cannot clear it.</summary>
    private readonly List<MailSummary> _dayFound = [];

    /// <summary>What the mail about each appointment came to, keyed by the appointment's id.</summary>
    private readonly Dictionary<string, VorzimmerWindow.DayEntry> _dayAbout = new(StringComparer.Ordinal);

    /// <summary>Which day <see cref="_dayAbout"/> describes, so it is not carried into tomorrow.</summary>
    private DateTime _dayAboutFor = DateTime.MinValue;

    /// <summary>The day as the last count found it, for redrawing it alone.</summary>
    private IReadOnlyList<VorzimmerWindow.DayEntry> _dayToday = [];

    private bool _lookingAhead;

    /// <summary>
    /// Find the mail about today's remaining meetings, and hang it under them.
    ///
    /// <b>This is the look-ahead rule, and it needed a query it did not have.</b> "Before an
    /// appointment: what it is, who is in it, and what came in about it since it was booked."
    /// Searching for the appointment's own title finds the invitation and nothing else -- the
    /// mail that matters before the Linux Team Weekly says "Kernel-Update KW 37", which
    /// shares no word with the meeting's name. So the model first writes what the mail about
    /// this meeting would say, in the mailbox's language and in English, and the desk and the
    /// mailbox are searched with those words as well as with the organiser's name.
    ///
    /// <b>Only what has not happened yet, and at most three.</b> A reminder after the meeting
    /// is worthless, and the morning's first three are what a person can still prepare for --
    /// the same bound the Viva briefing settled on. Each one costs a mailbox search, and a
    /// page that takes half a minute to finish drawing is a page that looks broken.
    ///
    /// <b>Kept for the day.</b> The watcher counts every few minutes; the post about a
    /// meeting does not change that often, and a model call per tick would be paid for by
    /// somebody waiting on their own question.
    /// </summary>
    private async Task LookAheadAsync(DeskReading reading, DateTime now)
    {
        if (_vorzimmer is null || _lookingAhead || _session is null)
            return;

        // Their turn first, the same rule the sorting pass follows.
        if (_session.IsBusy)
            return;

        if (_dayAboutFor != now.Date)
        {
            _dayAbout.Clear();
            _dayFound.Clear();
            _dayAboutFor = now.Date;
        }

        // What is still to come, soonest first. An all-day entry is the day's background
        // rather than a meeting to prepare for, and has nothing to search for.
        List<DeskObject> ahead = reading.Objects
            .Where(o => o.Kind == DeskKind.Appointment)
            .Where(o => (FactsOf(o)?.End ?? o.When) > now && !(FactsOf(o)?.AllDay ?? false))
            .Where(o => o.Subject is { Length: > 0 })
            .Where(o => !_dayAbout.ContainsKey(o.Id))
            .OrderBy(o => o.When)
            .Take(MeetingsLookedAhead)
            .ToList();

        if (ahead.Count == 0)
            return;

        _lookingAhead = true;

        try
        {
            AddRow(GlyphTool, $"looking for mail about {ahead.Count} meeting(s) still to come today", "desk");

            IReadOnlyDictionary<string, string> imagined = await ImagineAboutAsync(ahead)
                .ConfigureAwait(true);

            foreach (DeskObject meeting in ahead)
            {
                var hits = new List<(DateTime When, string Id, string Label)>();

                // The desk's own memory first: it carries the sentence the model wrote about
                // each mail, which is a better label than a subject line.
                try
                {
                    foreach (DeskObject known in _session.Desk?.About(
                        meeting,
                        limit: 8,
                        imagined: imagined.TryGetValue(meeting.Id, out string? document) ? document : null) ?? [])
                    {
                        if (known.Kind != DeskKind.Mail)
                            continue;

                        hits.Add((known.When, known.Id, Label(
                            known.WhoName is { Length: > 0 } name ? name : known.WhoAddress,
                            known.VerdictWhy is { Length: > 0 } why ? why : known.Subject)));
                    }
                }
                catch (Exception ex)
                {
                    AddRow(GlyphWarning, $"the desk could not be searched for '{meeting.Subject}': {ex.Message}", "desk", isWarning: true);
                }

                // Then the mailbox, for what the desk never walked past: the agenda sent
                // three weeks ago, the document from the organiser, anything already read.
                foreach (string query in Queries(meeting, imagined))
                {
                    if (_session.Outlook is not { } outlook)
                        break;

                    try
                    {
                        MailSearchResult found = await outlook
                            .SearchMailAsync(query, limit: 10)
                            .ConfigureAwait(true);

                        foreach (MailSummary mail in found.Page.Messages)
                        {
                            if (hits.Any(h => h.Label.Contains(mail.Subject, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            if (_dayFound.Any(m => m.EntryId == mail.EntryId))
                                continue;

                            _dayFound.Add(mail);

                            hits.Add((
                                mail.Received,
                                DayFoundPrefix + (_dayFound.Count - 1).ToString(CultureInfo.InvariantCulture),
                                Label(mail.From, mail.Subject)));
                        }
                    }
                    catch (Exception ex)
                    {
                        AddRow(GlyphWarning, $"mail search for '{query}' failed: {ex.Message}", "desk", isWarning: true);
                    }
                }

                // Newest first: the most recent word about a meeting is the one that changes
                // what you walk into it knowing.
                hits.Sort((a, b) => b.When.CompareTo(a.When));

                _dayAbout[meeting.Id] = new VorzimmerWindow.DayEntry(
                    Id: meeting.Id,
                    When: string.Empty,
                    What: string.Empty,
                    Where: string.Empty,
                    Note: string.Empty,
                    Past: false,
                    Next: false,
                    AboutCount: hits.Count,
                    AboutId: hits.Count > 0 ? hits[0].Id : null,
                    AboutLabel: hits.Count > 0 ? hits[0].Label : null);

                // Published per meeting rather than at the end: the first one is the next
                // one, and it is the row somebody is waiting to see.
                _vorzimmer?.Day(WithAbout(_dayToday));
            }
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"could not look ahead to today's meetings: {ex.Message}", "desk", isWarning: true);
        }
        finally
        {
            _lookingAhead = false;
        }
    }

    /// <summary>How many of today's remaining meetings are looked ahead to.</summary>
    /// <remarks>
    /// Three. The same bound the Viva briefing settled on, and for the same reason: the
    /// morning's first three are what a person can still prepare for, and each one costs a
    /// mailbox search.
    /// </remarks>
    private const int MeetingsLookedAhead = 3;

    /// <summary>What the mailbox is asked, for one meeting: two or three words at a time.</summary>
    /// <remarks>
    /// The words are ANDed by the DASL filter, so a query is a conjunction and a long one
    /// finds nothing. Two queries: the meeting's own distinctive words, and the imagined
    /// mail's best words that the title did not already carry -- which is the half that
    /// finds "Kernel-Update KW 37" under "Linux Team Weekly".
    /// </remarks>
    private static IEnumerable<string> Queries(
        DeskObject meeting,
        IReadOnlyDictionary<string, string> imagined)
    {
        IReadOnlyList<string> own = DeskTriage.Keywords(meeting.Subject, most: 2);

        if (own.Count > 0)
            yield return string.Join(" ", own);

        if (!imagined.TryGetValue(meeting.Id, out string? document))
            yield break;

        List<string> extra = DeskTriage.Keywords(document, most: 8)
            .Where(w => !own.Contains(w, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(w => w.Any(char.IsDigit))
            .ThenByDescending(w => w.Length)
            .Take(2)
            .ToList();

        if (extra.Count > 0)
            yield return string.Join(" ", extra);
    }

    /// <summary>Who it is from and what it says, on one short line.</summary>
    private static string Label(string who, string what) =>
        (who is { Length: > 0 } ? Oneline(who) + " · " : string.Empty) + Shorten(Oneline(what), 110);

    /// <summary>The day list with whatever the look-ahead has found so far folded into it.</summary>
    private IReadOnlyList<VorzimmerWindow.DayEntry> WithAbout(
        IReadOnlyList<VorzimmerWindow.DayEntry> day) =>
        day
            .Select(row => _dayAbout.TryGetValue(row.Id, out VorzimmerWindow.DayEntry? about)
                ? row with
                {
                    AboutCount = about.AboutCount,
                    AboutId = about.AboutId,
                    AboutLabel = about.AboutLabel,
                }
                : row)
            .ToList();

    /// <summary>
    /// The imagined mail for each meeting, or nothing when the model could not be asked.
    /// Never throws: this is a better query, not the only one.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ImagineAboutAsync(
        IReadOnlyList<DeskObject> meetings)
    {
        if (_session is null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var said = new System.Text.StringBuilder();

        try
        {
            await _session.AskAsideAsync(
                DeskTriage.ImagineAboutAppointments(meetings, MailboxLanguage),
                agentEvent =>
                {
                    if (agentEvent is Shellvis.Core.Agent.AgentEvent.AssistantMessage message)
                        said.Append(message.Text);
                },
                CancellationToken.None,
                withTools: false).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"could not imagine the mail about today's meetings: {ex.Message}", "desk", isWarning: true);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return DeskTriage.ReadImagined(said.ToString(), meetings);
    }

    /// <summary>
    /// Answer a question typed into the page, from two places at once.
    ///
    /// <b>The desk first, then Outlook, and the two are merged rather than shown apart.</b>
    /// The store holds three months of what the walk passed -- mail, tickets, tasks,
    /// appointments -- and for the mail it holds the model's verdict and its sentence. That
    /// is the part of the answer nothing else can give. Outlook's own search reaches
    /// everything the desk never saw: older mail, filed mail, sent mail. A hit the desk knew
    /// keeps its sentence; one only Outlook found gets the message's first line and is marked
    /// as a preview, so the two are told apart on the page without being put in two lists.
    ///
    /// <b>The answer says where it came from.</b> How many the desk remembered, and whether
    /// the rest came from the search index or from a walk of the newest messages. The mail
    /// tools learned this the hard way: an empty result that does not say how hard it looked
    /// is indistinguishable from a search that silently failed.
    ///
    /// <b>Nothing is written.</b> Searching does not touch the store and does not judge
    /// anything; a hit that was never judged stays unjudged until the sorting pass reaches it.
    /// </summary>
    /// <summary>Which search is current, so a slow second phase cannot overwrite a newer answer.</summary>
    private int _searchGeneration;

    private async Task SearchTheDeskAsync(string words)
    {
        string query = words.Trim();

        if (query.Length < 2)
            return;

        int generation = ++_searchGeneration;

        // The two halves of the answer, kept apart until they are drawn: what the desk
        // remembered, and what only Outlook found. Both grow across the two phases below
        // and are published twice -- once with the direct hits, once with the imagined
        // document's hits added -- so the reader sees something at once and more shortly.
        var remembered = new List<DeskObject>();
        var mails = new List<MailSummary>();
        var said = new List<string>();

        void Publish(string? alsoSearched)
        {
            if (generation != _searchGeneration)
                return;

            var hits = new List<(DateTime When, VorzimmerWindow.Hit Row)>();

            foreach (DeskObject thing in remembered)
            {
                hits.Add((thing.When, new VorzimmerWindow.Hit(
                    Id: thing.Id,
                    Who: thing.WhoName is { Length: > 0 } name ? name
                        : thing.WhoAddress is { Length: > 0 } address ? address
                        : KindWord(thing.Kind),
                    When: Stamp(thing.When),
                    What: thing.Subject,
                    Why: thing.VerdictWhy ?? string.Empty,
                    Preview: false)));
            }

            _found.Clear();

            foreach (MailSummary mail in mails)
            {
                _found.Add(mail);

                hits.Add((mail.Received, new VorzimmerWindow.Hit(
                    Id: FoundPrefix + (_found.Count - 1).ToString(CultureInfo.InvariantCulture),
                    Who: mail.From,
                    When: Stamp(mail.Received),
                    What: mail.Subject,
                    Why: Shorten(Oneline(mail.Preview), 220),
                    Preview: true)));
            }

            hits.Sort((a, b) => b.When.CompareTo(a.When));

            List<VorzimmerWindow.Hit> rows = hits.Take(HitsShown).Select(h => h.Row).ToList();

            var parts = new List<string>(said);

            if (remembered.Count > 0)
                parts.Insert(0, remembered.Count.ToString(CultureInfo.CurrentCulture) + Words.FromMemory);

            if (alsoSearched is { Length: > 0 })
                parts.Add(Words.AlsoSearched + alsoSearched);

            string where = hits.Count.ToString(CultureInfo.CurrentCulture) + Words.HitsWord
                + (parts.Count > 0 ? " · " + string.Join(" · ", parts) : string.Empty);

            _vorzimmer?.Found(new VorzimmerWindow.SearchOutcome(
                Query: query,
                Rows: rows,
                More: Math.Max(0, hits.Count - rows.Count),
                Where: where));
        }

        // Already on the desk: the store's row wins, because it carries the sentence and
        // the id that survives filing. Matched on the handle first and on subject-and-minute
        // second, since the handle goes stale.
        bool Known(MailSummary mail) =>
            remembered.Any(r =>
                (r.EntryId is { Length: > 0 } handle && handle == mail.EntryId)
                || (r.Subject == mail.Subject && Math.Abs((r.When - mail.Received).TotalMinutes) < 1))
            || mails.Any(m => m.EntryId == mail.EntryId);

        void AddRemembered(IEnumerable<DeskObject> found)
        {
            foreach (DeskObject thing in found)
            {
                if (!remembered.Any(r => r.Id == thing.Id))
                    remembered.Add(thing);
            }
        }

        // ------------------------------------------------------------ phase one: as typed
        try
        {
            AddRemembered(_session?.Desk?.Search(query, since: null, limit: 40) ?? []);
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"the desk could not be searched: {ex.Message}", "desk", isWarning: true);
        }

        bool outlookAnswered = false;

        if (_session?.Outlook is { } outlook)
        {
            try
            {
                MailSearchResult found = await outlook
                    .SearchMailAsync(query, limit: 40)
                    .ConfigureAwait(true);

                foreach (MailSummary mail in found.Page.Messages)
                {
                    if (!Known(mail))
                        mails.Add(mail);
                }

                said.Add(found.Path == MailSearchPath.Index
                    ? Words.ViaIndex + found.Folders.ToString(CultureInfo.CurrentCulture) + Words.FoldersWord
                    : Words.ViaWalkStart + found.Scanned.ToString(CultureInfo.CurrentCulture) + Words.ViaWalkEnd);

                outlookAnswered = true;
            }
            catch (Exception ex)
            {
                // Outlook not running, or a store that refuses the query. The desk's half of
                // the answer still stands, and the sentence says the other half is missing
                // rather than letting an incomplete list pass for a complete one.
                said.Add(Words.SearchFailed);
                AddRow(GlyphWarning, $"mail search failed: {ex.Message}", "desk", isWarning: true);
            }
        }
        else
        {
            said.Add(Words.OutlookUnreachable);
        }

        Publish(alsoSearched: null);

        // ------------------------------------------------- phase two: as the answer reads
        //
        // HyDE. The model writes the document being looked for -- "Auftragsbestaetigung
        // 4711, Lieferung KW 38" for "die Bestellung von Weber" -- and its distinctive words
        // are searched as well. A question and its answer rarely share vocabulary, and the
        // first phase can only find what shares the reader's words. Skipped while the model
        // is answering the reader's own question: their turn wins, and the direct hits are
        // already on the page.
        if (_session is null || _session.IsBusy)
            return;

        string imagined;

        try
        {
            var text = new System.Text.StringBuilder();

            await _session.AskAsideAsync(
                DeskTriage.ImagineOne(query, MailboxLanguage),
                agentEvent =>
                {
                    if (agentEvent is Shellvis.Core.Agent.AgentEvent.AssistantMessage message)
                        text.Append(message.Text);
                },
                CancellationToken.None,
                withTools: false).ConfigureAwait(true);

            imagined = text.ToString();
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"could not imagine the document being searched for: {ex.Message}", "desk", isWarning: true);
            return;
        }

        if (generation != _searchGeneration)
            return;

        // Only the words the reader did not already type: the rest was phase one.
        List<string> typed = [.. OutlookClient.Words(query)];

        List<string> extra = DeskTriage.Keywords(imagined, most: 8)
            .Where(w => !typed.Contains(w, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (extra.Count == 0)
            return;

        try
        {
            foreach (string word in extra)
                AddRemembered(_session.Desk?.Search(word, since: null, limit: 12) ?? []);
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"the desk could not be searched with the imagined words: {ex.Message}", "desk", isWarning: true);
        }

        // Outlook is asked with the numbers and names first, three words at most: every
        // word is a search across every folder in scope, and the imagined document's most
        // distinctive words are the ones that find the confirmation.
        if (outlookAnswered && _session.Outlook is { } again)
        {
            foreach (string word in extra
                .OrderByDescending(w => w.All(char.IsDigit))
                .ThenByDescending(w => w.Length)
                .Take(3))
            {
                try
                {
                    MailSearchResult found = await again
                        .SearchMailAsync(word, limit: 15)
                        .ConfigureAwait(true);

                    foreach (MailSummary mail in found.Page.Messages)
                    {
                        if (!Known(mail))
                            mails.Add(mail);
                    }
                }
                catch (Exception ex)
                {
                    AddRow(GlyphWarning, $"mail search for '{word}' failed: {ex.Message}", "desk", isWarning: true);
                }
            }
        }

        AddRow(GlyphTool, $"search widened with the imagined document: {string.Join(" ", extra)}", "desk");

        Publish(alsoSearched: string.Join(" ", extra));
    }

    private static string KindWord(DeskKind kind) => kind switch
    {
        DeskKind.Task => Words.KindTask,
        DeskKind.Appointment => Words.KindAppointment,
        DeskKind.Ticket => Words.KindTicket,
        _ => Words.UnknownSender,
    };

    /// <summary>
    /// The time alone for today, the date as well for anything older. A column of
    /// "04.09. 09:12" for mail that all arrived this morning spends the width on the half
    /// that is the same in every row.
    /// </summary>
    private static string Stamp(DateTime when) =>
        when.Date == DateTime.Now.Date
            ? when.ToString("HH:mm", CultureInfo.CurrentCulture)
            : when.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture);

    private static string Shorten(string text, int most) =>
        text.Length <= most ? text : text[..(most - 1)].TrimEnd() + "…";

    /// <summary>The desk as the previous count found it, for telling what changed.</summary>
    /// <remarks>
    /// A second comparison point, separate from the badge baseline. The badges answer "what
    /// has come in since I opened this", so their baseline has to stay put while the window
    /// is open; a notification answers "what changed just now", so its baseline is the count
    /// before this one. One field could not be both, and using one would have made either
    /// the badges reset every few minutes or the notification repeat itself for ever.
    /// </remarks>
    private DeskSnapshot? _deskLast;

    private DeskTally? _tallyLast;

    /// <summary>
    /// Say something when the desk has changed, once per look.
    ///
    /// <b>Once per look, not once per message.</b> A morning's synchronisation brought in
    /// three hundred and eighty messages on this mailbox; a notification each would be three
    /// hundred and eighty, and then the one that mattered arrives among them unread. So the
    /// change is aggregated into one line, and the count that costs attention leads it.
    ///
    /// <b>Growth only.</b> A number that fell -- mail read, a task finished -- is not news,
    /// and being congratulated for tidying up is how somebody learns to dismiss these. The
    /// same rule the badges follow.
    ///
    /// <b>Nothing on the first count of a session.</b> There is no previous state to have
    /// changed from, and announcing the whole inbox at startup is the behaviour that makes an
    /// alert worthless -- the refusal the watcher already makes on its first run.
    ///
    /// The line goes through <see cref="NoteQuietly"/> like every other announcement, so the
    /// transcript always records it and the desktop alert waits until Windows says an
    /// interruption is allowed.
    /// </summary>
    private void AnnounceChange(DeskSnapshot now, DeskTally tally)
    {
        DeskSnapshot? before = _deskLast;
        DeskTally? wasTally = _tallyLast;

        _deskLast = now;
        _tallyLast = tally;

        if (before is null || wasTally is null)
            return;

        var said = new List<string>();

        // The one that costs attention, first and in its own words.
        if (tally.Answer > wasTally.Answer)
            said.Add($"{tally.Answer - wasTally.Answer}{Words.NoticeNeedsAnswer}");

        if (now.Unread > before.Unread)
            said.Add($"{now.Unread - before.Unread}{Words.NoticeNewUnread}");

        if (now.MeetingRequests > before.MeetingRequests)
            said.Add($"{now.MeetingRequests - before.MeetingRequests}{Words.NoticeMeetingRequests}");

        if (now.OverdueTasks > before.OverdueTasks)
            said.Add($"{now.OverdueTasks - before.OverdueTasks}{Words.NoticeOverdue}");

        if (said.Count == 0)
            return;

        string headline = "Vorzimmer: " + string.Join(", ", said);

        NoteQuietly(headline, "desk", isProblem: false, headline: headline);
    }

    /// <summary>How many real entries a tray shows before it is a list rather than a hint.</summary>
    /// <remarks>
    /// Four. The tray is beside a rule about not listing thirty things, and a page that
    /// then lists thirty things has argued with itself. Four is enough to recognise what is
    /// waiting; the count above says how much more there is.
    /// </remarks>
    private const int EntriesPerTray = 4;

    /// <summary>
    /// One tray, as the page shows it: what the model put under this verdict.
    ///
    /// <b>Read from the store, not derived from the sender.</b> The previous version asked
    /// whether the address looked like a machine, which is a fact about the envelope and not
    /// an answer to "does this need something from me". The verdict is a judgement about the
    /// contents, made once per message by the model and kept -- so this is a lookup.
    /// </summary>
    /// <param name="since">How far back to look at all: everything the store keeps.</param>
    /// <param name="fresh">
    /// Where the period ends. Rows older than this are marked, not excluded -- the page
    /// leads with what is fresh and reaches back only when a tray would stand empty.
    /// </param>
    private static IReadOnlyList<VorzimmerWindow.DeskEntry> Judged(
        DeskStore? store,
        DateTime since,
        DateTime fresh,
        DeskVerdict verdict)
    {
        if (store is null)
            return [];

        return store.Judged(verdict, since, EntriesPerTray)
            .Select(t =>
            {
                // The earlier thing the sorting tied this to, looked up so the row can say
                // what it is and open it. One Get per row that has one; most rows have none.
                DeskObject? tied = t.Related is { Length: > 0 } relatedId ? store.Get(relatedId) : null;

                return new VorzimmerWindow.DeskEntry(
                    Id: t.Id,
                    Who: t.WhoName is { Length: > 0 } name ? name : t.WhoAddress,
                    When: Stamp(t.When),
                    What: t.Subject,
                    Why: t.VerdictWhy ?? string.Empty,

                    // Marked rather than filtered. The rows are newest first, so the fresh ones
                    // lead and the page can draw a line before the first of these.
                    Old: t.When < fresh,

                    RelatedId: tied?.Id,
                    RelatedLabel: tied is null
                        ? null
                        : Words.SeeAlso
                            + tied.When.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture)
                            + " · "
                            + (tied.Subject is { Length: > 0 } about ? about : KindWord(tied.Kind)));
            })
            .ToList();
    }

    /// <summary>How many appointments the day list shows before it stops being a day.</summary>
    /// <remarks>
    /// Twelve, which is more than a working day has and fewer than a shared calendar can
    /// hold. The list is the shape of the day and is meant to be complete, so the cap is a
    /// guard against a calendar that is not one person's rather than a design figure.
    /// </remarks>
    private const int AppointmentsShown = 12;

    /// <summary>
    /// The day as the page lists it: every appointment of today in order, each knowing
    /// whether it is over, running, next, or still to come.
    /// </summary>
    /// <remarks>
    /// <b>The words are decided here, in code, not on the page.</b> "Vorbei", "läuft" and
    /// "in 40 Min." are date arithmetic against the count's own clock, and date arithmetic
    /// is the thing this application has got wrong before. The page receives the row already
    /// worded and draws it.
    ///
    /// <b>The accent goes to one row.</b> The first appointment still to come is what a
    /// person looks for on a day list, so it is the only one marked. An all-day entry never
    /// takes it: "Urlaub Müller" is the day's background, not its next event.
    /// </remarks>
    private static IReadOnlyList<VorzimmerWindow.DayEntry> Day(DeskReading reading, DateTime now)
    {
        var rows = new List<VorzimmerWindow.DayEntry>();
        bool nextFound = false;

        foreach (DeskObject item in reading.Objects
            .Where(o => o.Kind == DeskKind.Appointment)
            .OrderBy(o => o.When)
            .Take(AppointmentsShown))
        {
            AppointmentFacts? facts = FactsOf(item);

            bool allDay = facts?.AllDay ?? false;
            DateTime end = facts?.End ?? item.When;

            bool past = !allDay && end <= now;
            bool running = !allDay && item.When <= now && now < end;
            bool next = !allDay && !past && !running && !nextFound;

            if (next)
                nextFound = true;

            string note = allDay ? Words.AllDay
                : past ? Words.Past
                : running ? Words.Running
                : next ? Until(item.When - now)
                : string.Empty;

            rows.Add(new VorzimmerWindow.DayEntry(
                Id: item.Id,
                When: allDay ? string.Empty : item.When.ToString("HH:mm", CultureInfo.CurrentCulture),
                What: item.Subject,
                Where: item.State,
                Note: note,
                Past: past,
                Next: next));
        }

        return rows;
    }

    /// <summary>"in 40 Min.", "in 2 Std. 5 Min." -- how long until something starts.</summary>
    private static string Until(TimeSpan until)
    {
        int minutes = (int)Math.Round(until.TotalMinutes);

        if (minutes <= 0)
            return Words.Running;

        return minutes < 60
            ? Words.InPrefix + minutes.ToString(CultureInfo.CurrentCulture) + Words.MinutesShort
            : Words.InPrefix + (minutes / 60).ToString(CultureInfo.CurrentCulture) + Words.HoursShort
                + (minutes % 60).ToString(CultureInfo.CurrentCulture) + Words.MinutesShort;
    }

    private static AppointmentFacts? FactsOf(DeskObject item)
    {
        if (item.Facts is not { Length: > 0 } json)
            return null;

        try
        {
            return JsonSerializer.Deserialize<AppointmentFacts>(json);
        }
        catch (JsonException)
        {
            // A row written by an older build, or by a tool that stored something else
            // here. Without the end the row is still a row; it just cannot say "vorbei".
            return null;
        }
    }

    /// <summary>
    /// What is late, as rows: the overdue tasks, longest overdue first, four of them.
    /// </summary>
    /// <remarks>
    /// Longest overdue first rather than newest, because that is the order in which they
    /// embarrass: a task three weeks past its date is the one somebody is waiting on. The
    /// date is written in the language of the page and short -- "fällig 09.09.", "due 9 Sep"
    /// -- because the tray is narrow and the year is almost always this one. A task more
    /// than a year late gets its year, since "09.09." would then be a lie by omission.
    /// </remarks>
    private static IReadOnlyList<VorzimmerWindow.DueEntry> Late(DeskReading reading)
    {
        var culture = new CultureInfo(Words.LanguageTag);
        DateTime today = DateTime.Now.Date;

        return reading.Objects
            .Where(o => o.Kind == DeskKind.Task && o.State == "overdue" && o.Due is not null)
            .OrderBy(o => o.Due)
            .Take(EntriesPerTray)
            .Select(o => new VorzimmerWindow.DueEntry(
                Id: o.Id,
                What: o.Subject,
                Due: Words.DuePrefix + (today - o.Due!.Value.Date).TotalDays switch
                {
                    > 365 => o.Due.Value.ToString("d", culture),
                    _ => o.Due.Value.ToString(Words.DueFormat, culture),
                }))
            .ToList();
    }

    /// <summary>
    /// Write what the walk saw into the remembered desk, and forget what is out of date.
    ///
    /// <b>A ticket a mail mentions gets a row of its own, even though the walk never saw the
    /// ticket.</b> Without it the link points at nothing and the useful question -- "what do
    /// we know about IMIT-1234" -- comes back empty while three notifications about it sit in
    /// the cache. The row starts almost blank on purpose: the key is all that is actually
    /// known, and filling the subject in from the mail's subject would be inventing a title
    /// for somebody else's ticket. The Jira tools overwrite it with the real thing the first
    /// time the ticket is fetched.
    ///
    /// <b>Pruning rides along here rather than on a schedule of its own.</b> The retention is
    /// then enforced by the thing that also fills the store, so there is no arrangement in
    /// which the cache grows because a separate job stopped running.
    /// </summary>
    private void Remember(DeskReading reading)
    {
        try
        {
            if (_session?.Desk is not { } store)
                return;

            foreach (DeskObject thing in reading.Objects)
            {
                store.See(thing);

                if (thing.TicketKey is not { Length: > 0 } key)
                    continue;

                string ticketId = DeskObject.MakeId(DeskKind.Ticket, key);

                store.See(new DeskObject(
                    Id: ticketId,
                    Kind: DeskKind.Ticket,
                    Subject: string.Empty,
                    WhoName: string.Empty,
                    WhoAddress: string.Empty,
                    When: thing.When,
                    Due: null,
                    State: string.Empty,
                    TicketKey: key,
                    Thread: null,
                    EntryId: null,
                    Facts: null,
                    Enrichment: null,
                    FirstSeen: thing.When,
                    LastSeen: reading.Counts.TakenAt));

                store.Link(thing.Id, ticketId, "about");
            }

            // What the walk did NOT see is what has been read since.
            //
            // The walk enumerates unread items, so a message that has been dealt with is
            // never visited again and would keep the state it was first written with for
            // three months. Measured before this existed: the folder reported 85 unread and
            // the store counted 399, and every number on the page inherited the difference.
            //
            // Bounded by what the scan actually covered. Beyond the oldest message it looked
            // at, "not seen" is no evidence at all, and marking those read would hide mail
            // that is genuinely waiting.
            var stillUnread = reading.Objects
                .Where(o => o.Kind == DeskKind.Mail)
                .Select(o => o.Id)
                .ToHashSet(StringComparer.Ordinal);

            // AN EMPTY WALK IS TWO DIFFERENT THINGS, and treating them alike left the trays
            // full of mail that had all been read.
            //
            // The guard here used to be "stillUnread.Count > 0", so a walk that found
            // nothing skipped the marking entirely. That is right when the walk failed and
            // catastrophic when it succeeded: read everything in the inbox and the pass
            // finds no unread mail, marks nothing, and every row keeps the state it was
            // first written with for three months. Reported exactly that way -- "die
            // ungelesenen Mails sind jetzt alle gelesen aber verschwinden nicht".
            //
            // The folder's own UnReadItemCount tells the two apart. It is Outlook's number,
            // not the walk's, so it is still trustworthy when the walk returned nothing.
            int marked = 0;
            string covered = string.Empty;

            // Outlook says there IS unread mail and the walk brought none back. A failed
            // look, not an empty desk -- Restrict matching nothing silently is a failure
            // mode this project has met before -- and nothing is written on the strength
            // of it.
            bool lookFailed = stillUnread.Count == 0 && reading.Counts.Unread > 0;

            if (!lookFailed)
            {
                // WHETHER THE WALK WAS CAPPED IS THE WHOLE QUESTION, and the bound used to
                // be tied to the wrong measurement.
                //
                // It was the oldest message still unread. That is right only when the walk
                // stopped at its limit: then coverage genuinely ends there and older rows
                // cannot be proved read. When the walk ran out of unread mail first it saw
                // EVERY unread message in the folder, and "not in the set" is conclusive at
                // any age.
                //
                // Measured on the machine that reported this: Outlook held 4 unread, the
                // walk looked at 4 of a possible 200 and returned all four, the oldest of
                // them from 15:01 that afternoon -- and the store went on calling 57 rows
                // unread because 53 of them were older than 15:01. Every one had been read.
                // The trays kept them for what would have been three months.
                bool capped = reading.Counts.Scanned >= OutlookClient.DeskScan;

                DateTime from = capped
                    ? reading.Objects
                        .Where(o => o.Kind == DeskKind.Mail)
                        .Select(o => o.When)
                        .DefaultIfEmpty(reading.Counts.TakenAt)
                        .Min()
                    : reading.Counts.TakenAt - store.Retention;

                marked = store.MarkRead(stillUnread, from);

                covered = capped
                    ? $"{stillUnread.Count} still unread, scan capped at {reading.Counts.Scanned} "
                        + $"back to {from:dd.MM. HH:mm}"
                    : $"the walk saw all {stillUnread.Count} unread message(s) there are";
            }

            // Said out loud when it actually changes something, because this is the one
            // write in the pass that can be wrong in a way nothing else would show: it
            // decides that mail has been READ. Silent, it turned every recent row to read
            // while the folder still reported eighty-five unread, and the page showed four
            // zeroes with no hint of where they came from.
            if (marked > 0 && covered.Length > 0)
                AddRow(GlyphTool, $"{covered}; marked {marked} row(s) read", "desk");

            store.Prune(reading.Counts.TakenAt);
        }
        catch (Exception ex)
        {
            // The cache is an accelerator, not a source of truth. A store that cannot be
            // written costs speed and memory of what was learned; it must not cost the
            // count, which has already been taken by the time this runs.
            AddRow(GlyphWarning, $"the desk could not be remembered: {ex.Message}", "desk", isWarning: true);
        }
    }

    /// <summary>
    /// Ask how far back "remembering" should reach.
    ///
    /// A slider in the settings form rather than a text box, because there is no wrong value
    /// to type -- only a position to choose -- and rather than on the reference page, because
    /// that page reports what is on the desk and a control among the figures makes a reader
    /// wonder which numbers they can also drag.
    /// </summary>
    private async Task ConfigureRememberingAsync()
    {
        int now = _session?.DeskWindow?.Days ?? 30;

        SettingsResult answer = await SettingsWindow.ShowAsync(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            "Remembering period",
            "Shellvis keeps a quarter of a year of what it has walked past: mail, tickets, "
                + "tasks, and what it worked out about them. This is how much of that it "
                + "brings to bear when you say 'lately' -- it does not change what is kept.",
            [
                new SettingsField(
                    Key: "days",
                    Label: "Zeitraum",
                    Value: now.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Min: DeskWindow.Least,
                    Max: DeskWindow.Most,
                    Describe: days => new DeskWindow(days).Describe(Words)),
            ],
            ["Save", "Cancel"]).ConfigureAwait(true);

        if (answer.Button != "Save")
            return;

        if (answer.Values.TryGetValue("days", out string? said)
            && int.TryParse(said, System.Globalization.CultureInfo.InvariantCulture, out int days))
        {
            RememberOver(days);
        }
    }

    /// <summary>
    /// Take a new remembering period now, and keep it for next time.
    ///
    /// <b>Two writes, and they are not the same write.</b> The live window is what the tools
    /// read on their next call, so it changes immediately -- a setting that needed a restart
    /// to take effect would make the control look broken. The config file is what survives a
    /// restart, and it is written straight away rather than on shutdown, because a setting
    /// that is lost when the application is killed is a setting somebody has to make twice.
    /// </summary>
    private void RememberOver(int days)
    {
        if (_session?.DeskWindow is not { } window)
            return;

        window.Days = days;

        try
        {
            ConfigLoadResult loaded = ConfigStore.Load();

            // Clamped by the window rather than here, so there is one place that decides
            // what a legal period is.
            loaded.Config.Desk.RememberDays = window.Days;

            ConfigStore.Save(loaded.Config);

            AddRow(GlyphTool, $"looking back over {window.Describe(Words)}", "desk");
        }
        catch (Exception ex)
        {
            // The live setting stands; only its survival is lost. Said out loud, because a
            // setting that quietly reverts on the next start is worse than one that failed
            // visibly now.
            AddRow(
                GlyphWarning,
                $"the period is set for this session but could not be saved: {ex.Message}",
                "desk",
                isWarning: true);
        }
    }

    private static DeskSnapshot? LoadDesk()
    {
        try
        {
            return File.Exists(DeskStatePath)
                ? JsonSerializer.Deserialize<DeskSnapshot>(File.ReadAllText(DeskStatePath), DeskFormat)
                : null;
        }
        catch (Exception)
        {
            // A file from an older shape, or a half-written one. Treated as "nothing known",
            // which suppresses badges for one opening rather than showing wrong ones.
            return null;
        }
    }

    private void SaveDesk(DeskSnapshot desk)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DeskStatePath)!);
            File.WriteAllText(DeskStatePath, JsonSerializer.Serialize(desk, DeskFormat));

            // Held in memory too, so the badges this session shows are measured from the
            // same point the next session will measure from.
            _deskBaseline ??= desk;
        }
        catch (Exception)
        {
            // Not being able to remember the comparison point costs badges, not correctness.
        }
    }

    private static readonly JsonSerializerOptions DeskFormat = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
