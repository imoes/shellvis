using System.Drawing;
using System.Drawing.Imaging;

namespace Shellvis.Core.Desktop;

/// <summary>A captured image on disk, plus what it depicts.</summary>
/// <param name="Path">Absolute path to the PNG.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Detail">What was captured, in plain words.</param>
public sealed record CaptureResult(string Path, int Width, int Height, string Detail)
{
    public override string ToString() => $"{Detail} -> {Path} ({Width}x{Height})";
}

/// <summary>
/// Captures the screen or a single window to a PNG.
///
/// This complements the UI Automation tree rather than replacing it. The tree is the
/// better channel by far for deciding what to click: it is compact, it carries names
/// and supported actions, and it survives being described in text. A screenshot is
/// what you reach for when the tree cannot answer the question -- custom-drawn
/// surfaces that expose no automation elements, charts, rendered documents, or a
/// "what does this actually look like" check after an action.
///
/// Images are written to disk rather than returned as base64. A screenshot of a 4K
/// desktop is several megabytes; inlining that into a tool result would blow the
/// context budget in one call. The agent gets a path and attaches it deliberately.
/// </summary>
public static class ScreenCapture
{
    /// <summary>Where captures land. One directory so cleanup is a single operation.</summary>
    public static string OutputDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "shellvis", "captures");

    /// <summary>Capture one window by handle, cropped to its bounds.</summary>
    public static CaptureResult CaptureWindow(nint windowHandle)
    {
        WindowInfo window = WindowInspector.Describe(windowHandle)
            ?? throw new InvalidOperationException(
                $"Window {windowHandle} is not visible or no longer exists.");

        // Screen-region capture rather than PrintWindow: PrintWindow asks the window
        // to redraw itself into a bitmap, which misses anything composited by DWM
        // (acrylic, transparency, hardware-accelerated video) and returns black for
        // some GPU-rendered apps. Capturing the region shows what the user sees.
        return CaptureRegion(
            window.Left, window.Top, window.Width, window.Height,
            $"window \"{window.Title}\" ({window.ProcessName})");
    }

    /// <summary>Capture the window the user is currently working in.</summary>
    public static CaptureResult CaptureForegroundWindow()
    {
        WindowInfo window = WindowInspector.Foreground()
            ?? throw new InvalidOperationException("No window currently has focus.");

        return CaptureWindow(window.Handle);
    }

    /// <summary>
    /// Capture the entire virtual desktop, spanning every monitor.
    ///
    /// The virtual screen origin can be negative when a secondary monitor sits left
    /// of or above the primary one, which is why the bounds cannot be assumed to
    /// start at 0,0.
    /// </summary>
    public static CaptureResult CaptureAllScreens()
    {
        Rectangle bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        return CaptureRegion(
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            $"all monitors ({bounds.Width}x{bounds.Height} from {bounds.X},{bounds.Y})");
    }

    /// <summary>Capture an arbitrary screen rectangle, in physical pixels.</summary>
    public static CaptureResult CaptureRegion(int left, int top, int width, int height, string? detail = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Capture region must have a positive size.");

        Directory.CreateDirectory(OutputDirectory);

        string path = Path.Combine(
            OutputDirectory,
            $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        bitmap.Save(path, ImageFormat.Png);

        return new CaptureResult(path, width, height, detail ?? $"region {width}x{height} at {left},{top}");
    }

    /// <summary>
    /// Delete previous captures. Worth calling between sessions: an agent that
    /// screenshots liberally fills a temp directory with megabytes nobody will read.
    /// </summary>
    public static int ClearCaptures()
    {
        if (!Directory.Exists(OutputDirectory))
            return 0;

        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(OutputDirectory, "capture-*.png"))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
                // Still open in a viewer. Not worth failing the cleanup over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}
