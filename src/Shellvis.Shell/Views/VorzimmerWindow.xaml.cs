using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

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
/// <b>Why HTML at all, then.</b> Because the page is a layout: a masthead, three trays of
/// deliberately unequal width, a threshold in two columns, six tabbed cards, a numbered
/// sequence. Building that in XAML would be a week of panels to arrive at the same picture,
/// and the picture is the point -- the whole page exists to be taken in at a glance.
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

        View.NavigateToString(Page(DarkWanted() ? "dark" : "light"));
    }

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
    private static string Page(string theme)
    {
        string body = Fragment();

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
            <html lang="de" data-theme="{{theme}}">
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

    /// <summary>The page as it ships, out of the assembly.</summary>
    private static string Fragment()
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

        return reader.ReadToEnd();
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

        // Wide enough for the three trays to sit side by side, which is where the page says
        // something the words do not: the first is named, the second counted, the third is a
        // number. Below about 900 CSS pixels they stack and that reading is lost. Clamped to
        // the work area so a 1366x768 laptop still gets a whole window.
        int width = Math.Min((int)Math.Round(1180 * scale), (int)(area.WorkArea.Width * 0.94));
        int height = Math.Min((int)Math.Round(820 * scale), (int)(area.WorkArea.Height * 0.94));

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
