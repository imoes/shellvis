using System.Diagnostics;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shellvis.Shell.Interop;

/// <summary>
/// Whether the pill should be above the foreground window, or behind it.
///
/// Separated from the window so the decision is testable on its own: it is a pure function of
/// the foreground window's geometry, the notification state and a name list, and the harness
/// can check it without a pill on screen.
/// </summary>
internal static class ForegroundState
{
    private static readonly nint HwndTopmost = -1;
    private static readonly nint HwndNoTopmost = -2;

    /// <summary>
    /// Decide whether to step out of the way, and say why when the answer is yes.
    /// </summary>
    /// <param name="self">The pill's own window, which is never yielded to.</param>
    /// <param name="yieldTo">
    /// Process names to yield to regardless of geometry, without extension and case
    /// insensitive.
    /// </param>
    public static bool ShouldYield(
        nint self, IReadOnlyList<string> yieldTo, out string? reason, out nint front)
    {
        reason = null;

        HWND foreground = PInvoke.GetForegroundWindow();
        front = foreground.IsNull ? 0 : (nint)(long)foreground;

        // Ours, or nobody's. Yielding to our own window would make the bar drop behind the
        // console it is attached to.
        if (foreground.IsNull || foreground == (HWND)self)
            return false;

        if (IsOwnProcess(foreground))
            return false;

        // Windows' own answer to "may something be put over what is running". A presentation
        // or a full-screen Direct3D application is the same situation this is about, and using
        // the documented signal beats inventing one.
        if (PInvoke.SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state).Succeeded
            && state is QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
        {
            reason = "A full-screen application is in front, so Shellvis has stepped behind it. "
                + "Ctrl+Alt+Space still brings it up.";

            return true;
        }

        string process = ProcessName(foreground);

        // The name list, and the reason it is second rather than first: a windowed remote
        // desktop client has no distinguishing shape at all. It is not full-screen and it
        // reports nothing unusual, yet it takes the keyboard, so identity is the only signal
        // left. Kept in the config so it can be corrected without a build.
        foreach (string candidate in yieldTo)
        {
            if (candidate.Length > 0
                && process.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"{process} is in front and takes the keyboard, so Shellvis has stepped "
                    + "behind it. Ctrl+Alt+Space still brings it up.";

                return true;
            }
        }

        if (CoversItsMonitor(foreground))
        {
            reason = $"{process} covers the whole screen, so Shellvis has stepped behind it. "
                + "Ctrl+Alt+Space still brings it up.";

            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a window covers its entire monitor.
    ///
    /// Compared against the monitor the window is on rather than the primary display: on a
    /// multi-monitor machine a maximised window on one screen is not full-screen anywhere
    /// else, and the pill has no reason to yield to it if it is not even in the way.
    ///
    /// A tolerance of a pixel or two, because a genuinely full-screen window is sometimes
    /// reported one pixel larger than the monitor.
    /// </summary>
    private static bool CoversItsMonitor(HWND window)
    {
        if (!PInvoke.GetWindowRect(window, out RECT bounds))
            return false;

        HMONITOR monitor = PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        if (monitor.IsNull)
            return false;

        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };

        if (!PInvoke.GetMonitorInfo(monitor, ref info))
            return false;

        RECT screen = info.rcMonitor;

        return Shellvis.Core.Desktop.ScreenGeometry.CoversMonitor(
            bounds.left, bounds.top, bounds.right, bounds.bottom,
            screen.left, screen.top, screen.right, screen.bottom);
    }

    /// <summary>Whether the window belongs to this process, which is never yielded to.</summary>
    private static unsafe bool IsOwnProcess(HWND window)
    {
        uint pid = 0;
        _ = PInvoke.GetWindowThreadProcessId(window, &pid);

        return pid == Environment.ProcessId;
    }

    private static unsafe string ProcessName(HWND window)
    {
        try
        {
            uint pid = 0;
            _ = PInvoke.GetWindowThreadProcessId(window, &pid);

            if (pid == 0)
                return string.Empty;

            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // A process that ended between the two calls is ordinary, not exceptional.
            return string.Empty;
        }
    }

    /// <summary>Put the window back above everything.</summary>
    public static void Raise(nint window) => Place(window, HwndTopmost);

    /// <summary>
    /// Drop out of the topmost band AND get behind the window in front.
    ///
    /// Two calls, and the second one is the whole point. HWND_NOTOPMOST clears the topmost
    /// style but places the window "above all non-topmost windows" -- so a bar that was on
    /// top of a remote desktop session stayed on top of it, now as an ordinary window rather
    /// than a topmost one. It was reported exactly that way: still shown over the connection.
    /// The topmost bit was genuinely cleared, which is what had been measured; that clearing
    /// it is not the same as getting out of the way was assumed rather than checked.
    ///
    /// So the window is then inserted directly BELOW the window in front. Below that one
    /// specifically rather than at the very bottom of the z-order: the bar should be behind
    /// what the user is working in, not behind everything they own.
    /// </summary>
    public static void StepBehind(nint window, nint front)
    {
        Place(window, HwndNoTopmost);

        if (front != 0 && front != window)
            Place(window, front);
    }

    /// <summary>
    /// Reorder without moving, resizing or activating.
    ///
    /// SWP_NOACTIVATE matters more than it looks: the entire purpose is to get out of the way
    /// of the window that has the focus, and taking the focus while doing so would be worse
    /// than having stayed on top.
    /// </summary>
    private static void Place(nint window, nint insertAfter) =>
        PInvoke.SetWindowPos(
            (HWND)window,
            (HWND)insertAfter,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
}
