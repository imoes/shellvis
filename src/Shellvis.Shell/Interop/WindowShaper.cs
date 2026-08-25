using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;

namespace Shellvis.Shell.Interop;

/// <summary>A rounded rectangle in logical DIPs, relative to the window's client area.</summary>
internal readonly record struct RoundedRect(double X, double Y, double Width, double Height, double Radius);

/// <summary>
/// The non-client frame of a window, in physical pixels.
///
/// This matters more than it looks. A WinUI window with SetBorderAndTitleBar(false, false)
/// still keeps WS_DLGFRAME, so Windows reserves a three-pixel frame on every side: on this
/// machine a 460x412 window has a 454x406 client area starting three pixels in. XAML draws
/// in client coordinates while SetWindowRgn clips in WINDOW coordinates, so a region built
/// straight from measured element bounds sits three pixels up and to the left of the content
/// it is meant to trace -- and the content, being sized for the full window, overhangs the
/// client area by six pixels and loses its right and bottom edges to the clip. That was
/// visible as a border that looked shifted down and to the right with its bottom stroke
/// missing entirely.
/// </summary>
/// <param name="OffsetX">Where the client area begins inside the window rectangle.</param>
/// <param name="OffsetY">Likewise, vertically.</param>
/// <param name="ExtraWidth">How much wider the window is than its client area.</param>
/// <param name="ExtraHeight">Likewise, taller.</param>
internal readonly record struct WindowFrame(int OffsetX, int OffsetY, int ExtraWidth, int ExtraHeight);

/// <summary>
/// Clips a WinUI window to one or more rounded shapes.
///
/// Why this exists: WinUI has no equivalent of WPF's AllowsTransparency, so a
/// borderless window still composites as a rectangle. SystemBackdropElement draws
/// the acrylic pill with a real CornerRadius, but the window behind it would still
/// show square corners around it. SetWindowRgn is the reliable fix -- pixels outside
/// the region are never composited, so the corners are genuinely cut away rather
/// than painted over.
///
/// Multiple shapes are supported because the collapsed pill and the expanded console
/// are two separate floating surfaces with a gap between them. A single rounded rect
/// cannot express that, so the region is a union.
///
/// Trade-off, and the reason this is a spike: a GDI region has hard edges, so the
/// curve is aliased. DWM's own rounding is anti-aliased but fixed at roughly 8px --
/// far too tight for a 32px pill -- so it cannot replace this.
/// </summary>
internal sealed class WindowShaper : IDisposable
{
    private readonly HWND _hwnd;
    private HRGN _current;

    public WindowShaper(nint windowHandle) => _hwnd = new HWND(windowHandle);

    /// <summary>Physical pixels per logical DIP for the monitor this window is on.</summary>
    public double Scale
    {
        get
        {
            uint dpi = PInvoke.GetDpiForWindow(_hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
    }

    /// <summary>
    /// Measure the non-client frame. Queried every time rather than cached: it changes
    /// with the monitor's scale factor, and a stale value would put the clip back out of
    /// step with the content, which is the exact defect this measurement exists to fix.
    /// </summary>
    public WindowFrame Frame()
    {
        if (!PInvoke.GetWindowRect(_hwnd, out RECT window))
            return default;

        if (!PInvoke.GetClientRect(_hwnd, out RECT client))
            return default;

        // GetClientRect gives a size with a zero origin, so the client's position has to
        // be asked for separately.
        var origin = default(System.Drawing.Point);
        if (!PInvoke.ClientToScreen(_hwnd, ref origin))
            return default;

        return new WindowFrame(
            origin.X - window.left,
            origin.Y - window.top,
            (window.right - window.left) - client.right,
            (window.bottom - window.top) - client.bottom);
    }

    /// <summary>
    /// Clip the window to the union of <paramref name="shapes"/>. Coordinates are
    /// logical DIPs; the region must be built in physical pixels, which is why this
    /// has to be redone whenever the window changes size or moves to a monitor with
    /// a different scale factor.
    /// </summary>
    public void Apply(IReadOnlyList<RoundedRect> shapes)
    {
        if (shapes.Count == 0)
            return;

        double scale = Scale;
        WindowFrame frame = Frame();
        HRGN combined = HRGN.Null;

        foreach (var shape in shapes)
        {
            HRGN part = Build(shape, scale, frame);
            if (part.IsNull)
                continue;

            if (combined.IsNull)
            {
                combined = part;
                continue;
            }

            // CombineRgn needs a distinct destination; it cannot write into one of
            // its own sources, so allocate an empty region to receive the union.
            HRGN merged = PInvoke.CreateRectRgn(0, 0, 1, 1);
            PInvoke.CombineRgn(merged, combined, part, RGN_COMBINE_MODE.RGN_OR);
            PInvoke.DeleteObject((HGDIOBJ)combined);
            PInvoke.DeleteObject((HGDIOBJ)part);
            combined = merged;
        }

        if (combined.IsNull)
            return;

        // SetWindowRgn takes ownership on success, so the previous region is only
        // ours to free after the swap succeeds.
        if (PInvoke.SetWindowRgn(_hwnd, combined, bRedraw: true) == 0)
        {
            PInvoke.DeleteObject((HGDIOBJ)combined);
            return;
        }

        if (!_current.IsNull)
            PInvoke.DeleteObject((HGDIOBJ)_current);

        _current = combined;
    }

    private static HRGN Build(RoundedRect r, double scale, WindowFrame frame)
    {
        // The shape arrives in client DIPs; the region has to be in physical window
        // pixels, so scale first and then shift by where the client area starts.
        int left = (int)Math.Round(r.X * scale) + frame.OffsetX;
        int top = (int)Math.Round(r.Y * scale) + frame.OffsetY;

        // CreateRoundRectRgn's right and bottom are exclusive, so X+Width would be the
        // arithmetically correct value -- but a ROUNDED region comes out one pixel short
        // of it on both edges, which GetRgnBox confirms: a region asked for 3..463 reports
        // 3..462, and the last column of painted content is clipped away. On the pill that
        // removed the outer pixel of the gradient ring along the right side and cut its
        // bottom stroke entirely, which read as a border that had slipped out of place.
        //
        // An earlier revision also added one pixel here and was reverted as wrong. It was
        // wrong then for a different reason: the region was still being built in client
        // coordinates while the window clips in window coordinates, so it sat three pixels
        // off and the extra pixel only widened the mismatch. With the frame accounted for,
        // the compensation lands where it belongs.
        int right = (int)Math.Round((r.X + r.Width) * scale) + frame.OffsetX + 1;
        int bottom = (int)Math.Round((r.Y + r.Height) * scale) + frame.OffsetY + 1;

        if (right <= left || bottom <= top)
            return HRGN.Null;

        // GDI expresses corner rounding as the full width/height of the ellipse, not
        // as the radius -- hence the doubling. Getting this wrong yields corners that
        // look half as round as intended.
        int ellipse = (int)Math.Round(r.Radius * 2 * scale);

        // An ellipse larger than the rectangle produces an empty region, which would
        // make that shape vanish entirely.
        ellipse = Math.Clamp(ellipse, 0, Math.Min(right - left, bottom - top));

        return PInvoke.CreateRoundRectRgn(left, top, right, bottom, ellipse, ellipse);
    }

    /// <summary>
    /// Ask DWM for rounded corners and an extended frame. Best-effort: both are
    /// cosmetic refinements on top of the region clip, so failures are ignored.
    /// </summary>
    public unsafe void TrySoftenEdges()
    {
        // DONOTROUND, not ROUND. Asking DWM to round the corners makes it draw its own
        // border on the WINDOW rectangle -- which the clipping region does not affect,
        // because DWM draws it outside the region logic. The result is a faint outline
        // tracing the full 460x412 window, including the area the collapsed console
        // occupies. The region already does the rounding, so DWM must not also try.
        var pref = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        PInvoke.DwmSetWindowAttribute(
            _hwnd,
            DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
            &pref,
            (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));

        // And no border colour at all. DWMWA_COLOR_NONE is the documented way to say
        // "draw no border", which is stronger than picking a transparent colour.
        uint none = 0xFFFFFFFE; // DWMWA_COLOR_NONE
        PInvoke.DwmSetWindowAttribute(
            _hwnd,
            DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR,
            &none,
            sizeof(uint));

        // Negative margins extend the glass frame across the whole client area,
        // which is what lets DWM composite the window with per-pixel alpha.
        var margins = new MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1,
        };
        PInvoke.DwmExtendFrameIntoClientArea(_hwnd, &margins);
    }

    public void BringToFront() => PInvoke.SetForegroundWindow(_hwnd);

    public void Dispose()
    {
        if (_current.IsNull)
            return;

        // Detach before deleting: freeing a region the window still references would
        // leave it clipped to freed memory.
        PInvoke.SetWindowRgn(_hwnd, HRGN.Null, bRedraw: false);
        PInvoke.DeleteObject((HGDIOBJ)_current);
        _current = HRGN.Null;
    }
}
