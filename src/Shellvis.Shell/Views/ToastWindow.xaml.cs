using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

using Shellvis.Shell.Interop;

using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shellvis.Shell.Views;

/// <summary>
/// The desktop alert: news arriving without taking the screen.
///
/// <b>Why this is allowed to exist at all, when a popup was ruled out.</b> The rule this
/// project settled on is that nothing may appear in front of someone who is working -- a
/// window that arrives mid-sentence steals the keystroke in flight and teaches its reader to
/// dismiss anything Shellvis shows. Outlook's desktop alert is the counter-example that has
/// survived twenty years of daily use, and it survives because of four properties, every one
/// of which is enforced here rather than assumed:
///
/// <list type="bullet">
/// <item><b>It never takes focus.</b> <c>WS_EX_NOACTIVATE</c> and a show call that does not
/// activate. The keystroke in flight still lands where the user was typing -- which is the
/// whole difference between an alert and an interruption.</item>
/// <item><b>It stays out of the way.</b> Bottom-right, above the tray, where nothing is being
/// worked on. Not centred, not near the caret.</item>
/// <item><b>It leaves on its own.</b> Seven seconds and it fades out. Nothing has to be
/// dismissed, so ignoring it is free -- and ignoring it is the common case.</item>
/// <item><b>Clicking it opens the thing.</b> Not a menu, not a settings page: the message
/// window, at what the notice was about. A notification whose click does something
/// unexpected is one people stop clicking.</item>
/// </list>
///
/// <b>What this is NOT.</b> A Windows toast. Those need a package identity and a registered
/// AUMID, and this application is deliberately unpackaged -- and a real toast lands in the
/// Action Centre with its own lifetime, its own dismissal and its own settings page, none of
/// which this can then reason about. A window of its own is honest about what it is.
/// </summary>
public sealed partial class ToastWindow : Window
{
    /// <summary>How long the alert stays before it fades.</summary>
    /// <remarks>
    /// Outlook's own default is five. Seven, because this one carries a whole sentence rather
    /// than a sender and a subject, and a sentence that has to be re-read once is gone.
    /// </remarks>
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(7);

    private const int Width = 380;
    private const int Height = 104;

    /// <summary>Distance kept from the screen edges, so it does not touch the taskbar.</summary>
    private const int Inset = 16;

    private readonly WindowShaper _shaper;
    private readonly nint _handle;
    private DispatcherQueueTimer? _dwellTimer;

    public ToastWindow()
    {
        InitializeComponent();

        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _shaper = new WindowShaper(_handle);

        ExtendsContentIntoTitleBar = true;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;

            // On top, and this one is not the debate the answer window had. A notice that can
            // be covered by the window the user is looking at is a notice that is never seen,
            // and it removes itself after seven seconds -- so "on top" costs seven seconds of
            // a corner rather than occupying the desktop until somebody closes it.
            presenter.IsAlwaysOnTop = true;
        }

        // The frame goes first, and it is not cosmetic housekeeping: the frame is what painted
        // a rectangular band around the rounded panel. It can be dropped outright here because
        // this window is not resizable -- see WindowShaper.TrimFrame. Then glass, and no DWM
        // rounding: the silhouette comes from the region cut in Place().
        _shaper.TrimFrame(keepResizeBorder: false);
        _shaper.TrySoftenEdges();

        CloseButton.Click += (_, _) => Dismiss();

        // The body opens; the cross dismisses. Two targets, because a notification where
        // every click does the same thing forces the reader to choose between reading it and
        // getting rid of it.
        Surface.PointerPressed += OnSurfacePressed;
    }

    /// <summary>What to do when the notice is clicked. Set by the pill.</summary>
    public Action? OnOpen { get; set; }

    /// <summary>
    /// Bring the window into existence properly, off screen, before it is ever needed.
    ///
    /// <b>Why a window has to be shown once before it can be shown quietly.</b> A WinUI
    /// window's XAML island is not composed until the framework has shown it, and until then
    /// neither <c>ShowWindow(SW_SHOWNOACTIVATE)</c> nor <c>AppWindow.Show(false)</c> puts
    /// anything on screen: the HWND exists, carries the right title and size, and is
    /// invisible. Both were tried against a live scheduled job before this was understood,
    /// and from outside the symptom is indistinguishable from "the notification never fired".
    ///
    /// Only <c>Activate</c> composes it, and <c>Activate</c> takes the foreground. So it is
    /// done exactly once, here, at a moment when the foreground is already being taken --
    /// the pill's own first activation -- and off screen, so nothing flashes. Afterwards the
    /// window carries <c>WS_EX_NOACTIVATE</c> and every real show is quiet.
    ///
    /// The DPI is the second reason this is not deferred: <c>GetDpiForWindow</c> reports 96
    /// for a window that has never been mapped to a monitor, and the first placement computed
    /// from that came out at two thirds size in the middle of the screen.
    /// </summary>
    public void Prime()
    {
        // Off screen, and far enough that no monitor arrangement contains it.
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, Width, Height));

        Activate();
        AppWindow.Hide();

        MakeUnfocusable();
    }

    /// <summary>Whether the alert is currently on screen.</summary>
    public bool IsShowing { get; private set; }

    /// <summary>
    /// Show a notice, replacing whatever was already up.
    ///
    /// Replacing rather than stacking: two alerts at once is a wall, and the second one
    /// arriving while the first is being read moves the text out from under the eye. The
    /// count in the source line is how the earlier ones are still accounted for.
    /// </summary>
    public void Show(string headline, string source)
    {
        Headline.Text = headline;
        Source.Text = source;

        Place();

        // Shown WITHOUT activating. AppWindow.Show() would take the foreground, which is
        // exactly the interruption this window exists to avoid -- the keystroke in flight
        // would land here instead of in whatever the user was typing into.
        ShowWithoutStealingFocus();

        IsShowing = true;
        RestartDwell();
    }

    /// <summary>Take it away, without doing anything about the notice it carried.</summary>
    public void Dismiss()
    {
        StopDwell();
        IsShowing = false;
        AppWindow.Hide();
    }

    private void OnSurfacePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();

        // After the window is gone, so the message window arrives to an uncluttered corner
        // rather than on top of an alert that is still fading.
        OnOpen?.Invoke();
    }

    /// <summary>Bottom-right, above the taskbar, on the screen with the pointer.</summary>
    private void Place()
    {
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 work = display.WorkArea;

        double scale = _shaper.Scale;
        int width = (int)Math.Round(Width * scale);
        int height = (int)Math.Round(Height * scale);
        int inset = (int)Math.Round(Inset * scale);

        // The WORK area, not the screen: its bottom edge is the top of the taskbar, so this
        // lands above the tray rather than behind it, at whatever taskbar height and edge the
        // user has.
        AppWindow.MoveAndResize(new RectInt32(
            work.X + work.Width - width - inset,
            work.Y + work.Height - height - inset,
            width,
            height));

        // Cut to the rounded panel, frame and all. DWM's rounding leaves the window's own
        // edge square around it, which shows as a rectangular border tracing rounded content.
        _shaper.ClipWindowRounded(SurfaceRadius);
    }

    /// <summary>The radius the panel is painted with, so the clip and the paint agree.</summary>
    private const double SurfaceRadius = 8;

    /// <summary>
    /// <c>WS_EX_NOACTIVATE</c>, plus out of the taskbar and out of Alt-Tab.
    ///
    /// The load-bearing line of the whole window. Without NOACTIVATE, clicking the alert --
    /// or merely its appearing under the pointer -- pulls the foreground away from whatever
    /// the user is doing, and a notification that can do that is the popup this application
    /// argued itself out of.
    /// </summary>
    private unsafe void MakeUnfocusable()
    {
        try
        {
            var hwnd = new HWND((void*)_handle);

            nint style = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

            style |= (nint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
            style |= (nint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
            style &= ~(nint)WINDOW_EX_STYLE.WS_EX_APPWINDOW;

            PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style);
        }
        catch (Exception)
        {
            // Cosmetic if it fails, in the sense that the alert still works -- but it would
            // then be able to take focus, so nothing here pretends otherwise.
        }
    }

    /// <summary>
    /// Put it on screen without taking the foreground.
    ///
    /// <b>Why not <c>ShowWindow(SW_SHOWNOACTIVATE)</c>, which is the obvious answer.</b> It
    /// was the first answer, and the window came up hidden: a WinUI window's XAML island is
    /// not composed until the window has been shown through the framework, so a raw Win32
    /// show on a window that has never been activated sets a flag and paints nothing. The
    /// symptom is exact and worth writing down, because from outside it looks like nothing
    /// happened at all -- the HWND exists, carries the right title, and is invisible.
    ///
    /// <c>AppWindow.Show(false)</c> is the documented way to ask for both at once, and it
    /// works only once the island exists. <see cref="Prime"/> is what makes sure it does.
    /// </summary>
    private void ShowWithoutStealingFocus() => AppWindow.Show(activateWindow: false);

    private void RestartDwell()
    {
        StopDwell();

        _dwellTimer = DispatcherQueue.CreateTimer();
        _dwellTimer.Interval = Dwell;
        _dwellTimer.IsRepeating = false;
        _dwellTimer.Tick += (_, _) => Dismiss();
        _dwellTimer.Start();
    }

    private void StopDwell()
    {
        _dwellTimer?.Stop();
        _dwellTimer = null;
    }
}
