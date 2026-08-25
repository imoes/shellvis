using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shellvis.Core.Desktop;

/// <summary>
/// Enumerates and manipulates top-level desktop windows.
///
/// This is the cheap half of desktop awareness. Walking the UI Automation tree of
/// every running app costs hundreds of milliseconds and floods the model context; a
/// window list costs almost nothing and is usually enough to answer "what is open" or
/// to choose a target. So the agent gets this first and pays for a full UIA snapshot
/// only once it knows which window it cares about.
///
/// Enumeration walks the Z-order chain rather than calling EnumWindows. Two reasons:
/// the generated P/Invoke surface uses unmanaged function pointers for callbacks
/// (so a capturing callback is not an option), and Z-order is exactly the ordering an
/// agent wants -- the window on top is the one the user is looking at.
/// </summary>
public static class WindowInspector
{
    /// <summary>Upper bound on the Z-order walk, so a corrupted chain cannot hang the agent.</summary>
    private const int MaxWindows = 5000;

    /// <summary>
    /// List the visible top-level windows in Z-order, front to back.
    /// </summary>
    /// <param name="includeUntitled">
    /// Include windows with no caption. Off by default: the desktop is full of
    /// invisible helper and message-only windows that are pure noise in a prompt.
    /// </param>
    public static IReadOnlyList<WindowInfo> ListWindows(bool includeUntitled = false)
    {
        var found = new List<WindowInfo>();
        HWND foreground = PInvoke.GetForegroundWindow();

        HWND current = PInvoke.GetTopWindow(HWND.Null);
        for (int guard = 0; current != HWND.Null && guard < MaxWindows; guard++)
        {
            WindowInfo? info = Describe(current, foreground, includeUntitled);
            if (info is not null)
                found.Add(info);

            current = PInvoke.GetWindow(current, GET_WINDOW_CMD.GW_HWNDNEXT);
        }

        return found;
    }

    /// <summary>Look up a single window by handle, or null if it is gone or hidden.</summary>
    public static WindowInfo? Describe(nint handle) =>
        Describe(ToHwnd(handle), PInvoke.GetForegroundWindow(), includeUntitled: true);

    /// <summary>The window the user is currently working in, if any.</summary>
    public static WindowInfo? Foreground()
    {
        HWND hwnd = PInvoke.GetForegroundWindow();
        return hwnd == HWND.Null ? null : Describe(hwnd, hwnd, includeUntitled: true);
    }

    /// <summary>
    /// Bring a window to the front and restore it if minimized.
    ///
    /// Windows refuses SetForegroundWindow from a process that does not own the
    /// current foreground window, so this is best-effort by design: it reports whether
    /// the window actually ended up in front rather than pretending it succeeded.
    /// </summary>
    public static bool Activate(nint handle)
    {
        HWND hwnd = ToHwnd(handle);

        // A minimized window cannot take focus, so restore before raising.
        if (PInvoke.IsIconic(hwnd))
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

        PInvoke.SetForegroundWindow(hwnd);
        return PInvoke.GetForegroundWindow() == hwnd;
    }

    private static unsafe WindowInfo? Describe(HWND hwnd, HWND foreground, bool includeUntitled)
    {
        if (!PInvoke.IsWindowVisible(hwnd))
            return null;

        string title = ReadWindowText(hwnd);
        if (!includeUntitled && string.IsNullOrWhiteSpace(title))
            return null;

        if (!PInvoke.GetWindowRect(hwnd, out RECT rect))
            return null;

        // A zero-size window is real but has nothing an agent can look at or click.
        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0)
            return null;

        uint processId = 0;
        PInvoke.GetWindowThreadProcessId(hwnd, &processId);

        WindowDisplayState state = PInvoke.IsIconic(hwnd)
            ? WindowDisplayState.Minimized
            : PInvoke.IsZoomed(hwnd)
                ? WindowDisplayState.Maximized
                : WindowDisplayState.Normal;

        return new WindowInfo(
            Handle: (nint)hwnd.Value,
            Title: title,
            ClassName: ReadClassName(hwnd),
            ProcessId: (int)processId,
            ProcessName: ResolveProcessName((int)processId),
            Left: rect.left,
            Top: rect.top,
            Width: width,
            Height: height,
            State: state,
            IsForeground: hwnd == foreground);
    }

    private static unsafe string ReadWindowText(HWND hwnd)
    {
        int length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0)
            return string.Empty;

        // GetWindowText writes a terminating null inside the buffer it is handed, so
        // the buffer must be one larger than the reported length.
        char[] buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            int written = PInvoke.GetWindowText(hwnd, new PWSTR(p), buffer.Length);
            return written <= 0 ? string.Empty : new string(p, 0, written);
        }
    }

    private static unsafe string ReadClassName(HWND hwnd)
    {
        // 256 is the documented maximum length of a window class name.
        char[] buffer = new char[256];
        fixed (char* p = buffer)
        {
            int written = PInvoke.GetClassName(hwnd, new PWSTR(p), buffer.Length);
            return written <= 0 ? string.Empty : new string(p, 0, written);
        }
    }

    private static unsafe HWND ToHwnd(nint handle) => new((void*)handle);

    private static string ResolveProcessName(int processId)
    {
        if (processId <= 0)
            return string.Empty;

        // Process lookup throws for processes that exited between enumeration and
        // now, and for ones this user cannot open (elevated or protected). Neither is
        // worth failing an entire window listing over.
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
