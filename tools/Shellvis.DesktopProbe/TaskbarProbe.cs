using System.Runtime.InteropServices;

using Shellvis.Core.Desktop;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Where the docked bar would land, on this machine's real taskbars.
///
/// The bug this exists for was invisible from the code: the bar was placed by arithmetic
/// that is correct on an empty taskbar, so it looked right in every screenshot taken with
/// two windows open and covered the app icons on a working desktop. The measurement is the
/// only thing that tells the two apart, and it cannot be faked -- a stub taskbar would only
/// prove the arithmetic against itself.
///
/// <b>Every monitor, and that is the second lesson.</b> The first version of this checked the
/// primary screen: it read the primary work area, measured the primary strip, and passed --
/// while the bar sat 28 pixels over an icon on the second monitor. A second display gets its
/// own taskbar window, of a different class, which the placement code was not looking at. A
/// harness that only ever examines the primary screen cannot see that, however carefully it
/// asserts.
///
/// So this prints what was measured rather than asserting a position -- the icons somebody
/// has open are not a fixed input -- and asserts the two properties that matter: that each
/// strip was actually READ, and that the span chosen on it overlaps nothing.
/// </summary>
internal static class TaskbarProbe
{
    public static int Run()
    {
        Console.WriteLine("=== Taskbar placement ===");
        Console.WriteLine();

        // Physical pixels throughout. The window is placed in physical pixels and UI
        // Automation reports physical pixels; converting anything here would only introduce
        // the DPI mistake this is meant to catch.
        SetProcessDPIAware();

        List<(RECT Monitor, RECT Work, bool Primary)> screens = Monitors();

        if (screens.Count == 0)
        {
            Console.WriteLine("FAIL no monitors could be enumerated.");
            return 1;
        }

        // The width the docked bar actually asks for: 320 + a 26px button + 2, scaled.
        double scale = GetDpiForSystem() / 96.0;
        int needed = (int)Math.Round((320 + 26 + 2) * scale);

        Console.WriteLine($"{screens.Count} monitor(s); the docked bar needs {needed}px at scale {scale:0.##}");
        Console.WriteLine();

        int failures = 0;

        foreach ((RECT monitor, RECT work, bool primary) in screens)
        {
            Console.WriteLine(
                $"-- {(primary ? "primary" : "secondary")} monitor "
                + $"{monitor.Right - monitor.Left}x{monitor.Bottom - monitor.Top} "
                + $"at {monitor.Left},{monitor.Top} --");

            int stripTop = work.Bottom;
            int stripBottom = monitor.Bottom;

            if (stripBottom - stripTop <= 0)
            {
                Console.WriteLine("   ..   no taskbar along the bottom here (hidden, or on another edge).");
                Console.WriteLine("        The bar falls back to the bottom of the work area, by design.");
                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"   strip  y {stripTop}..{stripBottom}, x {work.Left}..{work.Right}");

            // THE check that was missing. Every taskbar has a Start button, so an empty
            // measurement means the strip was not read -- and a strip that reads as empty is
            // a strip the bar will happily sit in the middle of.
            List<TaskbarLayout.Span> used =
                TaskbarLayout.Used(stripTop, stripBottom, work.Left, work.Right);

            failures += Check(
                "the strip was measured, not assumed empty",
                used.Count > 0,
                used.Count > 0
                    ? $"{used.Count} occupied run(s), first at x {used[0].Left}..{used[0].Right}"
                    : "a taskbar always has a Start button; nothing found means nothing was read");

            TaskbarLayout.Span? free = TaskbarLayout.FindFreeSpan(
                stripTop, stripBottom, work.Left, work.Right, needed);

            if (free is not { } span)
            {
                failures += Check("a free span was found", false,
                    "the bar would fall back to arithmetic, which is what covered the icons");

                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"   free   x {span.Left}..{span.Right}  ({span.Width}px)");

            failures += Check("the span is wide enough for the bar", span.Width >= needed);

            failures += Check("the span is inside the work area",
                span.Left >= work.Left && span.Right <= work.Right);

            // The property the whole file exists for.
            TaskbarLayout.Span? clash = used.FirstOrDefault(
                u => u.Left < span.Right && u.Right > span.Left) is { Width: > 0 } hit
                    ? hit
                    : null;

            failures += Check("and it overlaps nothing the taskbar is using",
                clash is null,
                clash is { } over ? $"overlaps x {over.Left}..{over.Right}" : string.Empty);

            Console.WriteLine();
        }

        Console.WriteLine(failures == 0
            ? "On every screen with a taskbar, the docked bar has somewhere to sit that\n"
              + "nothing else is using -- and the strip was read rather than assumed."
            : $"{failures} taskbar check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static int Check(string what, bool passed, string detail = "")
    {
        Console.WriteLine($"   {(passed ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        return passed ? 0 : 1;
    }

    /// <summary>Every monitor, with its bounds and its work area.</summary>
    private static List<(RECT Monitor, RECT Work, bool Primary)> Monitors()
    {
        var found = new List<(RECT, RECT, bool)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            if (GetMonitorInfo(monitor, ref info))
                found.Add((info.rcMonitor, info.rcWork, (info.dwFlags & MonitorPrimary) != 0));

            return true;
        }, IntPtr.Zero);

        // Primary first, so the output reads in the order somebody thinks about their desk.
        return [.. found.OrderByDescending(m => m.Item3)];
    }

    private const uint MonitorPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorFound(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorFound callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
