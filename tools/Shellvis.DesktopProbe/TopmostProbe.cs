using Shellvis.Core.Desktop;

namespace Shellvis.DesktopProbe;

/// <summary>
/// The rule that decides whether the bar steps out of the way.
///
/// The bar is always-on-top, which is right almost always and wrong when the foreground window
/// has taken over the screen -- a remote desktop session being the case this was built for. The
/// decision has three parts: the notification state Windows reports, a list of process names,
/// and this geometry. Only the geometry can be checked without a desktop, and it is also the
/// only one with arithmetic in it, so it is the one worth pinning down.
///
/// The tolerance is the point. Requiring an exact match to the monitor makes the rule almost
/// never fire, which is the failure the user would actually notice: the bar left sitting on top
/// of a remote session because the window reported itself one pixel inside the screen.
/// </summary>
internal static class TopmostProbe
{
    public static int Run()
    {
        Console.WriteLine("-- when the bar steps behind the foreground window --");

        int failures = 0;

        // A full-screen window on a 1920x1080 monitor at the origin.
        failures += Check(
            "a window exactly filling its monitor counts as covering it",
            ScreenGeometry.CoversMonitor(0, 0, 1920, 1080, 0, 0, 1920, 1080));

        // Reported a pixel or two short, which real windows do.
        failures += Check(
            "and so does one reported two pixels short of the edges",
            ScreenGeometry.CoversMonitor(2, 2, 1918, 1078, 0, 0, 1920, 1080));

        // Reported slightly larger, which they also do.
        failures += Check(
            "and one reported slightly larger than the monitor",
            ScreenGeometry.CoversMonitor(-2, -2, 1922, 1082, 0, 0, 1920, 1080));

        // A maximised window is NOT full-screen: the taskbar strip is still visible, so the
        // bar has somewhere to sit and no reason to move.
        failures += Check(
            "a merely maximised window does not, because the taskbar strip is still there",
            !ScreenGeometry.CoversMonitor(0, 0, 1920, 1032, 0, 0, 1920, 1080));

        failures += Check(
            "nor does a large but ordinary window",
            !ScreenGeometry.CoversMonitor(100, 80, 1500, 900, 0, 0, 1920, 1080));

        // The multi-monitor case, and the reason the comparison is against the window's own
        // monitor. This window fills the LEFT screen, whose origin is negative -- judged
        // against the primary monitor it would look like nothing at all.
        failures += Check(
            "a window filling a secondary monitor is judged against THAT monitor",
            ScreenGeometry.CoversMonitor(-1920, 0, 0, 1080, -1920, 0, 0, 1080));

        failures += Check(
            "and the same window is not full-screen on the primary one",
            !ScreenGeometry.CoversMonitor(-1920, 0, 0, 1080, 0, 0, 1920, 1080));

        // One edge short is enough to disqualify it: a window covering everything except a
        // strip down one side has left room, and taking the bar out of the topmost band would
        // hide it for no gain.
        failures += Check(
            "falling short on a single edge is enough to disqualify a window",
            !ScreenGeometry.CoversMonitor(0, 0, 1600, 1080, 0, 0, 1920, 1080));

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: the geometry rule fires on full-screen windows and not on ordinary ones."
            : $"{failures} check(s) failed.");

        Console.WriteLine();
        Console.WriteLine("NOT covered here: the notification-state check and the process-name");
        Console.WriteLine("list both need a real foreground window, and the process list is a");
        Console.WriteLine("list precisely because a windowed remote client has no shape to test.");

        return failures == 0 ? 0 : 1;
    }

    private static int Check(string what, bool condition)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }
}
