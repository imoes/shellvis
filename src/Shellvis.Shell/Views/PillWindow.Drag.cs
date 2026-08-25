using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Shellvis.Shell.Views;

/// <summary>
/// Dragging the pill around the desktop.
///
/// Done by hand rather than with AppWindow.TitleBar drag rectangles. A drag rectangle is
/// a region the system claims for window movement, and everything inside it stops
/// receiving input -- on a bar that is almost entirely input field and buttons, the
/// rectangles would have to be threaded around every control and rebuilt whenever the
/// layout changes (docking, the console opening, a button appearing). Handling the pointer
/// is less machinery and does not fight the controls for clicks.
///
/// The pointer is tracked with GetCursorPos rather than with the WinUI pointer position.
/// A WinUI position is relative to the window, and the window is the thing being moved, so
/// the coordinate system shifts underneath the gesture. Screen coordinates from the OS are
/// absolute, already in physical pixels, and need no scale conversion at all.
/// </summary>
public sealed partial class PillWindow
{
    private bool _dragging;

    /// <summary>Where the cursor was when the drag began, in screen pixels.</summary>
    private System.Drawing.Point _dragCursorStart;

    /// <summary>Where the window was when the drag began.</summary>
    private PointInt32 _dragWindowStart;

    /// <summary>
    /// Make an element a drag handle.
    ///
    /// Applied to the tint layers, which sit under the content and are hit-testable
    /// because they have a Background. The content grids have none, so a click that lands
    /// between two controls falls through to the tint and starts a drag, while a click on
    /// a button reaches the button. That is exactly the behaviour asked for: grab the
    /// blank part and move it.
    /// </summary>
    private void MakeDraggable(UIElement surface)
    {
        surface.PointerPressed += OnDragPressed;
        surface.PointerMoved += OnDragMoved;
        surface.PointerReleased += OnDragReleased;
        surface.PointerCaptureLost += (_, _) => EndDrag();
        surface.PointerCanceled += (_, _) => EndDrag();
    }

    private void OnDragPressed(object sender, PointerRoutedEventArgs e)
    {
        // The docked bar is positioned onto the taskbar strip by PlaceOnTaskbar; letting
        // it be dragged off would leave a bar that looks docked and is not, with nothing
        // to put it back.
        if (_docked)
            return;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            return;

        if (!PInvoke.GetCursorPos(out System.Drawing.Point cursor))
            return;

        _dragCursorStart = cursor;
        _dragWindowStart = AppWindow.Position;
        _dragging = true;

        // Capture, so the drag survives the pointer leaving the pill. Without it a fast
        // gesture outruns the window and the move stops halfway.
        if (sender is UIElement surface)
            surface.CapturePointer(e.Pointer);

        e.Handled = true;
    }

    private void OnDragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !PInvoke.GetCursorPos(out System.Drawing.Point cursor))
            return;

        AppWindow.Move(new PointInt32(
            _dragWindowStart.X + (cursor.X - _dragCursorStart.X),
            _dragWindowStart.Y + (cursor.Y - _dragCursorStart.Y)));

        e.Handled = true;
    }

    private void OnDragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement surface)
            surface.ReleasePointerCapture(e.Pointer);

        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;

        _dragging = false;

        // The window may have landed on a monitor with a different scale factor, and the
        // region is built in physical pixels. Without this the silhouette keeps the old
        // monitor's size and the clip stops matching what is painted.
        ApplyRegion(_consoleOpen ? PillMetrics.ConsoleHeight : 0);
    }
}
