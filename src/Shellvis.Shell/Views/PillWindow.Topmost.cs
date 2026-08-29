using Microsoft.UI.Xaml;

using Shellvis.Core.Config;

// Aliased for the same reason as elsewhere: DispatcherQueueTimer exists in two namespaces.
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Shellvis.Shell.Views;

/// <summary>
/// Stepping out of the way of whatever the user is actually working in.
///
/// <b>The problem with always-on-top.</b> It is right almost all the time -- a command bar
/// you have to hunt for is a command bar you stop using -- and wrong in one situation: when
/// the foreground window has taken over the screen. A remote desktop session is the clear
/// case. It fills the display, it captures the keyboard (measured in this project: Ctrl+Alt+
/// Space went into the remote session rather than raising the pill), and a floating bar
/// sitting on top of it covers part of another machine's desktop while being unusable itself.
///
/// <b>What it yields to, and why that is mostly a shape and not a list.</b> Two structural
/// signals decide it:
///
/// <list type="bullet">
/// <item>The foreground window covers its entire monitor. That is what "has taken over the
/// screen" means, and it catches a full-screen remote session, a presentation, a game and a
/// video without knowing anything about any of them.</item>
/// <item><c>SHQueryUserNotificationState</c> reports a presentation or a full-screen
/// Direct3D application. This is the API Windows itself uses to decide whether a notification
/// may appear over what is running, which is the same question being asked here.</item>
/// </list>
///
/// A name list exists as well, and it is the part worth being uneasy about. A remote desktop
/// client in a WINDOW is not full-screen and reports nothing unusual, yet it still swallows
/// the keyboard -- so there is no shape to test, and the only remaining signal is who it is.
/// This project has argued against exactly that (the non-speech annotation list was replaced
/// by a shape rule for good reason), so the list is small, defaulted to the remote clients
/// this is about, and left in the config where the user can extend it rather than pretending
/// to be complete.
///
/// <b>Yielding means getting behind, not merely giving up the topmost flag.</b> The first
/// revision only cleared the flag, and it was reported still showing over a remote desktop
/// connection -- correctly, because HWND_NOTOPMOST places a window above all NON-topmost
/// windows, which is still above the session. The window is therefore also inserted directly
/// below the window in front. It is not moved, resized or hidden, and it keeps its place: if
/// the window in front does not cover it, it stays visible, which is the desired outcome for
/// an ordinary windowed application.
///
/// It only yields while something ELSE is in the foreground. Activating Shellvis puts it back
/// on top immediately, so the hotkey and the tray icon still bring it up over a remote session
/// when that is what you want.
/// </summary>
public sealed partial class PillWindow
{
    private DispatcherQueueTimer? _topmostTimer;

    /// <summary>What the window is currently set to, so it is not reasserted every tick.</summary>
    private bool _topmost = true;

    private string[] _yieldTo = [];

    /// <summary>
    /// How often the foreground is examined.
    ///
    /// Polling rather than a foreground event hook. <c>SetWinEventHook</c> would be the
    /// tidier mechanism and it needs its own thread with a message pump plus an unmanaged
    /// callback -- the machinery the space hook already needed for a reason that does not
    /// apply here. The checks are three cheap calls, and a second of lag before the bar steps
    /// aside is not something anyone will notice.
    /// </summary>
    private const int PollMilliseconds = 700;

    private void RegisterTopmostYield()
    {
        _yieldTo = ConfigStore.Load().Config.Window.YieldTo ?? [];

        _topmostTimer = DispatcherQueue.CreateTimer();
        _topmostTimer.Interval = TimeSpan.FromMilliseconds(PollMilliseconds);
        _topmostTimer.IsRepeating = true;
        _topmostTimer.Tick += (_, _) => UpdateTopmost();
        _topmostTimer.Start();

        // Also on activation, so raising the pill puts it back on top at once rather than
        // after up to seven tenths of a second of sitting behind what it was yielding to.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                SetTopmost(true);
        };
    }

    private void UpdateTopmost()
    {
        nint self = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Docked is a different situation entirely, and treating it like the floating bar was a
        // defect: the docked bar sits ON the taskbar strip, and the taskbar is itself a topmost
        // window. Measured -- with the bar docked, dropping out of the topmost band put
        // Shell_TrayWnd at the bar's own coordinates, so it did not step behind anything, it
        // vanished. "Yielding is not hiding" is true for the floating bar and false here.
        //
        // It is also unnecessary: a bar inside the taskbar strip is not covering anyone's remote
        // session, and a window that genuinely takes over the screen covers the taskbar and the
        // bar with it.
        if (_docked)
        {
            HoldAboveTaskbar(self);
            return;
        }

        bool yield = Interop.ForegroundState.ShouldYield(
            self, _yieldTo, out string? reason, out nint front);

        if (yield)
        {
            // Re-applied whenever the window in front CHANGES, not once. Getting behind a
            // window is a position in the z-order rather than a state, so switching from one
            // full-screen window to another would otherwise leave the bar above the new one.
            if (_topmost || front != _behind)
            {
                _topmost = false;
                _behind = front;
                Interop.ForegroundState.StepBehind(self, front);
            }
        }
        else if (!_topmost)
        {
            _topmost = true;
            _behind = 0;
            Interop.ForegroundState.Raise(self);
        }

        // Said once per change, not per tick. The bar moving behind another window looks like
        // a glitch if nothing explains it, and it is the sort of thing a user would otherwise
        // report as "Shellvis disappeared".
        if (reason is not null && reason != _lastYieldReason)
        {
            _lastYieldReason = reason;
            AddRow(GlyphTool, reason, "window");
        }
        else if (reason is null)
        {
            _lastYieldReason = null;
        }
    }

    private string? _lastYieldReason;

    /// <summary>
    /// Keep the docked bar above the taskbar, re-asserting it rather than assuming it holds.
    ///
    /// Two windows are competing here and both are topmost: this bar and Shell_TrayWnd. Being
    /// in the topmost band is not a position within it, so anything that raises the taskbar --
    /// and the shell raises it readily -- puts the bar underneath, at which point it is
    /// completely invisible because it occupies the taskbar's own strip.
    ///
    /// The style flag is deliberately NOT consulted. An earlier version tracked "am I topmost"
    /// in a field and only acted on a change, which meant the bar could be covered and stay
    /// covered indefinitely: the flag was still set, so by its own account nothing was wrong.
    /// It was reported as Shellvis disappearing when you click elsewhere, and reproduced
    /// exactly -- with the bar docked and the topmost band reordered, the window at the bar's
    /// coordinates was Shell_TrayWnd and a full poll later it still was.
    ///
    /// Re-inserting at the top of the band every tick is cheap and does not activate anything.
    /// Only while docked: doing it to the floating bar would be an application insisting on
    /// being above every other topmost window for no reason.
    /// </summary>
    private void HoldAboveTaskbar(nint self)
    {
        _topmost = true;
        _behind = 0;
        _lastYieldReason = null;

        Interop.ForegroundState.Raise(self);
    }

    /// <summary>The window the bar is currently sitting behind, or zero when it is on top.</summary>
    private nint _behind;

    /// <summary>
    /// Put the bar back on top, used when it is activated.
    ///
    /// SetWindowPos rather than the presenter's IsAlwaysOnTop: the presenter property raises
    /// and can activate the window, which would take the focus from the very session being
    /// stepped aside for.
    /// </summary>
    private void SetTopmost(bool topmost)
    {
        if (_topmost == topmost)
            return;

        _topmost = topmost;
        _behind = 0;

        if (topmost)
            Interop.ForegroundState.Raise(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }
}
