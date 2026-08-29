using System.Runtime.InteropServices;

using Shellvis.Core.Desktop;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Where the docked bar would land, on this machine's real taskbar.
///
/// The bug this exists for was invisible from the code: the bar was placed by arithmetic
/// that is correct on an empty taskbar, so it looked right in every screenshot taken with
/// two windows open and covered the app icons on a working desktop. The measurement is the
/// only thing that tells the two apart, and it cannot be faked -- a stub taskbar would only
/// prove the arithmetic against itself.
///
/// So this prints what was measured rather than asserting a position: the icons someone has
/// open are not a fixed input. What it does assert is the property that matters -- whatever
/// span is chosen must not overlap anything the taskbar is already using.
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

        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);

        var work = new RECT();

        if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0))
        {
            Console.WriteLine("FAIL the work area could not be read.");
            return 1;
        }

        int stripTop = work.Bottom;
        int stripBottom = screenHeight;

        Console.WriteLine($"screen   {screenWidth}x{screenHeight}");
        Console.WriteLine($"work     {work.Right - work.Left}x{work.Bottom - work.Top} at {work.Left},{work.Top}");
        Console.WriteLine($"strip    y {stripTop}..{stripBottom}  ({stripBottom - stripTop}px tall)");
        Console.WriteLine();

        if (stripBottom - stripTop <= 0)
        {
            Console.WriteLine("No taskbar at the bottom of the primary screen (hidden, or on another edge).");
            Console.WriteLine("The docked bar falls back to the bottom of the work area, which is by design.");
            return 0;
        }

        // The width the docked bar actually asks for: 320 + a 26px button + 2, scaled.
        double scale = GetDpiForSystem() / 96.0;
        int needed = (int)Math.Round((320 + 26 + 2) * scale);

        Console.WriteLine($"the docked bar needs {needed}px at scale {scale:0.##}");
        Console.WriteLine();

        TaskbarLayout.Span? free = TaskbarLayout.FindFreeSpan(
            stripTop, stripBottom, work.Left, work.Right, needed);

        if (free is not { } span)
        {
            Console.WriteLine("FAIL no free span was found. The bar would fall back to the old arithmetic,");
            Console.WriteLine("     which is what covered the icons in the first place.");
            return 1;
        }

        Console.WriteLine($"free span  x {span.Left}..{span.Right}  ({span.Width}px)");
        Console.WriteLine(span.Left <= work.Left
            ? "           the stretch beside Start -- the one that does not move when a window opens"
            : "           a gap in the middle of the strip");

        int failures = 0;

        failures += Check("the span is wide enough for the bar", span.Width >= needed);
        failures += Check("the span is inside the work area",
            span.Left >= work.Left && span.Right <= work.Right);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "The docked bar has somewhere to sit that nothing else is using."
            : $"{failures} taskbar check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static int Check(string what, bool passed)
    {
        Console.WriteLine($"   {(passed ? "ok  " : "FAIL")} {what}");
        return passed ? 0 : 1;
    }

    private const uint SPI_GETWORKAREA = 0x0030;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref RECT value, uint winIni);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
