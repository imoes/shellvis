using System.Globalization;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Shellvis.Core.Notes;
using Shellvis.Shell.Interop;

using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shellvis.Shell.Views;

/// <summary>
/// One note stuck to the desktop.
///
/// <b>What is being rebuilt.</b> Three programs are remembered as one: the XP Tablet
/// Edition ink tool, Vista's Sidebar Notes gadget and its Tablet PC Sticky Notes with the
/// automatic timestamp, and Windows 7's StikyNot.exe, which is the yellow square most people
/// mean. What they share is the behaviour, and the behaviour is the requirement: a frameless
/// window per note, no taskbar button and no Alt-Tab entry, on top but unobtrusive, dragged
/// from anywhere, resized from a corner, saved without being asked, and back where it was
/// after a restart. A note you have to save is a document.
///
/// <b>No code was taken from the projects that were checked.</b> The most mature open-source
/// one is Java, the one in the right language is a tabbed overlay with eight stars, and the
/// most polished program of the kind is proprietary freeware. What made building it
/// worthwhile anyway is that the hard parts already exist here: a rounded frameless window
/// (<see cref="WindowShaper"/>), dragging from any surface (the pill does it), and stepping
/// aside for a full-screen or remote session (<see cref="ForegroundState"/>). Adopting a WPF
/// project would have meant two UI stacks in one process to avoid writing a text box.
///
/// <b>WS_EX_TOOLWINDOW is the load-bearing line.</b> Without it every note is a window: it
/// gets a taskbar button, it appears in Alt-Tab, and eight notes make the taskbar useless.
/// That single style is most of the difference between a note and an application.
/// </summary>
public sealed partial class StickyWindow : Window
{
    private readonly WindowShaper _shaper;
    private readonly nint _handle;

    /// <summary>Told when anything changes, so the store can write it down.</summary>
    private readonly Action<StickyWindow> _changed;

    private readonly Action<StickyWindow> _closed;

    /// <summary>Where the pointer and the window were when a drag began, in screen pixels.</summary>
    private PointInt32 _dragFrom;
    private PointInt32 _windowFrom;
    private bool _dragging;

    public StickyWindow(Sticky sticky, Action<StickyWindow> changed, Action<StickyWindow> closed)
    {
        InitializeComponent();

        Id = sticky.Id;
        Colour = sticky.Colour;
        _changed = changed;
        _closed = closed;

        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _shaper = new WindowShaper(_handle);

        ExtendsContentIntoTitleBar = true;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);

            // Resizable from the edges, which is how the original worked and how anyone
            // expects a note to behave. Not maximisable: a full-screen sticky note is not a
            // thing, and the button would only ever be pressed by accident.
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        HideFromTaskbarAndAltTab();
        _shaper.TrySoftenEdges();

        Body.Text = sticky.Text;
        Stamp.Text = sticky.Updated.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
        Paint(sticky.Colour);

        AppWindow.MoveAndResize(new RectInt32(sticky.X, sticky.Y, sticky.Width, sticky.Height));

        // Saved on every keystroke rather than on a save command or on close. A note that
        // needs saving is a document, and a note lost to a power cut is worse than one that
        // costs a small write per character.
        Body.TextChanged += (_, _) =>
        {
            Stamp.Text = DateTime.Now.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
            _changed(this);
        };

        CloseButton.Click += (_, _) => Discard();
        ColourButton.Click += (_, _) => NextColour();

        MakeDraggable(Header);
        MakeDraggable(Surface);

        // The size and position are read back from the window rather than tracked, so a
        // drag by the system border counts the same as a drag by the surface.
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
                _changed(this);
        };

        Closed += (_, _) => _closed(this);
    }

    public long Id { get; }

    public StickyColour Colour { get; private set; }

    public string Text => Body.Text;

    public int X => AppWindow.Position.X;

    public int Y => AppWindow.Position.Y;

    public int Width => AppWindow.Size.Width;

    public int Height => AppWindow.Size.Height;

    /// <summary>Throw this note away, window and record together.</summary>
    public void Discard()
    {
        _discarded = true;
        Close();
    }

    /// <summary>Whether it was thrown away rather than closed with the application.</summary>
    public bool Discarded => _discarded;

    private bool _discarded;

    /// <summary>Move to the next colour, saving as it goes.</summary>
    private void NextColour()
    {
        // A cycle rather than a picker. Five colours reachable by clicking are quicker than
        // five in a flyout, and a flyout on a note this size covers the note.
        StickyColour[] wheel = Enum.GetValues<StickyColour>();

        Colour = wheel[(Array.IndexOf(wheel, Colour) + 1) % wheel.Length];

        Paint(Colour);
        _changed(this);
    }

    private void Paint(StickyColour colour)
    {
        Surface.Background = Brush($"Sticky{colour}Brush");
        Body.Foreground = Brush("StickyInkBrush");
    }

    /// <summary>
    /// Take the window out of the taskbar and out of Alt-Tab.
    ///
    /// The one interop call that makes a note a note. WS_EX_TOOLWINDOW does both at once,
    /// and WS_EX_APPWINDOW has to go with it, because a window carrying both is shown.
    ///
    /// A failure here is cosmetic and is swallowed: a note that works but also appears in
    /// Alt-Tab is a worse note, not a broken application.
    /// </summary>
    private unsafe void HideFromTaskbarAndAltTab()
    {
        try
        {
            var hwnd = new HWND((void*)_handle);

            nint style = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

            style |= (nint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
            style &= ~(nint)WINDOW_EX_STYLE.WS_EX_APPWINDOW;

            PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Drag from anywhere on the note.
    ///
    /// The cursor position comes from Win32 rather than from the pointer event, because the
    /// window is what moves: a WinUI position is relative to the window, so the coordinate
    /// system shifts out from under the gesture. The pill learned this first.
    /// </summary>
    private void MakeDraggable(UIElement surface)
    {
        surface.PointerPressed += (sender, e) =>
        {
            if (sender is not UIElement element)
                return;

            if (!PInvoke.GetCursorPos(out System.Drawing.Point cursor))
                return;

            _dragging = true;
            _dragFrom = new PointInt32(cursor.X, cursor.Y);
            _windowFrom = new PointInt32(AppWindow.Position.X, AppWindow.Position.Y);
            element.CapturePointer(e.Pointer);
        };

        surface.PointerMoved += (_, _) =>
        {
            if (!_dragging || !PInvoke.GetCursorPos(out System.Drawing.Point now))
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
            : new SolidColorBrush(Colors.Khaki);
}
