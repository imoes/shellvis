using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace Shellvis.Core.Desktop;

/// <summary>
/// Where there is actually room on the taskbar.
///
/// <b>Why this is measured rather than assumed.</b> The docked bar used to be placed by
/// arithmetic: so many pixels in from the right, never left of centre. That works on an
/// empty taskbar and fails on a working one. Windows 11 centres the app buttons, so the
/// cluster grows outward from the middle in both directions as windows are opened -- and
/// with a dozen of them open it reaches under a bar parked at a fixed offset. The report
/// that produced this file was exactly that: "Shellvis covers taskbar icons, and there is
/// still space left of the Start button."
///
/// There is no API for "the empty part of the taskbar". What there is, is UI Automation:
/// Explorer publishes the taskbar's buttons as elements with real screen rectangles. Union
/// them, and the gaps between them are the free space -- measured on the taskbar the user is
/// actually looking at, at whatever DPI and alignment and icon count they have.
///
/// The far-left stretch is preferred over the one before the tray, and deliberately: on a
/// centred taskbar it is the only region whose width does not change when a window is
/// opened. A bar placed there stays put; one placed to the right of the cluster is correct
/// until the next application starts.
/// </summary>
public static class TaskbarLayout
{
    /// <summary>A run of free pixels on the strip, in physical screen coordinates.</summary>
    public readonly record struct Span(int Left, int Right)
    {
        public int Width => Right - Left;
    }

    /// <summary>
    /// Elements wider than this are containers, not buttons.
    ///
    /// The taskbar's own tree carries wrappers that span the whole strip; treating one as
    /// occupied space would mean there is never any room anywhere. A taskbar button is
    /// around 48 physical pixels and the tray is a few hundred, so the cut is generous.
    /// </summary>
    private const int WidestElement = 460;

    /// <summary>Air left between the bar and whatever it sits beside.</summary>
    private const int Margin = 8;

    /// <summary>
    /// Somewhere on the strip that is free and at least <paramref name="needed"/> wide, or
    /// null when the taskbar cannot be read.
    ///
    /// Null rather than a guess: the caller has a fallback that has been on screen for
    /// months, and replacing a known-mediocre position with an invented one is not an
    /// improvement.
    /// </summary>
    public static Span? FindFreeSpan(int stripTop, int stripBottom, int stripLeft, int stripRight, int needed)
    {
        List<Span> occupied;

        try
        {
            occupied = Used(stripTop, stripBottom, stripLeft, stripRight);
        }
        catch (Exception)
        {
            // Deliberately broad. This reaches into another process's UI tree through COM:
            // Explorer restarting mid-call, a UIA timeout and a shell update that renames a
            // class all surface differently, and none of them is a reason to fail to place a
            // window. The fallback placement is right there.
            return null;
        }

        if (occupied.Count == 0)
            return null;

        // The gaps between what is occupied, in order.
        var gaps = new List<Span>();
        int cursor = stripLeft;

        foreach (Span used in occupied)
        {
            if (used.Left - cursor >= needed + (2 * Margin))
                gaps.Add(new Span(cursor, used.Left));

            cursor = Math.Max(cursor, used.Right);
        }

        if (stripRight - cursor >= needed + (2 * Margin))
            gaps.Add(new Span(cursor, stripRight));

        if (gaps.Count == 0)
            return null;

        // The first gap is the one before the leftmost element -- the stretch beside Start on
        // a centred taskbar. Taken when it fits, for the reason in the class comment: it is
        // the only free space that does not move when a window opens. Otherwise the widest.
        return gaps[0].Left == stripLeft
            ? gaps[0]
            : gaps.OrderByDescending(g => g.Width).First();
    }

    /// <summary>
    /// What is already using pixels on this strip, as disjoint runs.
    ///
    /// Public so the harness can assert the strip was actually READ. That is the check that
    /// was missing: a taskbar always has at least a Start button, so an empty result means
    /// the measurement failed rather than that the strip is free -- and treating it as free
    /// is precisely how the bar came to sit on somebody's icons.
    /// </summary>
    public static List<Span> Used(int stripTop, int stripBottom, int stripLeft, int stripRight)
    {
        var spans = new List<Span>();

        // EVERY taskbar, not just the first one.
        //
        // This is the defect that produced the second report of covered icons. A second
        // monitor gets its own taskbar in a window of class Shell_SecondaryTrayWnd, with its
        // own Start button and its own row of icons, and FindWindow("Shell_TrayWnd") never
        // sees any of it. So on that screen the strip measured as completely empty, the bar
        // was placed where it liked, and it landed 28 pixels over the rightmost icon --
        // measured on this desk: bar at x -613, the icon it covered at -629 to -585.
        //
        // The rectangles are clipped to the strip being asked about a few lines below, so
        // taking the buttons of all of them and letting the clip sort it out is both simpler
        // and safer than deciding here which taskbar belongs to which display.
        List<HWND> taskbars = Taskbars();

        if (taskbars.Count == 0)
            return spans;

        using var automation = new UIA3Automation();

        // HWND is a wrapper struct; FromHandle takes the nint inside it, so the conversion
        // is spelled out rather than passed as a method group.
        foreach (AutomationElement element in taskbars
            .SelectMany(bar => automation.FromHandle(bar).FindAllDescendants()))
        {
            System.Drawing.Rectangle box;

            try
            {
                box = element.BoundingRectangle;
            }
            catch (Exception)
            {
                // An element that vanished between the enumeration and the read. Common on a
                // live taskbar and not worth abandoning the measurement for.
                continue;
            }

            if (box.Width <= 0 || box.Height <= 0 || box.Width > WidestElement)
                continue;

            // Vertically on the strip. An element from a flyout or a preview window can be
            // in the tree and nowhere near the taskbar.
            if (box.Bottom <= stripTop || box.Top >= stripBottom)
                continue;

            int left = Math.Max(box.Left, stripLeft);
            int right = Math.Min(box.Right, stripRight);

            if (right > left)
                spans.Add(new Span(left, right));
        }

        return Merge(spans);
    }

    /// <summary>
    /// Every taskbar window on the desktop: the primary one and one per further monitor.
    /// </summary>
    /// <remarks>
    /// The secondary bars are found by walking <c>FindWindowEx</c> over the class rather
    /// than by enumerating all top-level windows and reading their class names. Same result,
    /// and it asks the shell for exactly the thing wanted instead of filtering everything
    /// the desktop happens to contain.
    /// </remarks>
    private static List<HWND> Taskbars()
    {
        var found = new List<HWND>();

        HWND primary = PInvoke.FindWindow("Shell_TrayWnd", null);

        if (!primary.IsNull)
            found.Add(primary);

        HWND after = HWND.Null;

        // Bounded: one iteration per monitor in the worst case, and a guard because a loop
        // driven by another process's window list should not be able to spin for ever if
        // that process behaves oddly during a restart.
        for (int guard = 0; guard < 16; guard++)
        {
            HWND next = PInvoke.FindWindowEx(HWND.Null, after, "Shell_SecondaryTrayWnd", null);

            if (next.IsNull)
                break;

            found.Add(next);
            after = next;
        }

        return found;
    }

    /// <summary>Overlapping rectangles into disjoint runs, left to right.</summary>
    private static List<Span> Merge(List<Span> spans)
    {
        var merged = new List<Span>();

        foreach (Span span in spans.OrderBy(s => s.Left))
        {
            if (merged.Count > 0 && span.Left <= merged[^1].Right)
            {
                merged[^1] = new Span(merged[^1].Left, Math.Max(merged[^1].Right, span.Right));
                continue;
            }

            merged.Add(span);
        }

        return merged;
    }
}
