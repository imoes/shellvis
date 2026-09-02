using Microsoft.UI.Xaml;

using Shellvis.Core.Desktop;

// Aliased for the same reason as elsewhere: DispatcherQueueTimer exists in two namespaces.
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Shellvis.Shell.Views;

/// <summary>
/// Living on the taskbar's level, and nowhere else.
///
/// <b>What this used to do, and why it was wrong.</b> The bar decided for itself when to get out
/// of the way. Every seven tenths of a second it looked at the foreground window and applied
/// three rules: does it cover its whole monitor, does <c>SHQueryUserNotificationState</c> report
/// a presentation or a full-screen Direct3D application, and is its process name in a list kept
/// in <c>config.yaml</c> -- defaulted to the Microsoft remote desktop clients. When any of them
/// fired, the window was taken out of the topmost band and inserted below the window in front.
///
/// Every one of those rules was a guess about what Windows was about to do, and the guesses were
/// wrong in both directions at once. The bar dropped behind windows it had decided to yield to
/// and was reported as simply disappearing; and it still sat in front of a remote desktop
/// connection, because a connection that matched none of the three rules was not recognised at
/// all. Adding a fourth rule would have been more of the same.
///
/// <b>What it does now.</b> The window registers as an application desktop toolbar and does what
/// the shell tells it. That is the documented mechanism for a window that wants to behave like
/// part of the taskbar, and it is the same notification the shell uses to move the taskbar
/// itself: when a full-screen application takes the screen the bar drops to the bottom of the
/// z-order, and when the last one closes it comes back. Nothing is polled, no process is named,
/// and there is no rule of ours to be wrong -- if the taskbar is visible, so is Shellvis, and if
/// the taskbar has gone, so has Shellvis.
///
/// See <see cref="TaskbarBand"/> for the decision and <see cref="Interop.AppBar"/> for the
/// wiring, including why the taskbar's <c>ABS_ALWAYSONTOP</c> flag is not consulted.
///
/// <b>The one thing still re-asserted on a timer</b> is the docked bar's place WITHIN the
/// topmost band, and only while docked. Two topmost windows are competing for the same pixels
/// there: the bar lies on the taskbar strip, and being in the topmost band is not a position
/// within it, so anything that raises Shell_TrayWnd hides the bar completely. That is a fight
/// over one strip of screen rather than a judgement about what the user is doing, which is why
/// it survived and the rest did not.
/// </summary>
public sealed partial class PillWindow
{
    private Interop.AppBar? _appBar;
    private DispatcherQueueTimer? _bandTimer;

    /// <summary>
    /// How often the docked bar re-asserts itself above the taskbar.
    ///
    /// Only while docked, and only a <c>SetWindowPos</c> with <c>SWP_NOACTIVATE</c>: it moves
    /// nothing, resizes nothing and takes no focus.
    /// </summary>
    private const int BandHoldMilliseconds = 700;

    private void RegisterTopmostYield()
    {
        nint self = WinRT.Interop.WindowNative.GetWindowHandle(this);

        _appBar = new Interop.AppBar(self);

        _appBar.BandChanged += moved =>
        {
            // Once per change, in BOTH directions. A bar that moves behind another window
            // without explanation is what gets reported as "Shellvis disappeared" -- which is
            // how the machinery this replaced came to exist. Saying only that it stepped
            // aside, and never that it came back, is the same fault half-fixed: the console
            // then reads as two disappearances and no returns.
            if (moved is { Length: > 0 })
                AddRow(GlyphTool, moved, "window");
        };

        // The taskbar moved, changed size, or came back from autohide. The docked bar lies on
        // it, so it has to be placed again -- and this is now a notification rather than
        // something noticed late by a timer.
        _appBar.TaskbarMoved += () =>
        {
            if (_docked)
                PlaceOnTaskbar();
        };

        if (!_appBar.TryRegister())
        {
            // Explorer refused, which it does while it is restarting. The window is still
            // topmost by its presenter; it simply will not be told about full-screen
            // applications until the next start.
            AddRow(GlyphTool, "The shell would not register Shellvis as a taskbar toolbar, "
                + "so it will not step aside for full-screen applications.", "window");
        }

        _bandTimer = DispatcherQueue.CreateTimer();
        _bandTimer.Interval = TimeSpan.FromMilliseconds(BandHoldMilliseconds);
        _bandTimer.IsRepeating = true;
        _bandTimer.Tick += (_, _) =>
        {
            if (_docked)
                _appBar?.HoldAbove();
        };

        _bandTimer.Start();

        // Activating Shellvis puts it back where it belongs at once. Over a full-screen
        // application that means it stays at the bottom of the z-order until the application
        // closes, which is the taskbar's behaviour too: the hotkey shows the window, it does
        // not promote it above something the shell has decided owns the screen.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                _appBar?.ApplyPosition();
        };
    }

    /// <summary>Leave the shell's list of appbars. Called from the window's own teardown.</summary>
    private void UnregisterTopmostYield()
    {
        _bandTimer?.Stop();
        _bandTimer = null;

        _appBar?.Dispose();
        _appBar = null;
    }
}
