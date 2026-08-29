using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Shellvis.Shell.Controls;
using Shellvis.Shell.Interop;

using Windows.Graphics;

namespace Shellvis.Shell.Views;

/// <summary>
/// The assistant's answer, in a window of its own.
///
/// <b>Why a second window at all.</b> The console under the pill is a log -- tool calls,
/// warnings, approvals, in the order they happened -- and the answer is a document. They ran
/// in one list until now, and the agent-UX literature names exactly this as a mistake:
/// conversation and activity move on different clocks, and interleaving them produces a
/// record that serves neither. Scrolling back through a long answer meant scrolling past the
/// commands that produced it.
///
/// <b>Why this was built as a spike first.</b> The pill was the only window this application
/// had ever opened, and it leans on things that are per-window or per-process and had never
/// been exercised twice: a Win32 clipping region, a comctl32 subclass for the hotkey, and a
/// low-level keyboard hook on a thread of its own. Whether a second WinUI window coexists
/// with all of that was unknown, and unknown-but-load-bearing is the thing to test before
/// building on it.
/// </summary>
public sealed partial class AnswerWindow : Window
{
    private readonly WindowShaper _shaper;

    /// <summary>Where the pointer was when the drag began, in screen pixels.</summary>
    private PointInt32 _dragFrom;
    private PointInt32 _windowFrom;
    private bool _dragging;

    public AnswerWindow()
    {
        InitializeComponent();

        _shaper = new WindowShaper(Win32Interop.GetWindowFromWindowId(AppWindow.Id));

        // No caption, no border: the surface inside draws its own rounded panel, exactly as
        // the pill does. Resizable, unlike the pill -- an answer can be any length, and a
        // document the reader cannot make taller is a document they will copy out instead.
        ExtendsContentIntoTitleBar = true;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;

            // Minimisable, and this is a correction. It was off because the window has no
            // title bar and therefore no system buttons -- but that reasoning confused "no
            // button to press" with "should not be possible". A document window you cannot
            // get out of the way, on a window that is always on top, is a document window
            // that sits over your work until you close it and lose your place in it.
            //
            // Restoring is the taskbar button: unlike a sticky note this is NOT a tool
            // window, so it has one.
            presenter.IsMinimizable = true;

            // Above ordinary windows like the pill, and stepping aside for the same reasons:
            // ForegroundState is shared rather than reimplemented, so a full-screen window or
            // a remote session is not covered by this either.
            // NOT always on top, and this is a correction.
            //
            // It was on because the pill is, and that was the wrong analogy. The pill is a
            // command bar: it has to be reachable from whatever is in front, which is the
            // whole reason it floats. This is a document. A document that cannot be put
            // behind the thing you are working on is a document that covers your work until
            // you close it, and closing it is how you lose your place in it.
            //
            // Reaching it again costs nothing: the button in the console header raises it,
            // and it has a taskbar button of its own.
            presenter.IsAlwaysOnTop = false;
        }

        _shaper.TrySoftenEdges();

        CloseButton.Click += (_, _) => Hide();

        MinimiseButton.Click += (_, _) =>
        {
            if (AppWindow.Presenter is OverlappedPresenter minimisable)
                minimisable.Minimize();
        };
        MakeDraggable(Header);
        MakeDraggable(Surface);

        // Hidden rather than destroyed. The window is reused for every answer: creating and
        // tearing one down per turn would flicker, lose the reader's scroll position, and
        // put a window-creation cost in front of every reply.
        Closed += (_, args) => { };
    }

    /// <summary>
    /// What to do when a link in the answer is clicked.
    ///
    /// Set by the pill, which owns the transcript a link writes into and knows what a
    /// shellvis: target means. This window only draws.
    /// </summary>
    public Action<string>? OnLink { get; set; }

    /// <summary>Show an answer, creating or reusing the window.</summary>
    public void ShowAnswer(string markdown, string heading)
    {
        HeaderText.Text = heading;
        HasAnswer = markdown.Trim().Length > 0;

        MarkdownRenderer.Render(
            Body,
            markdown,
            prose: new FontFamily("Segoe UI Variable Text"),
            mono: new FontFamily("Cascadia Mono"),
            size: 14,
            foreground: Brush("ConsoleTextBrush"),
            muted: Brush("ConsoleMutedBrush"),
            onLink: OnLink);
    }

    /// <summary>Put the window beside the pill the first time, then leave it where it is.</summary>
    public void PlaceBeside(nint pillHandle)
    {
        if (_placed)
            return;

        _placed = true;

        var pill = new WindowShaper(pillHandle);
        double scale = pill.Scale;

        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);

        int width = (int)Math.Round(560 * scale);
        int height = (int)Math.Round(420 * scale);

        // Above the pill and its console, centred on the same monitor. Not overlapping it:
        // the log and the document are meant to be readable at the same time, which is the
        // entire reason they were separated.
        AppWindow.MoveAndResize(new RectInt32(
            area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
            area.WorkArea.Y + (int)Math.Round(40 * scale),
            width,
            height));
    }

    private bool _placed;

    /// <summary>Hide without destroying, so the next answer reuses this window.</summary>
    public void Hide() => AppWindow.Hide();

    /// <summary>
    /// Put the window in front, whether it was hidden, minimised or merely behind.
    ///
    /// All three states have to be handled here, and only the first one used to be. A
    /// minimised window answers Show() by staying minimised: it is already "shown", it is
    /// just iconic. So restoring has to be asked for separately, or the answer button
    /// appears to do nothing for the one state the user is most likely to be in.
    /// </summary>
    public void Reveal()
    {
        AppWindow.Show();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Restore();

        _shaper.BringToFront();
    }

    /// <summary>Whether there is anything to come back to.</summary>
    public bool HasAnswer { get; private set; }

    /// <summary>
    /// Drag by the surface, the same way the pill does.
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
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Colors.Gray);
}
