namespace Shellvis.Core.Desktop;

/// <summary>
/// Geometry questions about windows and screens, with no window handles involved.
///
/// Here rather than in the shell so it can be checked without a desktop: the interesting part
/// of "has this window taken over the screen" is the arithmetic, and the arithmetic is exactly
/// where an off-by-one hides. A test that needs a real full-screen window on a real monitor
/// tests the compositor's mood as much as the rule.
/// </summary>
public static class ScreenGeometry
{
    /// <summary>
    /// How far short of the monitor edge a window may fall and still count as covering it.
    ///
    /// A genuinely full-screen window is occasionally reported a pixel or two off in either
    /// direction -- some report themselves slightly larger than the monitor, some land a pixel
    /// inside it. Requiring an exact match makes the rule fire almost never, which is worse
    /// than firing a shade too eagerly: the consequence of a false positive is a floating bar
    /// that is not on top for a moment, and of a false negative that it covers someone's
    /// remote session.
    /// </summary>
    public const int EdgeTolerance = 2;

    /// <summary>
    /// Whether a window covers the whole of the monitor it is on.
    /// </summary>
    /// <remarks>
    /// Compared against the window's OWN monitor, not the primary display. On a multi-monitor
    /// machine a maximised window on the left screen covers nothing on the middle one, and a
    /// bar on the middle screen has no reason to step aside for it.
    /// </remarks>
    public static bool CoversMonitor(
        int left, int top, int right, int bottom,
        int screenLeft, int screenTop, int screenRight, int screenBottom) =>
        left <= screenLeft + EdgeTolerance
        && top <= screenTop + EdgeTolerance
        && right >= screenRight - EdgeTolerance
        && bottom >= screenBottom - EdgeTolerance;
}
