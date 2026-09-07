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
            _vorzimmer.RefreshRequested += () => _ = CountTheDeskAsync(saveBaseline: false);
            _vorzimmer.RememberDaysChanged += RememberOver;
        }

        _vorzimmer.Reveal(WinRT.Interop.WindowNative.GetWindowHandle(this));

        // The stored baseline is read on the FIRST open of this session only. Later opens
        // keep the one in memory, so a window closed and reopened within a session does not
        // wipe the badges the session earned.
        if (first)
            _deskBaseline = LoadDesk();

        _ = CountTheDeskAsync(saveBaseline: true);
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
    private async Task CountTheDeskAsync(bool saveBaseline)
    {
        // Already counting: say nothing and do nothing. The count in flight ends in a
        // render, which is what puts the button back -- so the press is not lost, it is
        // answered by the other one.
        if (_counting)
            return;

        if (_session?.Outlook is null)
        {
            _vorzimmer?.Trouble("Outlook ist nicht erreichbar");
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
            DateTime since = (_session.DeskWindow ?? new DeskWindow()).Since(DateTime.Now);

            DeskTally tally = _session.Desk is { } counted
                ? counted.Tally(since)
                : DeskTally.Nothing;

            DeskStore? store = _session.Desk;

            // The notification, before the page: it has to work whether or not anybody is
            // looking at the page, and the page is the case that needs it least.
            AnnounceChange(reading.Counts, tally);

            _vorzimmer?.Show(
                reading.Counts,
                _deskBaseline,
                (_session.DeskWindow ?? new DeskWindow()).Describe(),
                tally,
                Judged(store, since, DeskVerdict.Answer),
                Judged(store, since, DeskVerdict.Information),

                // The watcher's own settings, from the same clamped values the timer uses.
                // Read here rather than restated on the page, which is where they were and
                // where they would have gone on saying three after somebody set ten.
                new VorzimmerWindow.WatchTiming(
                    Every: Math.Clamp(_watchSettings.EveryMinutes, 1, 60),
                    Lead: Math.Clamp(_watchSettings.LeadMinutes, 1, 240),
                    Quiet: Math.Clamp(_watchSettings.QuietMinutes, 0, 240)));

            if (saveBaseline)
                SaveDesk(reading.Counts);
        }
        catch (Exception ex)
        {
            // One line in the console and no further attempt. Outlook not running, or a
            // mailbox that has gone offline, is a normal state of the world; the page keeps
            // its dashes and the next tick tries again.
            AddRow(GlyphWarning, $"could not count the desk: {ex.Message}", "desk", isWarning: true);
            _vorzimmer?.Trouble("konnte nicht gezählt werden");
        }
        finally
        {
            _counting = false;
        }
    }

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
            said.Add($"{tally.Answer - wasTally.Answer} braucht eine Antwort");

        if (now.Unread > before.Unread)
            said.Add($"{now.Unread - before.Unread} neu ungelesen");

        if (now.MeetingRequests > before.MeetingRequests)
            said.Add($"{now.MeetingRequests - before.MeetingRequests} Terminanfrage(n)");

        if (now.OverdueTasks > before.OverdueTasks)
            said.Add($"{now.OverdueTasks - before.OverdueTasks} überfällig");

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
    private static IReadOnlyList<VorzimmerWindow.DeskEntry> Judged(
        DeskStore? store,
        DateTime since,
        DeskVerdict verdict)
    {
        if (store is null)
            return [];

        DateTime today = DateTime.Now.Date;

        return store.Judged(verdict, since, EntriesPerTray)
            .Select(t => new VorzimmerWindow.DeskEntry(
                Who: t.WhoName is { Length: > 0 } name ? name : t.WhoAddress,

                // The time alone for today, the date as well for anything older. A column of
                // "04.09. 09:12" for mail that all arrived this morning spends the width on
                // the half that is the same in every row.
                When: t.When.Date == today
                    ? t.When.ToString("HH:mm", CultureInfo.CurrentCulture)
                    : t.When.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture),

                What: t.Subject,
                Why: t.VerdictWhy ?? string.Empty))
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

            if (reading.Objects.Where(o => o.Kind == DeskKind.Mail).Select(o => o.When)
                    is { } moments && stillUnread.Count > 0)
            {
                store.MarkRead(stillUnread, moments.Min());
            }

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
                    Describe: days => new DeskWindow(days).Describe()),
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

            AddRow(GlyphTool, $"looking back over {window.Describe()}", "desk");
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
