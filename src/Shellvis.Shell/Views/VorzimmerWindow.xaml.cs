using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

using Shellvis.Core.Desk;
using Shellvis.Core.Office;
using Shellvis.Core.Ui;
using Shellvis.Shell.Interop;

using Windows.Graphics;

namespace Shellvis.Shell.Views;

/// <summary>
/// The rules this assistant keeps a desk by, on one page, in a window of its own.
///
/// <b>Why a window and not a link.</b> The page could have been opened in the default
/// browser with two lines of code. That would make the rules a thing outside the
/// application -- a tab among thirty other tabs, on a machine where the browser may be
/// somebody's work environment -- and a reference you have to leave the application to read
/// is a reference nobody reads. It also would have handed a local file to whatever program
/// happens to own .html on this machine, which is not a decision this application should be
/// making on the user's behalf.
///
/// <b>Why HTML at all, then.</b> Because the page is a layout: a masthead with the date, the
/// day as a list, two trays of mail under it, and a narrow column beside them for what is
/// late, what was ignored and what the count covered. Building that in XAML would be a week
/// of panels to arrive at the same picture, and the picture is the point -- the whole page
/// exists to be taken in at a glance, in the order a briefing is given: what is fixed, what
/// needs you, what is late, and the rest as a number.
///
/// <b>What renders it.</b> The <c>WebView2</c> control that ships with WinUI, over the
/// Evergreen runtime that is present on Windows 11. That runtime is the one thing this
/// window needs and cannot provide: when it is missing the window says so in words instead
/// of showing an empty panel, which is what an unhandled failure here looks like.
/// </summary>
public sealed partial class VorzimmerWindow : Window
{
    private readonly WindowShaper _shaper;

    private PointInt32 _dragFrom;
    private PointInt32 _windowFrom;
    private bool _dragging;
    private bool _loaded;
    private bool _placed;
    private bool _trimmed;
    private nint _pillHandle;

    /// <summary>
    /// The language this window speaks, settled once when it is made.
    ///
    /// Once rather than per render: the page is loaded a single time per session and its
    /// words go into the markup, so changing the setting takes effect when the window is
    /// next opened. Re-reading it on every count would let half a page say one thing and
    /// half another.
    /// </summary>
    private readonly UiText _text =
        UiLanguage.For(Shellvis.Core.Config.ConfigStore.Load().Config.Ui.Language);

    public VorzimmerWindow()
    {
        InitializeComponent();

        _shaper = new WindowShaper(Win32Interop.GetWindowFromWindowId(AppWindow.Id));

        ExtendsContentIntoTitleBar = true;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);

            // Not resizable, for the reason the answer window measured: the resize border
            // IS the frame, and the frame paints a rectangular band around the rounded
            // surface. The page is responsive and scrolls inside itself, and it opens at a
            // size that fits the work area, so what is lost is edge-dragging rather than
            // legibility.
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;

            // A document, not a command bar: it must be possible to put this behind the
            // work it describes.
            presenter.IsAlwaysOnTop = false;
        }

        _shaper.TrySoftenEdges();

        RootHost.SizeChanged += (_, _) => ClipToSurface();
        Surface.SizeChanged += (_, _) => ClipToSurface();

        CloseButton.Click += (_, _) => Hide();

        MinimiseButton.Click += (_, _) =>
        {
            if (AppWindow.Presenter is OverlappedPresenter minimisable)
                minimisable.Minimize();
        };

        // The header only. Dragging the surface is right for the answer window, whose
        // content is a text block; here the content is a web view that takes the pointer
        // itself, so a drag started inside the page would never reach this handler and the
        // one place a drag DOES work should be the one place it looks like it would.
        MakeDraggable(Header);

        // The two places this window names itself: the taskbar entry and the strip above
        // the page. The XAML carries German as a default so the designer shows something
        // real; this is what is actually seen.
        Title = _text.VorzimmerWindowTitle;
        HeaderText.Text = _text.VorzimmerWindowTitle;
    }

    private const double SurfaceRadius = 8;

    private void ClipToSurface()
    {
        if (Surface.ActualWidth < 1 || Surface.ActualHeight < 1)
            return;

        _shaper.ClipWindowRounded(SurfaceRadius);
    }

    /// <summary>Put the window in front, loading the page the first time.</summary>
    public void Reveal(nint pillHandle)
    {
        Place(pillHandle);

        AppWindow.Show();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Restore();

        // The frame comes off on the first reveal rather than in the constructor: changing
        // the style of a window that has never been shown leaves AppWindow.Show doing
        // nothing at all, which looks exactly like the button having been ignored.
        if (!_trimmed)
        {
            _trimmed = true;
            _shaper.TrimFrame(keepResizeBorder: false);
        }

        ClipToSurface();
        _shaper.BringToFront();

        // Not awaited, and not async void either: the window is already up and the page
        // arrives when the runtime is ready. LoadAsync catches the one thing that can throw
        // and puts it on screen, so nothing here can go unobserved.
        _ = LoadAsync();
    }

    /// <summary>Hide without destroying, so the page is loaded once per session.</summary>
    public void Hide() => AppWindow.Hide();

    /// <summary>
    /// Bring up the runtime and hand it the page.
    ///
    /// Once per session: the page is static, and reloading it on every reveal would throw
    /// away the reader's scroll position for no gain.
    /// </summary>
    private async Task LoadAsync()
    {
        if (_loaded)
            return;

        _loaded = true;

        try
        {
            await View.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            _loaded = false;
            View.Visibility = Visibility.Collapsed;
            Missing.Visibility = Visibility.Visible;

            MissingDetail.Text =
                "Windows renders this page with the WebView2 runtime, which is part of "
                + "Microsoft Edge and is normally already installed. Install the Evergreen "
                + "WebView2 Runtime and open this window again.\n\n"
                + ex.Message;

            return;
        }

        Microsoft.Web.WebView2.Core.CoreWebView2Settings settings = View.CoreWebView2.Settings;

        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = true;
        settings.IsZoomControlEnabled = true;

        // Nothing here navigates and nothing here opens a window -- the page has no links
        // at all. Both are refused anyway: this view exists to draw one document that ships
        // inside the executable, and a view that could be talked into loading something else
        // is a browser nobody asked for.
        View.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;

        View.CoreWebView2.NavigationStarting += (_, args) =>
        {
            if (!args.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                && !args.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
            }
        };

        // Two messages come back from the page, and they are told apart by name rather
        // than by order.
        //
        // "ready" -- the script has run. The first snapshot waits for it, because posting
        // into a document whose script has not started loses the message silently, and what
        // that looks like is a page of dashes that never fills in.
        //
        // "refresh" -- somebody pressed the button. Asked for rather than assumed: the page
        // is refreshed on the watcher's own timer, and a refresh that found nothing changed
        // looks exactly like no refresh at all, so there has to be a way to say "now".
        View.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            string message;

            try
            {
                message = args.TryGetWebMessageAsString();
            }
            catch (Exception)
            {
                // Not a string message. Nothing this page sends looks like that, so it is
                // not something to act on.
                return;
            }

            if (message == "refresh")
            {
                RefreshRequested?.Invoke();
                return;
            }

            // "open:mail:<message-id>" -- a row was pressed. The DESK id travels, never the
            // Outlook handle: the page has no business holding a handle to somebody's
            // mailbox, and the owner can resolve the id against the store it already has.
            if (message.StartsWith("open:", StringComparison.Ordinal)
                && message.Length > "open:".Length)
            {
                OpenRequested?.Invoke(message["open:".Length..]);
                return;
            }

            // "join:<desk-id>" -- the join button on an appointment was pressed. Explicit
            // by construction: only a press sends it, and joining is the one thing on this
            // page other people can see happen.
            if (message.StartsWith("join:", StringComparison.Ordinal)
                && message.Length > "join:".Length)
            {
                JoinRequested?.Invoke(message["join:".Length..]);
                return;
            }

            // "expand:<desk-id>" -- a row was opened, and the page wants the long form of
            // it. "reread:<desk-id>" is the same with the kept digest set aside, for a
            // reader who does not believe it any more.
            if (message.StartsWith("expand:", StringComparison.Ordinal)
                && message.Length > "expand:".Length)
            {
                ExpandRequested?.Invoke(message["expand:".Length..], false);
                return;
            }

            if (message.StartsWith("reread:", StringComparison.Ordinal)
                && message.Length > "reread:".Length)
            {
                ExpandRequested?.Invoke(message["reread:".Length..], true);
                return;
            }

            // "search:<words>" -- somebody typed a question into the field. The words go up
            // as typed; what is searched, and how, is the owner's decision, because it holds
            // both the store and the Outlook client and this window holds neither.
            if (message.StartsWith("search:", StringComparison.Ordinal)
                && message.Length > "search:".Length)
            {
                SearchRequested?.Invoke(message["search:".Length..]);
                return;
            }

            // "remember:30" -- the slider settled on a new window. Parsed strictly and
            // ignored when it is not a number: a page can only send what this page's script
            // sends, but a message handler that trusts its input is a habit worth not having.
            if (message.StartsWith("remember:", StringComparison.Ordinal)
                && int.TryParse(
                    message.AsSpan("remember:".Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int days))
            {
                RememberDaysChanged?.Invoke(days);
                return;
            }

            _ready = true;

            if (_pending is not null)
            {
                Send(_pending);
                _pending = null;
            }
        };

        View.NavigateToString(Page(DarkWanted() ? "dark" : "light", _text));
    }

    private bool _ready;
    private string? _pending;

    /// <summary>
    /// Raised when the page asks to be counted again.
    ///
    /// An event rather than a call into the owner: this window knows how to draw a desk and
    /// nothing about how to measure one. The pill owns the Outlook client, the comparison
    /// point and the guard against two counts at once.
    /// </summary>
    public event Action? RefreshRequested;

    /// <summary>Raised when the slider settles on a new remembering window, in days.</summary>
    public event Action<int>? RememberDaysChanged;

    /// <summary>Raised when a row was pressed, with the desk id of the thing to open.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>Raised when the search field was submitted, with the words as typed.</summary>
    public event Action<string>? SearchRequested;

    /// <summary>Raised when the join button on an appointment was pressed, with its desk id.</summary>
    public event Action<string>? JoinRequested;

    /// <summary>
    /// Raised when a row was opened for its long form: the desk id, and whether the kept
    /// digest is to be set aside and the thread read again.
    /// </summary>
    public event Action<string, bool>? ExpandRequested;

    /// <summary>Hand the page the long form of one mail, or the state of getting it.</summary>
    public void Digest(DigestOutcome digest) =>
        Send(JsonSerializer.Serialize(new { digest }, PayloadFormat));

    /// <summary>
    /// The long form of one mail as the page draws it.
    /// </summary>
    /// <param name="Id">Which row it belongs to; the same mail may sit in a tray and in a search.</param>
    /// <param name="State">"reading" while the thread is read and the model writes; "ready"; "failed".</param>
    /// <param name="Text">The overview, in the four blocks the prompt asks for. Empty until ready.</param>
    /// <param name="Note">
    /// Small print under it: how many messages it covered and when it was written, so a
    /// reader can tell a digest from this morning from one written before the reply came.
    /// </param>
    public sealed record DigestOutcome(string Id, string State, string Text, string Note);

    /// <summary>
    /// Redraw the day alone, once the mail about each appointment has been looked up.
    ///
    /// <b>Its own message, like <see cref="Sorting"/> and for the same reason.</b> The
    /// look-up costs a model call and a mailbox search, so it happens after the page is
    /// already drawn; sending a whole fresh snapshot to change three lines would recount a
    /// mailbox over COM to deliver what one list says.
    /// </summary>
    public void Day(IReadOnlyList<DayEntry> today) =>
        Send(JsonSerializer.Serialize(new { day = today }, PayloadFormat));

    /// <summary>
    /// Hand the page what a search found.
    ///
    /// Its own message rather than a field on the snapshot, for the same reason
    /// <see cref="Sorting"/> is: a search is an answer to one question asked once, and
    /// folding it into the count would redraw the whole desk to show eight rows.
    /// </summary>
    public void Found(SearchOutcome found) =>
        Send(JsonSerializer.Serialize(new { search = found }, PayloadFormat));

    /// <summary>
    /// What a search produced, ready to draw.
    /// </summary>
    /// <param name="Query">The words, echoed so the panel can say what it answers.</param>
    /// <param name="Rows">The hits, newest first, already cut to what the panel shows.</param>
    /// <param name="More">How many matched beyond those rows.</param>
    /// <param name="Where">
    /// One sentence saying where the answer came from -- how many from the desk's own
    /// memory, whether Outlook's index or a walk of the newest messages produced the rest,
    /// and how wide it looked. An empty list without this sentence is indistinguishable
    /// from a search that quietly failed, which is the failure the mail tools already name.
    /// </param>
    public sealed record SearchOutcome(
        string Query, IReadOnlyList<Hit> Rows, int More, string Where);

    /// <summary>
    /// One search hit, in the shape of a tray row.
    /// </summary>
    /// <param name="Id">
    /// A desk id when the desk remembered it; otherwise an opaque token the owner can
    /// resolve. Never an Outlook handle -- see <see cref="DeskEntry"/> for why the page is
    /// given nothing it could use on a mailbox.
    /// </param>
    /// <param name="Why">
    /// The model's sentence when there is one; the message's own first line when there is
    /// not. <paramref name="Preview"/> says which, so the page can draw the second quieter
    /// and nobody mistakes a preview for a judgement.
    /// </param>
    public sealed record Hit(
        string Id, string Who, string When, string What, string Why, bool Preview);

    /// <summary>
    /// Hand the page what is on the desk.
    ///
    /// <paramref name="before"/> is what the desk looked like when this window was last
    /// opened, and the difference is what earns a badge. Passing null means nothing is known
    /// yet, which is not the same as nothing being new -- see <c>DeskSnapshot.NewSince</c>.
    /// </summary>
    public void Show(
        DeskSnapshot now,
        DeskSnapshot? before,
        string remembering,
        DeskTally tally,
        IReadOnlyList<DeskEntry> answer,
        IReadOnlyList<DeskEntry> information,
        IReadOnlyList<DayEntry> today,
        IReadOnlyList<DueEntry> overdue,
        Backlog behind,
        WatchTiming watch)
    {
        // The verdicts join the facts in one dictionary, because the page fills every box
        // by the same key and a second mechanism for four more numbers would be four more
        // ways for a box to stay a dash.
        var counts = new Dictionary<string, int>(now.Counts)
        {
            ["answer"] = tally.Answer,
            ["information"] = tally.Information,
            ["ignore"] = tally.Ignore,
            ["pending"] = tally.Pending,
        };

        string json = JsonSerializer.Serialize(
            new Payload(
                Counts: counts,
                New: now.NewSince(before),
                // Seconds, and they are not a detail. With minutes only, pressing "count
                // it now" twice inside one minute changed nothing on screen -- same time,
                // same numbers -- so the button looked broken while working perfectly. The
                // acknowledgement has to be finer-grained than the gesture it confirms.
                TakenAt: now.TakenAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture),

                // The day, written out, in the language of the page rather than of the
                // machine: "Donnerstag" on a German page whatever the regional format says.
                // A briefing starts with the date, and it comes from the count's own clock so
                // it cannot disagree with the time beside it.
                Date: now.TakenAt.ToString("D", new CultureInfo(_text.LanguageTag)),
                ScannedNote: ScannedNote(now, _text),
                Remembering: remembering,
                Answer: answer,
                Information: information,
                Today: today,
                Overdue: overdue,
                Behind: behind,
                Watch: watch),
            PayloadFormat);

        if (_ready)
            Send(json);
        else
            _pending = json;
    }

    /// <summary>
    /// What the count actually covers, said out loud when it does not cover everything.
    ///
    /// The total comes from the folder and is always complete; the split between a person
    /// and a system is a scan with a cap on it. When the cap bites, the page has to say so
    /// -- a breakdown that quietly describes the recent two hundred of four hundred unread
    /// messages is a number that looks like an answer and is not one.
    /// </summary>
    private static string ScannedNote(DeskSnapshot desk, UiText text) =>
        desk.Scanned < desk.Unread
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{text.ScanCappedNote}{desk.Scanned}")
            : text.UnreadNote;

    /// <summary>
    /// Tell the page a sorting pass is running, or has finished.
    ///
    /// <b>Its own tiny message, not a fresh count.</b> The page only learns anything when a
    /// count is handed to it, and a count is a COM walk -- so making "a pass is running"
    /// visible by recounting would pay for a mailbox query to change one caption. This posts
    /// three fields and nothing else.
    ///
    /// It exists because the report was "warum passiert da nichts?" while a pass was in fact
    /// running: the cell said "wird der Reihe nach beurteilt" whatever was happening, so a
    /// batch in flight and an idle minute looked identical.
    /// </summary>
    public void Sorting(bool running, int howMany) =>
        Send(JsonSerializer.Serialize(
            new { sorting = running, sortingCount = howMany }, PayloadFormat));

    /// <summary>
    /// Tell the page the count could not be taken, and why.
    ///
    /// Without this the button pressed on a machine with no Outlook stays on "zählt ..."
    /// for the life of the window: the render that would have restored it never arrives.
    /// A dead control on a stale page is the worst of the three possible outcomes.
    /// </summary>
    public void Trouble(string why) =>
        Send(JsonSerializer.Serialize(new { problem = why }, PayloadFormat));

    private void Send(string json)
    {
        try
        {
            View.CoreWebView2?.PostWebMessageAsString(json);
        }
        catch (Exception)
        {
            // The view has gone, or the runtime was never there. The page simply keeps the
            // figures it had; a failure to deliver numbers is not worth taking the window
            // down for.
        }
    }

    /// <summary>
    /// The shape the page reads. Named properties rather than an anonymous type so the
    /// contract with the script is written down in one place and can be searched for.
    /// </summary>
    private sealed record Payload(
        IReadOnlyDictionary<string, int> Counts,
        IReadOnlyDictionary<string, int> New,
        string TakenAt,
        string Date,
        string ScannedNote,
        string Remembering,
        IReadOnlyList<DeskEntry> Answer,
        IReadOnlyList<DeskEntry> Information,
        IReadOnlyList<DayEntry> Today,
        IReadOnlyList<DueEntry> Overdue,
        Backlog Behind,
        WatchTiming Watch);

    /// <summary>
    /// What lies behind each tray: the count that is older than the trays reach.
    /// </summary>
    /// <remarks>
    /// <b>Why the trays are bounded at all.</b> They were not, and the page filled with mail
    /// four weeks old -- server alerts that had resolved themselves, meetings already held.
    /// A desk holds what is current; the archive is the store's job.
    ///
    /// <b>And why the remainder is still counted.</b> Because a tray that quietly shortens is
    /// the same defect in the other direction. This project has already shipped a page
    /// reading "85 unread" beside four zeroes, every figure correct and the whole useless. A
    /// bound that is announced -- "nothing recent; eleven older ones are still there" -- is
    /// a statement; one that is silent is a page pretending the backlog went away.
    /// </remarks>
    /// <param name="Answer">Older than the window, needing an answer.</param>
    /// <param name="Information">Older than the window, worth knowing.</param>
    /// <param name="Overdue">Overdue tasks past the rows the tray shows.</param>
    /// <param name="Days">How far back the trays reach, so the page can say it.</param>
    public sealed record Backlog(int Answer, int Information, int Overdue, int Days);

    /// <summary>
    /// The watcher's three intervals, in minutes, so the page can describe them instead of
    /// asserting them.
    ///
    /// They stood in the prose as words -- "alle drei Minuten" -- and would have gone on
    /// saying three after somebody set the interval to ten. Configuration written into a
    /// sentence is a sentence that lies the moment the configuration moves.
    /// </summary>
    public sealed record WatchTiming(int Every, int Lead, int Quiet);

    /// <summary>
    /// One real entry on the page: who it is from, when it arrived, what it says.
    ///
    /// Three strings and nothing else. The page shows them and cannot act on them, so it is
    /// given no id and no handle -- there is nothing for a document to do with an EntryID,
    /// and putting one in the payload would be handing out a key nobody there needs.
    /// </summary>
    /// <param name="Old">
    /// Whether this fell outside the remembering period. Marked, not withheld: the trays
    /// lead with what is fresh and reach further back only when they would otherwise stand
    /// empty, and a row shown without saying it is three weeks old is a row that misleads.
    /// </param>
    /// <param name="RelatedId">
    /// The desk id of the earlier thing the sorting pass tied this to -- the same request
    /// made before, the mail that confirmed the order this one asks about -- so the page
    /// can open it. Null when nothing was tied.
    /// </param>
    /// <param name="RelatedLabel">
    /// That thing in a few words, date first, already worded: "dazu: 03.09.2026 Bestellung
    /// 4711 bestaetigt". The date is the point -- the summary says "am 03.09." and this is
    /// where that date can be followed.
    /// </param>
    /// <param name="Digest">
    /// The long form, written in the same call as the sentence and kept on the row, so the
    /// chevron shows it at once. Null on a row judged before the analysis had two parts;
    /// the page then asks for it when the row is opened.
    /// </param>
    /// <param name="DigestNote">Small print under it: how many messages it covered.</param>
    public sealed record DeskEntry(
        string Id,
        string Who,
        string When,
        string What,
        string Why,
        bool Old = false,
        string? RelatedId = null,
        string? RelatedLabel = null,
        string? Digest = null,
        string? DigestNote = null);

    /// <summary>
    /// One appointment of the day, as the page lists it: the time, the title, the room, and a
    /// word about where it stands -- over, running, or how long until it starts.
    /// </summary>
    /// <param name="Past">Its end has passed. Drawn in the faint grey, kept so the day has a shape.</param>
    /// <param name="Next">The first one still to come. The one row on the page with an accent mark.</param>
    /// <param name="AboutCount">
    /// How much mail was found about this meeting. Zero draws nothing: a line saying "0
    /// Mails dazu" under every appointment is the kind of figure that teaches the eye to
    /// skip the row it sits in.
    /// </param>
    /// <param name="AboutId">The newest of those mails, so the line can be pressed.</param>
    /// <param name="AboutLabel">Who it is from and what it says, in a few words.</param>
    /// <param name="Teams">
    /// The appointment carries a Teams join link, so the row offers to join. The link
    /// itself stays with the owner: the page says "join this one" by id, and the owner
    /// opens the URL it read out of the calendar entry.
    /// </param>
    public sealed record DayEntry(
        string Id,
        string When,
        string What,
        string Where,
        string Note,
        bool Past,
        bool Next,
        int AboutCount = 0,
        string? AboutId = null,
        string? AboutLabel = null,
        bool Teams = false);

    /// <summary>One overdue task: what it is and when it was due.</summary>
    public sealed record DueEntry(string Id, string What, string Due);

    /// <summary>
    /// camelCase, because the script reads <c>counts</c> and <c>takenAt</c>.
    ///
    /// Worth a line: with the default policy the JSON goes out as <c>Counts</c>, the script
    /// finds nothing under the name it asks for, and every figure stays a dash. Nothing
    /// throws on either side of that.
    /// </summary>
    private static readonly JsonSerializerOptions PayloadFormat = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Whether the page should be stamped dark, taken from the application's theme.</summary>
    private bool DarkWanted() =>
        RootHost.ActualTheme == ElementTheme.Dark;

    /// <summary>
    /// The page, wrapped for a browser and cut off from the network.
    ///
    /// <b>The file on disk is a fragment, and deliberately so.</b> The same page is published
    /// as an artifact, where the host supplies the document skeleton; keeping one file and
    /// wrapping it in the two places that need it is the only way the two renderings cannot
    /// drift apart.
    ///
    /// <b>The external stylesheet is stripped, which is the part worth reading twice.</b> The
    /// published page links two typefaces from a font host. That is fine on the web and wrong
    /// here: opening a local reference page inside an assistant that keeps everything on this
    /// machine must not send a request to anybody. So the link comes out, and the page falls
    /// back to the stacks it already declares -- Georgia, Segoe UI, Cascadia Mono, all
    /// present on Windows. Same layout, different faces, no traffic.
    ///
    /// The theme is stamped on the root element rather than left to the runtime, because the
    /// page is built for exactly that: an explicit stamp wins over the browser's own
    /// preference in both directions, so the document matches the window it is in.
    /// </summary>
    private static string Page(string theme, UiText text)
    {
        string body = Fragment(text);

        // Every link to somewhere else, whatever its rel: preconnect, stylesheet, or
        // anything a later edit adds. Matched on the scheme rather than on a host, so a
        // second font host tomorrow is caught by the same line.
        body = Regex.Replace(
            body,
            @"<link\b[^>]*href\s*=\s*""https?:[^""]*""[^>]*>",
            string.Empty,
            RegexOptions.IgnoreCase);

        // Two dollars, so a single brace is a brace. The skeleton carries CSS, and with one
        // dollar every rule in it would be read as an interpolation hole.
        return $$"""
            <!doctype html>
            <html lang="{{text.LanguageTag}}" data-theme="{{theme}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
              html { color-scheme: light dark; }
              body { margin: 0; font: 14px system-ui, sans-serif; }
              img { max-width: 100%; }
              [hidden] { display: none !important; }
            </style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;
    }

    /// <summary>The page as it ships, out of the assembly, in the chosen language.</summary>
    /// <remarks>
    /// <b>One file with tokens, not one file per language.</b> Two copies of a 39 KB page
    /// drift: a wording is improved in the one the author reads, a layout fix lands in one
    /// and not the other, and nothing says so until somebody opens the other. The markup and
    /// the script carry <c>{{PropertyName}}</c> and the strings come from
    /// <see cref="UiText"/>, so there is one page and one place where its words live.
    ///
    /// A token nobody has a string for survives substitution and is visible as
    /// <c>{{Whatever}}</c> on the page. That is deliberate -- the harness fails on a leftover
    /// brace, so it is caught in the build rather than shipped as a blank.
    /// </remarks>
    private static string Fragment(UiText text)
    {
        Assembly assembly = typeof(VorzimmerWindow).Assembly;

        string? name = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith("vorzimmer.html", StringComparison.OrdinalIgnoreCase));

        if (name is null)
            return "<p>The page is missing from this build.</p>";

        using Stream? stream = assembly.GetManifestResourceStream(name);

        if (stream is null)
            return "<p>The page is missing from this build.</p>";

        using var reader = new StreamReader(stream);

        return Localise(reader.ReadToEnd(), text);
    }

    /// <summary>Replace every <c>{{Key}}</c> with what that language calls it.</summary>
    internal static string Localise(string page, UiText text)
    {
        foreach ((string key, string value) in text.Tokens)
            page = page.Replace("{{" + key + "}}", value, StringComparison.Ordinal);

        return page;
    }

    /// <summary>
    /// Open beside the pill the first time, then leave it where the user left it -- unless
    /// the pill has since moved to another display, which is the case that made the answer
    /// window appear on the screen nobody was looking at.
    /// </summary>
    private void Place(nint pillHandle)
    {
        _pillHandle = pillHandle;

        if (_placed && SharesDisplayWithPill())
            return;

        _placed = true;

        double scale = new WindowShaper(pillHandle).Scale;

        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);

        // Wide enough for the main column and the side column to sit beside each other,
        // which is where the page says something the words do not: the day and the mail on
        // the left, what is late and what was ignored on the right. Below about 900 CSS
        // pixels they stack and that reading is lost. Tall enough that a morning with three
        // appointments and four rows in each tray fits without scrolling -- a briefing is
        // one screen, and one that has to be scrolled is two. Clamped to the work area so a
        // 1366x768 laptop still gets a whole window.
        int width = Math.Min((int)Math.Round(1180 * scale), (int)(area.WorkArea.Width * 0.94));
        int height = Math.Min((int)Math.Round(920 * scale), (int)(area.WorkArea.Height * 0.94));

        AppWindow.MoveAndResize(new RectInt32(
            area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
            area.WorkArea.Y + ((area.WorkArea.Height - height) / 2),
            width,
            height));
    }

    private bool SharesDisplayWithPill()
    {
        if (_pillHandle == 0)
            return true;

        try
        {
            DisplayArea mine = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);

            DisplayArea theirs = DisplayArea.GetFromWindowId(
                Win32Interop.GetWindowIdFromWindow(_pillHandle), DisplayAreaFallback.Nearest);

            return mine.WorkArea.X == theirs.WorkArea.X
                && mine.WorkArea.Y == theirs.WorkArea.Y;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Drag by the header, the same way the other windows do it.
    ///
    /// Cursor position comes from Win32 rather than from the pointer event, because the
    /// window is what moves: a WinUI position is relative to the window, so the coordinate
    /// system shifts out from under the gesture.
    /// </summary>
    private void MakeDraggable(UIElement surface)
    {
        surface.PointerPressed += (sender, e) =>
        {
            if (sender is not UIElement element)
                return;

            if (!Windows.Win32.PInvoke.GetCursorPos(out System.Drawing.Point cursor))
                return;

            _dragging = true;
            _dragFrom = new PointInt32(cursor.X, cursor.Y);
            _windowFrom = new PointInt32(AppWindow.Position.X, AppWindow.Position.Y);
            element.CapturePointer(e.Pointer);
        };

        surface.PointerMoved += (_, _) =>
        {
            if (!_dragging || !Windows.Win32.PInvoke.GetCursorPos(out System.Drawing.Point now))
                return;

            AppWindow.Move(new PointInt32(
                _windowFrom.X + (now.X - _dragFrom.X),
                _windowFrom.Y + (now.Y - _dragFrom.Y)));
        };

        surface.PointerReleased += (sender, e) =>
        {
            _dragging = false;

            if (sender is UIElement element)
                element.ReleasePointerCapture(e.Pointer);
        };

        surface.PointerCaptureLost += (_, _) => _dragging = false;
    }
}
