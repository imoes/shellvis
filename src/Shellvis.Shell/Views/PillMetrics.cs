namespace Shellvis.Shell.Views;

/// <summary>
/// The pill's geometry, in logical DIPs.
///
/// These live in one place because two independent consumers must agree exactly:
/// the XAML layout and the Win32 clipping region. If they drift by even a pixel the
/// window shows a sliver of unclipped rectangle, which is the exact artifact this
/// whole design exists to avoid.
/// </summary>
internal static class PillMetrics
{
    /// <summary>
    /// Width of both surfaces. Constant, because the pill never changes width.
    ///
    /// Raised from 460, which was the reference image's width and turned out to be the
    /// wrong thing to copy. The six buttons and their spacing take 259 DIP of it, so the
    /// input field was left with 171 -- about twenty characters, in a bar whose whole
    /// purpose is typing instructions into. The console inherits the same width and was
    /// truncating almost every line it showed.
    ///
    /// 720 is arrived at rather than chosen: it is what makes the input field as wide as
    /// it WOULD have been at 460 with no buttons at all (430 DIP), with the buttons still
    /// there. 430 + 30 padding + 36 spacing + 223 buttons = 719.
    /// </summary>
    public const double Width = 720;

    /// <summary>Collapsed height, and the basis for the corner radius.</summary>
    public const double PillHeight = 64;

    /// <summary>Half of <see cref="PillHeight"/>, giving a fully rounded end cap.</summary>
    public const double PillRadius = PillHeight / 2;

    /// <summary>Transparent gap so pill and console read as two floating surfaces.</summary>
    public const double Gap = 8;

    /// <summary>
    /// Maximum console height. Chosen to keep the window near the reference image's
    /// proportions while still showing a useful transcript; content scrolls inside
    /// rather than growing the window further.
    /// </summary>
    public const double ConsoleHeight = 340;

    public const double ConsoleRadius = 18;

    /// <summary>Full window height. The window is always this tall, see PillWindow.</summary>
    public const double TotalHeight = PillHeight + Gap + ConsoleHeight;

    /// <summary>Distance from the bottom of the work area when first shown.</summary>
    public const double BottomInset = 48;

    /// <summary>
    /// Height of the docked bar.
    ///
    /// 34 rather than the pill's 64: a Windows 11 taskbar is 48 logical pixels, so the
    /// bar has to fit inside it with a little air above and below or it looks like it is
    /// sitting on top of the taskbar rather than in it.
    /// </summary>
    public const double DockedHeight = 34;

    /// <summary>
    /// Size of a button on the docked bar.
    ///
    /// 26 rather than the pill's 36: the bar is 34 tall, so a 36px button would not fit
    /// inside it at all. Matches the microphone, which is already shrunk to the same size.
    /// </summary>
    public const double DockedButton = 26;

    /// <summary>
    /// Width of the docked bar.
    ///
    /// Narrower than the pill, because the docked mode drops three of the six buttons and a
    /// pill-width bar next to the taskbar's own search field looks like a mistake.
    ///
    /// The console toggle's footprint is ADDED rather than taken out of the field: the bar
    /// grew by exactly one button when the console toggle was put back on it, so the input
    /// field is the same size docked as it was before. Squeezing the field instead would
    /// have been invisible in the code and obvious in use -- the field is the reason the
    /// docked bar exists.
    /// </summary>
    public const double DockedWidth = 320 + DockedButton + 2;

    /// <summary>Half of <see cref="DockedHeight"/>, so the docked bar is a stadium too.</summary>
    public const double DockedRadius = DockedHeight / 2;

    /// <summary>
    /// Window height while docked.
    ///
    /// It has to shrink with the bar. The window is always exactly as tall as its
    /// content, and leaving it at <see cref="TotalHeight"/> with a 34px bar left 30px of
    /// empty window BELOW the bar -- which pushed the visible bar that far above the
    /// taskbar it was supposed to sit in.
    /// </summary>
    public const double DockedTotalHeight = DockedHeight + Gap + ConsoleHeight;

    /// <summary>Expand duration: long enough to read as motion, short enough not to drag.</summary>
    public static readonly TimeSpan ToggleDuration = TimeSpan.FromMilliseconds(190);
}
