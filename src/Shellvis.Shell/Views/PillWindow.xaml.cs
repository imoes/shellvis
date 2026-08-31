using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shellvis.Core;
using Shellvis.Core.Agent;
using Shellvis.Shell.Agent;
using Shellvis.Shell.Controls;
using Shellvis.Shell.Interop;
using Windows.Graphics;
using Windows.System;
using Windows.Win32;
using Windows.UI.Text;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Shellvis.Shell.Views;

/// <summary>
/// The floating command bar: a stadium-shaped pill with a console that expands
/// beneath it.
///
/// Two decisions worth understanding before changing anything here.
///
/// 1. The window is ALWAYS full height (pill + gap + console). Expanding does not
///    resize the window, it only grows the console and widens the clipping region.
///    Resizing a borderless always-on-top window every frame tears and fights the
///    compositor; and because the region also defines the hit-test area, the hidden
///    console cannot be clicked while collapsed.
///
/// 2. The expand animation is ticked from a DispatcherQueueTimer rather than driven
///    by a Storyboard. The console height and the Win32 clipping region have to move
///    together: a XAML animation would drive the height on the compositor thread
///    while the region lagged on the UI thread, briefly showing a hard rectangle edge
///    around the growing console. CompositionTarget.Rendering would give the same
///    lockstep, but it does not reliably fire in WinUI 3 -- an earlier revision used
///    it and the console silently never expanded.
/// </summary>
public sealed partial class PillWindow : Window
{
    // Segoe Fluent Icons glyphs, written as escapes and never as literal characters.
    //
    // They sit in the Unicode private use area, where most editors, diff viewers and
    // terminals show them as an empty box -- and a character nobody can see is a character
    // any tool can lose. That is not hypothetical: GlyphMic and GlyphMicOff in the
    // dictation file were silently emptied by a scripted edit, so pressing the microphone
    // button set the button's content to "" and the icon vanished. It was reported as the
    // symbol disappearing, and nothing in the source looked wrong, because an empty string
    // literal and one holding an invisible character are indistinguishable on screen.
    //
    // An escape is plain ASCII in the file. It cannot be mangled by an encoding round
    // trip, it survives sed and perl, and it says which codepoint it is without a comment.
    private const string GlyphChevronDown = "\uE70D"; // U+E70D
    private const string GlyphChevronUp = "\uE70E"; // U+E70E
    private const string GlyphTerminal = "\uE756"; // U+E756
    private const string GlyphTool = "\uE90F"; // U+E90F
    private const string GlyphPerson = "\uE77B"; // U+E77B
    private const string GlyphWarning = "\uE7BA"; // U+E7BA
    private const string GlyphSpeaker = "\uE767"; // U+E767, announcements

    private readonly WindowShaper _shaper;
    private HotkeyListener? _hotkey;
    private TrayIcon? _tray;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherQueueTimer _ticker;

    private bool _consoleOpen;
    private double _consoleFrom;
    private double _consoleTo;
    private bool _animating;

    /// <summary>
    /// The session, built on a background thread.
    ///
    /// Opening a PowerShell runspace takes seconds, and an earlier revision did it
    /// synchronously in this constructor: the pill took seven seconds to appear and was
    /// invisible to UI Automation until it did. Startup has to be instant, so the agent
    /// warms up behind the window rather than in front of it.
    /// </summary>
    private Task<AgentSession>? _sessionTask;
    private AgentSession? _session;

    /// <summary>The optimistic row for a tool call still in flight.</summary>
    private FrameworkElement? _pendingRow;
    private int _lastToolCount;

    public PillWindow()
    {
        InitializeComponent();

        _shaper = new WindowShaper(Win32Interop.GetWindowFromWindowId(AppWindow.Id));

        RootHost.Width = PillMetrics.Width;
        PillHost.Height = PillMetrics.PillHeight;
        GapSpacer.Height = PillMetrics.Gap;

        // Reserve the console's full expanded space immediately. The row keeps its
        // height for the window's whole life so the window never has to resize;
        // only the panel inside it grows, and the clipping region hides the rest.
        ConsoleAreaRow.Height = new GridLength(PillMetrics.ConsoleHeight);

        ConfigurePresenter();
        PositionAtBottomCentre();

        _shaper.TrySoftenEdges();
        ApplyRegion(0);

        // ~120 Hz so the region keeps up with the height on high-refresh displays;
        // the timer coalesces on slower ones rather than queueing up ticks.
        _ticker = DispatcherQueue.CreateTimer();
        _ticker.Interval = TimeSpan.FromMilliseconds(8);
        _ticker.IsRepeating = true;
        _ticker.Tick += OnTick;

        ConsoleToggleButton.Click += (_, _) => ToggleConsole();

        // Collapse, not close. The header button only ever has a reason to exist while the
        // console is open, so it closes rather than toggling: a second press of something
        // that has just vanished is not a gesture anyone makes deliberately.
        CollapseButton.Click += (_, _) =>
        {
            if (_consoleOpen)
                ToggleConsole();
        };

        CloseButton.Click += (_, _) => Close();
        AnswerButton.Click += (_, _) => OnShowAnswer();
        ExpandButton.Click += (_, _) => Undock();

        // The same three actions from the pill menu, which is reachable whether or not
        // the console is open.
        SparkleButton.Click += (_, _) => ToggleDock();
        HistoryButton.Click += (_, _) => ToggleHistory();
        NewSessionButton.Click += (_, _) => OnNewSession();
        HistorySearch.TextChanged += (_, _) => RefreshSessionList();
        MicButton.Click += (_, _) => ToggleDictation();
        ModeButton.Click += (_, _) => ShowModeMenu();
        ModelButton.Click += (_, _) => ShowModelMenu();

        // Both floating surfaces are drag handles on their blank parts. The console counts
        // too: it is the larger surface, and grabbing a panel by its background to move
        // the thing it belongs to is the ordinary gesture.
        MakeDraggable(PillTint);
        MakeDraggable(ConsoleTint);
        PromptBox.KeyDown += OnPromptKeyDown;

        // Re-shaped once the layout has actually happened. ApplyRegion in the
        // constructor runs BEFORE the first measure pass, so it can only fall back to the
        // metric constants -- and those were four pixels out from where WinUI really
        // paints the pill, which showed as a light edge above it. Measuring is only
        // possible after Loaded.
        RootHost.Loaded += (_, _) => ApplyRegion(_consoleOpen ? PillMetrics.ConsoleHeight : 0);
        RootHost.SizeChanged += (_, _) => ApplyRegion(_consoleOpen ? PillMetrics.ConsoleHeight : 0);

        // The pill band changes size when docking, and the region has to follow AFTER the
        // layout pass. A call queued from the dock code ran too early and measured the old
        // bounds, leaving a region wider than the painted bar -- visible as a bare edge at
        // the left end of the docked field.
        PillHost.SizeChanged += (_, _) => ApplyRegion(_consoleOpen ? PillMetrics.ConsoleHeight : 0);
        Activated += OnFirstActivated;
        Closed += OnClosed;

        StartSession();
    }

    private void ConfigurePresenter()
    {
        // Drops the caption area so the pill owns the entire client region.
        ExtendsContentIntoTitleBar = true;

        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
    }

    private void PositionAtBottomCentre()
    {
        double scale = _shaper.Scale;
        int width = (int)Math.Round(PillMetrics.Width * scale);
        int height = (int)Math.Round(PillMetrics.TotalHeight * scale);

        // The monitor the CURSOR is on, not the one the window happens to be on.
        //
        // Asking by window id looks right and is not: this runs from the constructor,
        // before the window has been placed, so "nearest to the window" resolves to
        // wherever Windows created it. On a multi-monitor machine that is often not the
        // screen the user is looking at, and the pill appeared on the left display while
        // the work was on the middle one. The cursor is the best available answer to
        // "where is their attention", and it costs one call.
        var work = (PInvoke.GetCursorPos(out System.Drawing.Point cursor)
                ? DisplayArea.GetFromPoint(
                    new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest)
                : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary))
            .WorkArea;

        int x = work.X + ((work.Width - width) / 2);

        // The pill occupies the BOTTOM band of the window, so anchoring the pill to
        // the bottom inset means offsetting the window by its full height. Anchoring
        // the window's top here instead would push everything below it off-screen --
        // which is exactly the bug this replaced.
        int pillBottom = work.Y + work.Height - (int)Math.Round(PillMetrics.BottomInset * scale);
        int y = pillBottom - height;

        AppWindow.MoveAndResize(ContentRect(x, y, width, height));
    }

    /// <summary>
    /// Turn a rectangle describing where the CONTENT should sit into the window rectangle
    /// that puts it there.
    ///
    /// MoveAndResize sizes the window, not the client area, and this window has a
    /// three-pixel non-client frame it cannot shed (see <see cref="WindowFrame"/>). Passing
    /// the content size straight through therefore left the client six pixels short of the
    /// XAML root, so the pill's right and bottom edges were cut off -- and since the
    /// clipping region traces the content, the missing edges showed as a border that had
    /// apparently slipped out of place.
    /// </summary>
    private RectInt32 ContentRect(int x, int y, int width, int height)
    {
        WindowFrame frame = _shaper.Frame();

        return new RectInt32(
            x - frame.OffsetX,
            y - frame.OffsetY,
            width + frame.ExtraWidth,
            height + frame.ExtraHeight);
    }

    /// <summary>
    /// Rebuild the clipping region for a given console height. Anything outside
    /// these shapes is neither composited nor hit-testable.
    /// </summary>
    private void ApplyRegion(double consoleHeight)
    {
        // MEASURED, not computed from the metrics constants.
        //
        // The earlier version built the region from PillMetrics arithmetic, and it was
        // four pixels out: the region's pill started at y=348 while WinUI painted the
        // pill's ring from y=352, leaving four rows of bare window visible as a light
        // edge above the pill. Sampling the pixels is how that was found, and the lesson
        // is that a region describing "where the content is" must ASK where the content
        // is -- any second calculation of the same layout will disagree eventually.
        var shapes = new List<RoundedRect>(2);

        if (MeasureBounds(PillHost) is { } pill)
        {
            // The radius follows the height, so the docked bar is a stadium too rather
            // than a 34px bar with 32px corners, which would look like a lozenge.
            shapes.Add(pill with
            {
                Radius = _docked ? PillMetrics.DockedRadius : PillMetrics.PillRadius,
            });
        }

        // Below a pixel there is nothing to show, and a zero-height rounded rect would
        // yield an empty region.
        if (consoleHeight >= 1 && MeasureBounds(ConsoleHost) is { } console)
            shapes.Add(console with { Radius = PillMetrics.ConsoleRadius });

        if (shapes.Count == 0)
        {
            // Before the first layout pass nothing has bounds yet. Falling back to the
            // constants keeps the window shaped rather than briefly rectangular.
            shapes.Add(new RoundedRect(
                0,
                PillMetrics.TotalHeight - PillMetrics.PillHeight,
                PillMetrics.Width,
                PillMetrics.PillHeight,
                PillMetrics.PillRadius));
        }

        _shaper.Apply(shapes);
    }

    /// <summary>
    /// Where an element actually sits, in the root's coordinates.
    ///
    /// Returns null until the element has been laid out, which is the case during the
    /// constructor: asking then would give a zero rectangle and clip the window away.
    /// </summary>
    private RoundedRect? MeasureBounds(FrameworkElement element)
    {
        if (element.ActualWidth < 1 || element.ActualHeight < 1)
            return null;

        try
        {
            Windows.Foundation.Point origin = element
                .TransformToVisual(RootHost)
                .TransformPoint(new Windows.Foundation.Point(0, 0));

            return new RoundedRect(
                origin.X, origin.Y, element.ActualWidth, element.ActualHeight, 0);
        }
        catch (Exception)
        {
            // TransformToVisual throws if the element is not in the same visual tree,
            // which can happen mid-teardown.
            return null;
        }
    }

    private void ToggleConsole()
    {
        _consoleOpen = !_consoleOpen;

        // Opening the console IS reading what is waiting, because the lines are already in
        // it. Anything else would leave a dot on a bar whose console the user is looking at.
        if (_consoleOpen)
            MarkRead();

        // Start from wherever the animation currently sits, so a mid-flight toggle
        // reverses smoothly instead of snapping.
        _consoleFrom = ConsoleHost.Height;
        _consoleTo = _consoleOpen ? PillMetrics.ConsoleHeight : 0;

        ConsoleToggleButton.Content = _consoleOpen ? GlyphChevronUp : GlyphChevronDown;
        ToolTipService.SetToolTip(
            ConsoleToggleButton, _consoleOpen ? "Hide console" : "Show console");

        _clock.Restart();
        SpikeLog.Write($"toggle open={_consoleOpen} from={_consoleFrom:F0} to={_consoleTo:F0}");

        if (_animating)
            return;

        _animating = true;
        _ticker.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        double t = _clock.Elapsed.TotalMilliseconds / PillMetrics.ToggleDuration.TotalMilliseconds;

        if (t >= 1)
        {
            Settle();
            return;
        }

        // Ease-out cubic: quick departure, gentle arrival.
        double eased = 1 - Math.Pow(1 - t, 3);
        double height = _consoleFrom + ((_consoleTo - _consoleFrom) * eased);

        ConsoleHost.Height = height;
        ApplyRegion(height);
    }

    private void Settle()
    {
        _ticker.Stop();
        _animating = false;
        _clock.Stop();

        ConsoleHost.Height = _consoleTo;
        ApplyRegion(_consoleTo);
        SpikeLog.Write($"settled at {_consoleTo:F0}");
    }

    private async void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Escape interrupts a running turn. Checked before Enter so a user who wants
        // out does not have to wait for the model.
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;

            // Dictation first: Escape while listening should abandon the recording, not
            // interrupt a turn that is probably not even running. Same escape ladder
            // reasoning as elsewhere -- the innermost transient state goes first.
            if (_dictation?.State == Core.Voice.DictationState.Listening)
            {
                CancelDictation();
                return;
            }

            _session?.Interrupt();
            return;
        }

        if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(PromptBox.Text))
            return;

        e.Handled = true;

        string prompt = PromptBox.Text.Trim();
        PromptBox.Text = string.Empty;

        // Two places, two forms, and that is the rule now: the conversation window gets
        // what was said, the console gets a log line saying that it was said. Rendering the
        // prompt as prose in the console was half of what made the separation inconsistent.
        RecordPrompt(prompt);
        AddRow(GlyphPerson, Oneline(prompt), "asked");

        if (!_consoleOpen)
            ToggleConsole();

        // A prompt typed during warm-up waits for it rather than being dropped: the
        // pill is usable the instant it appears, so this is a normal case.
        if (_session is null && _sessionTask is not null)
        {
            StatusText.Text = "Shellvis is still tuning up.";
            try
            {
                _session = await _sessionTask;
            }
            catch (Exception)
            {
                // AnnounceWhenReadyAsync has already reported it in the transcript.
            }
        }

        if (_session is null)
        {
            AddRow(GlyphWarning, "no model session available", "failed");
            return;
        }

        StatusText.Text = ShellvisVoice.Working;

        // async void is correct for an event handler, and the session already funnels
        // every failure into a Failure event rather than throwing, so nothing can
        // escape onto the UI thread unobserved.
        await _session.RunTurnAsync(prompt, Render);
    }

    /// <summary>
    /// Render one agent event into the transcript. Always called on the UI thread:
    /// the session marshals events through the dispatcher.
    /// </summary>
    private void Render(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.AssistantDelta e:
                RevealConsoleIfDocked();
                AppendDelta(e.Text);
                break;

            case AgentEvent.AssistantMessage e when e.Text.Trim().Length > 0:
                // If the answer streamed in, the row already holds it; rewriting it with
                // the final text replaces the incrementally built string with the
                // authoritative one, which also fixes any whitespace the chunking split
                // awkwardly. If nothing streamed, this creates the row as before.
                // The document goes to its own window; the log keeps a line saying it
                // happened. A console that showed the tools running and then nothing would
                // be a record with a hole in it.
                RecordAnswer(Tidy(e.Text));
                _streamed.Clear();

                AddRow(
                    GlyphSpeaker,
                    $"answered, {WordCount(e.Text)} words",
                    "answer",
                    isAnnouncement: true);

                break;

            case AgentEvent.ToolStarted e:
                // Docked, this is what makes output "pop up": a small bar on the taskbar
                // that ran a command with no visible trace would be worse than the
                // opacity this console exists to remove.
                RevealConsoleIfDocked();
                StartToolCard(e.Tool, e.Preview);
                break;

            case AgentEvent.ToolCompleted e:
                // The same card is rewritten rather than removed and replaced. Replacing
                // loses the scroll position and any selection the reader had made in it,
                // which for a long result is the difference between reading it and
                // watching it jump away.
                FinishToolCard(
                    e.Succeeded,
                    e.Result,
                    $"{e.Duration.TotalMilliseconds:F0}ms");

                // A note the model just wrote should appear now, not at the next start.
                // Driven from the result rather than from the tool, because the tool lives
                // in Core and Core has no windows.
                if (e.Succeeded && e.Tool == "note_stick")
                    ShowNewStickies();

                break;

            case AgentEvent.ToolRefused e:
                FinishToolCard(succeeded: false, e.Reason, "denied");
                break;

            case AgentEvent.Compacted e:
                AddRow(GlyphSpeaker, $"context compacted: {e.Detail}", "compacted",
                    isAnnouncement: true);
                break;

            case AgentEvent.Announcement e:
                AddRow(GlyphSpeaker, e.Text, "learned", isAnnouncement: true);
                break;

            case AgentEvent.Failure e:
                AddRow(GlyphWarning, e.Message, "failed", isWarning: true);
                break;

            case AgentEvent.TurnFinished e:
                // Cleared here as well as on AssistantMessage: an interrupted or failed turn
                // never produces a final message, and leftover text would make the next
                // turn's first delta continue this turn's sentence.
                _streamed.Clear();

                // Every reason named separately, including the two that shared the
                // catch-all. Refused and Failed are different endings and reading the same
                // sentence for both tells the user nothing about which happened: one means
                // they said no, the other means the provider broke.
                //
                // Noticed because "Shellvis hit a snag" appeared after a turn whose answer
                // had arrived. That reading was correct rather than wrong -- prose can be
                // emitted alongside tool calls and the turn can fail afterwards -- but a
                // status line that says only "a snag" leaves no way to tell the two apart.
                StatusText.Text = e.Reason switch
                {
                    TurnEndReason.Answered => ShellvisVoice.Standby,
                    TurnEndReason.Interrupted => "Shellvis stepped off the stage.",
                    TurnEndReason.BudgetExhausted => "Shellvis ran out of encores.",
                    TurnEndReason.Refused => "Shellvis needed a yes and did not get one.",
                    TurnEndReason.Failed => "Shellvis hit a snag.",
                    _ => ShellvisVoice.Standby,
                };
                break;
        }

        ScrollToEnd();
    }

    /// <summary>
    /// Collapse a tool result to its first meaningful line.
    ///
    /// Tool output runs to hundreds of lines; the console is a trace of WHAT ran, not a
    /// viewer for the output. The model still sees the whole thing.
    /// </summary>
    /// <summary>
    /// A prompt as one line of log.
    ///
    /// The console is a trace of what happened, so what belongs in it is that a question
    /// was asked and roughly which one. The question itself, in full and formatted, is in
    /// the conversation window, which is where someone goes to read rather than to scan.
    /// </summary>
    private static string Oneline(string text)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();

        while (flat.Contains("  ", StringComparison.Ordinal))
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);

        return flat.Length <= 110 ? flat : flat[..110] + "...";
    }

    private static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed.Length > 150 ? trimmed[..150] + "..." : trimmed;
        }

        return "(no output)";
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        // Reposition and reclip once the window is genuinely mapped to a monitor:
        // GetDpiForWindow only reports the real scale factor at that point, so the
        // constructor's first pass may have assumed 96 DPI.
        PositionAtBottomCentre();
        ApplyRegion(ConsoleHost.Height);

        // Before the prompt box takes focus, because priming the alert window activates it
        // once and the focus has to end up here, not there.
        PrepareToast();

        PromptBox.Focus(FocusState.Programmatic);
        RegisterHotkey();
        RegisterHoldToTalk();
        RegisterTopmostYield();

        // Warmed here rather than on the first key press. The model takes about a second and a
        // half to load, and paying that at the moment someone holds the space bar means the
        // gesture appears to do nothing -- and, because the keyboard hook runs on this thread,
        // means the key is not even suppressed while it happens.
        EnsureWhisper();
    }

    /// <summary>
    /// Claim Ctrl+Alt+Space to raise the pill.
    ///
    /// Not a convenience: Windows refuses SetForegroundWindow to any process that does
    /// not already own the foreground, so an always-on-top-but-unfocused pill cannot be
    /// reached any other way. Handling a hotkey message is what grants the app the
    /// right to come forward.
    ///
    /// A combination already taken by another application is reported rather than
    /// treated as a failure -- which combination is free is the user's business.
    /// </summary>
    private void RegisterHotkey()
    {
        _hotkey = new HotkeyListener(Win32Interop.GetWindowFromWindowId(AppWindow.Id));

        _hotkey.Pressed += id =>
        {
            if (id == HotkeyListener.RaiseId)
                PromptBox.Focus(FocusState.Programmatic);
            else if (id == HotkeyListener.DictateId)
                ToggleDictation();
        };

        const uint vkSpace = 0x20;
        const uint vkD = 0x44;

        var ctrlAlt = Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS.MOD_CONTROL
            | Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS.MOD_ALT;

        bool raise = _hotkey.TryRegister(HotkeyListener.RaiseId, ctrlAlt, vkSpace);

        AddRow(
            raise ? GlyphTool : GlyphWarning,
            raise
                ? "Ctrl+Alt+Space brings Shellvis to the front."
                : "Ctrl+Alt+Space is taken by another application; the pill cannot be raised by hotkey.",
            raise ? "hotkey" : "unavailable");

        // Registered separately so one taken combination does not cost the other. A
        // machine where Ctrl+Alt+D belongs to something else should still get its raise
        // hotkey.
        bool dictate = _hotkey.TryRegister(HotkeyListener.DictateId, ctrlAlt, vkD);

        if (dictate)
            AddRow(GlyphTool, "Ctrl+Alt+D starts and stops dictation.", "hotkey");

        RegisterTrayIcon();
    }

    /// <summary>
    /// Put Shellvis in the notification area.
    ///
    /// Registered after the hotkeys, and with its own comctl32 subclass id: the two
    /// mechanisms are independent, so a tray icon the shell refuses does not cost the
    /// hotkey and the other way round.
    /// </summary>
    private void RegisterTrayIcon()
    {
        _tray = new TrayIcon(Win32Interop.GetWindowFromWindowId(AppWindow.Id));

        // Left click raises. Right click opens the menu, handled inside TrayIcon.
        _tray.ShowRequested += () =>
        {
            _hotkey?.BringToFront();
            PromptBox.Focus(FocusState.Programmatic);
        };

        _tray.ConsoleRequested += ToggleConsole;
        _tray.DockRequested += ToggleDock;

        _tray.ExitRequested += () =>
        {
            // The sign-off happens in OnClosed, which Close() reaches. Quitting through
            // the same path as the window's own close button is what keeps the teardown
            // -- MCP children, the browser, the COM apartment -- in one place.
            Close();
        };

        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

        bool added = _tray.TryAdd(
            icon,
            $"Shellvis {ShellvisVersion.Current} - Ctrl+Alt+Space to show, Ctrl+Alt+D to dictate");

        AddRow(
            added ? GlyphTool : GlyphWarning,
            added
                ? "In the notification area: left click shows, right click opens the menu."
                : "The notification area refused the icon; Shellvis is still reachable by hotkey.",
            added ? "tray" : "unavailable");
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // The sign-off. Once there is a tray icon with an Exit item this becomes a
        // visible announcement; for now it marks the shutdown in the debug log so the
        // lifecycle boundary is observable.
        Debug.WriteLine(ShellvisVoice.Farewell);

        // EVERY step guarded, and independently.
        //
        // This was a bug, and a nasty one: closing the alert window was the single step here
        // that ran unguarded, and it sat in the middle. Anything it threw escaped the Closed
        // handler and abandoned the rest of the teardown -- so the tray icon was already gone,
        // the pill window was gone, and the remaining windows kept the process alive with
        // nothing left on screen able to reach it. The report was "you cannot quit Shellvis",
        // and that is exactly what it looks like from outside.
        //
        // The lesson is structural rather than local: a shutdown path is the one place where
        // a later step must not depend on an earlier one succeeding. Adding a try around the
        // one offending line would have fixed today's fault and left the shape that produced
        // it, so each step is now isolated by construction.
        static void Safe(Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                // Nowhere to report it: the console is being torn down. The debug log is the
                // only listener left, and losing one step is far better than losing the rest.
                Debug.WriteLine($"shutdown step failed: {ex}");
            }
        }

        // The GDI region outlives the managed window unless it is released here.
        Safe(() => _shaper.Dispose());

        // A hotkey left registered stays claimed system-wide until the process dies,
        // and the window subclass would call into collected code.
        // The tray icon goes first: an icon whose owner window has already gone leaves a
        // ghost in the notification area until the user hovers over it.
        Safe(() => _tray?.Dispose());
        Safe(() => _dictation?.Dispose());
        Safe(() => _hotkey?.Dispose());
        Safe(() => _spaceHook?.Dispose());
        Safe(() => _session?.Dispose());

        // The answer window is a second top-level window and does not close with this one.
        // Left open, it keeps the process alive with no way left to reach it.
        Safe(CloseAnswerWindow);

        // And the alert, for the same reason. It is a tool window with no taskbar button, so
        // one left behind would hold the process open with nothing on screen to close it.
        Safe(CloseToast);

        // Same for the notes, and the distinction matters here: closing them is not
        // throwing them away, so what is stored is left exactly as it is.
        Safe(CloseStickies);
    }

    // ---------------------------------------------------------------- transcript

    /// <summary>
    /// Placeholder content so the console's look can be judged before the agent
    /// exists. Deleted once step 3 wires up real events.
    /// </summary>
    /// <summary>
    /// Bring the agent up.
    ///
    /// Failure here is survivable and must be visible: an unreachable endpoint or a
    /// missing key leaves a usable window that says what is wrong, rather than a pill
    /// that silently swallows every prompt.
    /// </summary>
    private void StartSession()
    {
        // The version rides on the greeting rather than taking a row of its own: it is
        // wanted for identifying a build, not worth a line of the transcript every start.
        AddRow(GlyphSpeaker, ShellvisVoice.Greeting, ShellvisVersion.Current, isAnnouncement: true);
        StatusText.Text = "Shellvis is tuning up.";

        var gate = new PillApprovalGate(
            DispatcherQueue,
            () => (Content as FrameworkElement)?.XamlRoot);

        // Off the UI thread on purpose: this opens a PowerShell runspace and a UI
        // Automation connection, which together cost seconds.
        _sessionTask = Task.Run(() => AgentSession.Create(DispatcherQueue, gate));

        _ = AnnounceWhenReadyAsync();
    }

    private async Task AnnounceWhenReadyAsync()
    {
        try
        {
            AgentSession session = await _sessionTask!.ConfigureAwait(true);
            _session = session;

            // Both labels are placeholders in XAML; the real mode and model come from the
            // config file and are only known now.
            RefreshModeChip();
            RefreshModelLabel();

            // Whatever was on the desktop when the application last closed goes back before
            // anything else is reported. A restart that quietly clears the desktop is the
            // single worst thing a sticky note program can do.
            RestoreStickies();

            foreach (string warning in session.Warnings)
                AddRow(GlyphWarning, warning, "config");

            AddRow(GlyphTool, $"{session.ProviderLabel}  ({session.ToolCount} tools)", "ready");
            StatusText.Text = ShellvisVoice.Standby;

            // MCP servers connect after the window is usable: each one launches a
            // process or opens an HTTP session and can take seconds.
            foreach (string status in await session.ConnectMcpAsync().ConfigureAwait(true))
                AddRow(GlyphTool, status, "mcp");

            if (session.BrokerAvailability is { Length: > 0 } broker)
                AddRow(GlyphTool, broker, "broker");

            if (session.ThunderbirdAvailability is { Length: > 0 } mail)
                AddRow(GlyphTool, mail, "mail");

            if (session.ToolCount != _lastToolCount)
            {
                _lastToolCount = session.ToolCount;
                AddRow(GlyphTool, $"{session.ToolCount} tools available", "ready");
            }

            // Started after MCP, so a job whose prompt needs an MCP tool finds it. Every
            // run reports into the transcript: a scheduled agent touching the machine
            // invisibly is the opposite of what this console is for.
            //
            // Quietly, though. A scheduled run is the one thing here that speaks without
            // being asked, so it is the one thing that must never take the screen: it
            // writes its line and raises a dot, and the user reads it when they look. The
            // plan for this feature originally had a reminder OPEN the console by itself,
            // which is exactly the window-in-your-face this must not be.
            session.StartCron((message, isProblem, result) =>
            {
                // The report goes into the conversation, not just into the log, and that is
                // what makes the alert clickable: a notice has to open something, and the
                // something is the message window. Written WITHOUT revealing it -- the
                // window arrives only when the user asks for it, which is the click.
                if (result?.Headline is { Length: > 0 })
                    RecordScheduledReport(result.Job, result.Summary, result.Headline);

                NoteQuietly(message, "cron", isProblem, result?.Headline);
            });
        }
        catch (Exception ex)
        {
            // A missing key or an unreachable endpoint must leave a usable window that
            // says what is wrong, not a pill that silently swallows every prompt.
            AddRow(GlyphWarning, $"no model session: {ex.Message}", "failed");
            StatusText.Text = "Shellvis has no band tonight.";
        }
    }

    /// <summary>
    /// Swap the trailing pending row for its finished form.
    ///
    /// Tool calls render optimistically as soon as they start so the console never
    /// looks frozen, then get rewritten in place with the outcome.
    /// </summary>
    private void ReplaceLastPending(string glyph, string text, string trailing)
    {
        if (_pendingRow is not null && Transcript.Items.Remove(_pendingRow))
            _pendingRow = null;

        AddRow(glyph, text, trailing);
    }

    private void ScrollToEnd()
    {
        // ChangeView on the extent keeps the newest line visible without stealing
        // focus from the prompt box.
        TranscriptScroller.ChangeView(null, TranscriptScroller.ScrollableHeight, null, true);
    }

    private readonly System.Text.StringBuilder _streamed = new();

    /// <summary>
    /// Extend the answer row with a freshly arrived chunk.
    ///
    /// The row is created on the FIRST delta rather than when the iteration starts,
    /// because a turn that goes straight to a tool call produces no text at all and an
    /// empty announcement row would appear before every tool.
    /// </summary>
    private void AppendDelta(string text)
    {
        if (text.Length == 0)
            return;

        // Straight into the answer window. There is no row in the transcript any more: the
        // console is the log and the answer is a document, and the whole point of separating
        // them was that a growing paragraph in the middle of a command log made both harder
        // to read.
        _streamed.Append(text);
        StreamAnswer(Tidy(_streamed.ToString()));
    }

    /// <summary>Words in an answer, for the one line the log keeps about it.</summary>
    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Collapse runs of blank lines, keeping the line structure the model wrote.
    ///
    /// Not flattening to one line: a wrapped row can show paragraphs, and a numbered list
    /// crushed onto a single line is markedly harder to read than the same list with its
    /// breaks intact.
    /// </summary>
    private static string Tidy(string text)
    {
        string tidied = text.ReplaceLineEndings("\n");

        while (tidied.Contains("\n\n\n", StringComparison.Ordinal))
            tidied = tidied.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);

        return tidied.Trim();
    }

    private void AddRow(
        string glyph,
        string text,
        string trailing,
        bool isPrompt = false,
        bool isAnnouncement = false,
        bool isAnswer = false,
        bool isPending = false,
        bool isWarning = false)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        // Prose is rendered as Markdown; machine output is not.
        //
        // The models here write Markdown whether asked to or not, and the console used to
        // show it raw -- a calendar answer read "- **Montag, 24. August**:", asterisks
        // included. Tool results deliberately do NOT go through the renderer: an asterisk
        // in a command line and a backtick in a PowerShell string are data, and a console
        // that italicises them is no longer showing what happened.
        if (isPrompt || isAnnouncement || isAnswer)
        {
            FrameworkElement prose = ProseBody(
                text,
                isAnswer ? ProseKind.Answer
                    : isAnnouncement ? ProseKind.Announcement
                    : ProseKind.Prompt);
            Grid.SetColumn(prose, 1);
            row.Children.Add(prose);
            AddTrailing(row, trailing);
            AppendRow(row, isPending);
            return;
        }

        var body = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            // Everything wraps now.
            //
            // Tool results used to stay on one ellipsised line, on the argument that a
            // transcript of wrapped command output is harder to scan. That was the wrong
            // trade: what it actually produced was a console where most lines ended in an
            // ellipsis and the interesting part -- the error message, the path, the value
            // that came back -- was the part cut off. A console whose whole purpose is to
            // show what happened must not hide the end of what happened. Scanning is
            // served instead by the glyph column and the monospace face.
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,

            // Selectable, so a path, an error or a command can be copied out. Without it
            // the only way to get text out of the transcript is to retype it, which for a
            // stack trace or a GUID is not a way at all.
            IsTextSelectionEnabled = true,

            VerticalAlignment = VerticalAlignment.Center,

            // The one place colour is spent, and only on things that went wrong. Everything
            // else is muted, so a warning is the only line that catches the eye when the
            // reader is scanning rather than reading.
            Foreground = ThemeBrush(isWarning ? "ConsoleWarningBrush" : "ConsoleMutedBrush"),
        };
        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        AddTrailing(row, trailing);
        AppendRow(row, isPending);
    }

    /// <summary>
    /// The body of a prose row: a RichTextBlock filled by the Markdown renderer.
    ///
    /// A RichTextBlock rather than a TextBlock because real bold, italic and monospace
    /// spans need separate runs, and because it carries text selection the same way -- the
    /// ability to copy a path out of the transcript is not given up for formatting.
    /// </summary>
    private RichTextBlock ProseBody(string text, ProseKind kind)
    {
        var body = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        RenderProse(body, text, kind);
        return body;
    }

    /// <summary>
    /// Which voice a prose row is in.
    ///
    /// The distinction matters more than it looks. Every model answer used to be rendered as
    /// an ANNOUNCEMENT, a category meant for Shellvis speaking about itself -- and
    /// announcements are set in italic. So a correctly parsed Markdown answer arrived as a
    /// uniform slanted paragraph in which bold and bullets barely registered, and it was
    /// reported as "the output is still not Markdown". The parser was never the problem; the
    /// answer was simply wearing the wrong clothes.
    /// </summary>
    private enum ProseKind
    {
        /// <summary>What the user typed.</summary>
        Prompt,

        /// <summary>What the model answered. The main thing anyone reads.</summary>
        Answer,

        /// <summary>Shellvis about itself: started, switched, learned.</summary>
        Announcement,
    }

    /// <summary>
    /// Render Markdown into an existing prose body. Shared with the streaming path, which
    /// re-renders the whole answer on every delta.
    /// </summary>
    private void RenderProse(RichTextBlock body, string text, ProseKind kind)
    {
        MarkdownRenderer.Render(
            body,
            text,
            // Proportional for prose, monospace for code: the same distinction the
            // transcript already draws between words and machine output, applied inside a
            // single answer.
            prose: new FontFamily("Segoe UI Variable Text"),
            mono: new FontFamily("Cascadia Mono"),
            // An answer gets a point more than the rest. It is the thing being read; the
            // prompt above it and the announcements around it are context.
            size: kind == ProseKind.Answer ? 14 : 13,
            foreground: ThemeBrush("ConsoleTextBrush"),
            muted: ThemeBrush("ConsoleMutedBrush"),
            onLink: OnLinkActivated);

        // Only announcements slant. They are Shellvis speaking about itself, and slant
        // rather than another colour because colour is already spoken for by severity.
        // Answers are upright: an italic paragraph flattens headings, bold and bullets into
        // one texture, which is exactly how correctly rendered Markdown came to be reported
        // as no Markdown at all. Applied to the block rather than inside the renderer, so
        // the renderer stays about Markdown and nothing else.
        body.FontStyle = kind == ProseKind.Announcement ? FontStyle.Italic : FontStyle.Normal;
    }

    private void AddTrailing(Grid row, string trailing)
    {
        if (string.IsNullOrEmpty(trailing))
            return;

        var meta = new TextBlock
        {
            Text = trailing,
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = ThemeBrush("ConsoleMutedBrush"),
        };

        Grid.SetColumn(meta, 2);
        row.Children.Add(meta);
    }

    private void AppendRow(Grid row, bool isPending)
    {
        Transcript.Items.Add(row);

        // Remember only rows that are expected to be rewritten, so a normal row is
        // never clobbered by a later tool completion.
        _pendingRow = isPending ? row : null;

        ScrollToEnd();
    }

    /// <summary>
    /// Resolve a theme brush declared in PillTheme.xaml. Falls back to the current
    /// foreground rather than throwing, so a renamed resource degrades visually
    /// instead of crashing the window.
    /// </summary>
    private static Brush ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}
