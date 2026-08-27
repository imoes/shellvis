#:package System.Drawing.Common@9.0.0

// Draws the Shellvis application icon and assembles a multi-size .ico.
//
// THE MARK. Elvis's own device: the jagged bolt from the TCB logo. A pompadour-and-
// sunglasses silhouette was the obvious first idea and was tried first -- it is the wrong
// one. At 16 pixels, which is exactly the size the notification area asks for, a face loses
// its features and becomes an anonymous blob, and a spline smooth enough to draw hair also
// smooths the hair away. A bolt survives being small, and behind it sits a pompadour swoosh
// tone-on-tone: legible at 128 and 256, invisible and harmless at 16.
//
// The plate uses the pill's own gradient (#7C6BF5 -> #4A9BF5 -> #D46BC8) so the icon and the
// window it launches look like the same product.
//
// THE FORMAT. Every entry is an uncompressed DIB, not a PNG. PNG entries were the first
// attempt because they are a fraction of the code, and the file looked perfect -- in a PNG
// viewer. Read back through the Windows icon APIs it was pure noise at every size: PNG inside
// .ico is understood by parts of the shell but not by LoadImage, which is what the
// notification area code here calls. The verification that caught it renders the finished
// file back, rather than the bitmap it was drawn from.
//
// Written in C# rather than PowerShell after the PowerShell version silently produced a
// 0.1 KB file: [Math]::Floor returns a double, New-Object byte[] wants an int, and the
// resulting exception left an empty artefact instead of an error.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

int[] sizes = [16, 20, 24, 32, 48, 64, 128, 256];

string outPath = args.Length > 0 ? args[0] : "AppIcon.ico";
string previewPath = args.Length > 1 ? args[1] : "AppIcon-preview.png";
string stripPath = args.Length > 2 ? args[2] : "AppIcon-sizes.png";

var entries = new List<(int Size, byte[] Bytes)>();

foreach (int size in sizes)
{
    using Bitmap bitmap = Draw(size);
    entries.Add((size, DibEntry(bitmap)));

    if (size == 256)
        bitmap.Save(previewPath, ImageFormat.Png);
}

WriteIco(outPath, entries);
Console.WriteLine($"wrote {outPath}: {entries.Count} sizes, {new FileInfo(outPath).Length / 1024} KB");

// The check that matters: read the FILE back through the icon API and render what it gives.
RenderStrip(outPath, stripPath);
Console.WriteLine($"read back and rendered {stripPath}");

static Bitmap Draw(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

    using Graphics g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Color.Transparent);

    float inset = Math.Max(0.5f, size * 0.02f);
    float side = size - (2 * inset);

    PointF At(double x, double y) =>
        new((float)(inset + (side * x)), (float)(inset + (side * y)));

    // 24% radius: a rounded plate in the shape of a Windows 11 app tile. The pill's own
    // stadium shape reads as a blob once it is 16 pixels wide.
    float d = side * 0.24f * 2;

    using var plate = new GraphicsPath();
    plate.AddArc(inset, inset, d, d, 180, 90);
    plate.AddArc(inset + side - d, inset, d, d, 270, 90);
    plate.AddArc(inset + side - d, inset + side - d, d, d, 0, 90);
    plate.AddArc(inset, inset + side - d, d, d, 90, 90);
    plate.CloseFigure();

    using var fill = new LinearGradientBrush(
        new PointF(inset, inset),
        new PointF(inset + side, inset + side),
        Color.FromArgb(255, 124, 107, 245),
        Color.FromArgb(255, 212, 107, 200))
    {
        InterpolationColors = new ColorBlend(4)
        {
            Colors =
            [
                Color.FromArgb(255, 124, 107, 245),
                Color.FromArgb(255, 74, 155, 245),
                Color.FromArgb(255, 212, 107, 200),
                Color.FromArgb(255, 124, 107, 245),
            ],
            Positions = [0f, 0.35f, 0.7f, 1f],
        },
    };

    g.FillPath(fill, plate);

    // The detail work only happens where there are pixels to carry it. Below 32 a highlight
    // and a swoosh just muddy the plate and cost the bolt its contrast.
    if (size >= 32)
    {
        GraphicsState clip = g.Save();
        g.SetClip(plate);

        using var gloss = new LinearGradientBrush(
            new PointF(inset, inset),
            new PointF(inset, inset + (side * 0.55f)),
            Color.FromArgb(56, 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255));

        g.FillRectangle(gloss, inset, inset, side, side * 0.55f);

        // The pompadour: one sweeping wedge, brighter than the plate by a hair.
        using var quiff = new GraphicsPath();
        quiff.AddBezier(At(0.02, 0.78), At(-0.02, 0.22), At(0.50, -0.08), At(0.88, 0.18));
        quiff.AddBezier(At(0.88, 0.18), At(0.54, 0.06), At(0.18, 0.32), At(0.22, 0.80));
        quiff.CloseFigure();

        using var tone = new SolidBrush(Color.FromArgb(48, 255, 255, 255));
        g.FillPath(tone, quiff);

        g.Restore(clip);
    }

    // The bolt, leaning. The TCB bolt leans; a symmetrical one reads as a weather warning.
    PointF[] bolt =
    [
        At(0.605, 0.115), At(0.255, 0.560), At(0.450, 0.560),
        At(0.375, 0.905), At(0.745, 0.435), At(0.545, 0.435), At(0.635, 0.115),
    ];

    if (size >= 48)
    {
        // A shadow a pixel down, which is what stops the white bolt dissolving into the
        // light middle of the gradient.
        using var shadow = new SolidBrush(Color.FromArgb(64, 24, 14, 56));
        GraphicsState shifted = g.Save();
        g.TranslateTransform(0, size * 0.018f);
        g.FillPolygon(shadow, bolt);
        g.Restore(shifted);
    }

    g.FillPolygon(Brushes.White, bolt);

    return bitmap;
}

/// <summary>One icon directory entry as an uncompressed 32-bit DIB with its AND mask.</summary>
static byte[] DibEntry(Bitmap bitmap)
{
    int w = bitmap.Width;
    int h = bitmap.Height;

    BitmapData data = bitmap.LockBits(
        new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

    byte[] pixels = new byte[data.Stride * h];
    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
    int stride = data.Stride;
    bitmap.UnlockBits(data);

    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);

    // BITMAPINFOHEADER. The height is DOUBLED: an icon DIB stacks the colour bitmap and the
    // AND mask, and the header describes both together.
    writer.Write(40u);
    writer.Write(w);
    writer.Write(h * 2);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0u);
    writer.Write((uint)(w * h * 4));
    writer.Write(0);
    writer.Write(0);
    writer.Write(0u);
    writer.Write(0u);

    // Bottom-up, as a DIB is stored. Written top-down the icon comes out vertically
    // mirrored, which looks like a drawing mistake rather than a format one.
    for (int y = h - 1; y >= 0; y--)
        writer.Write(pixels, y * stride, w * 4);

    // The AND mask, all zeros: transparency comes from the alpha channel. It still has to be
    // present and correctly sized, with rows padded to four bytes like any DIB.
    int maskStride = (w + 31) / 32 * 4;
    writer.Write(new byte[maskStride * h]);

    writer.Flush();
    return stream.ToArray();
}

static void WriteIco(string path, List<(int Size, byte[] Bytes)> entries)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)entries.Count);

    int offset = 6 + (16 * entries.Count);

    foreach ((int size, byte[] bytes) in entries)
    {
        // 256 is written as 0: the field is a single byte, and 256 does not fit in it.
        byte dim = (byte)(size >= 256 ? 0 : size);

        writer.Write(dim);
        writer.Write(dim);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)bytes.Length);
        writer.Write((uint)offset);

        offset += bytes.Length;
    }

    foreach ((_, byte[] bytes) in entries)
        writer.Write(bytes);
}

/// <summary>
/// Render the small sizes out of the finished file, at actual size and magnified.
///
/// Judging an icon from its 256px artwork is how a 16px icon ends up unreadable, and reading
/// the file back is the only check that can catch a format the icon APIs reject.
/// </summary>
static void RenderStrip(string ico, string outPath)
{
    int[] shown = [16, 20, 24, 32, 48];

    using var strip = new Bitmap(560, 176);
    using Graphics g = Graphics.FromImage(strip);

    g.Clear(Color.FromArgb(255, 244, 244, 248));
    g.InterpolationMode = InterpolationMode.NearestNeighbor;
    g.PixelOffsetMode = PixelOffsetMode.Half;

    using var font = new Font("Segoe UI", 9);
    int x = 16;

    foreach (int size in shown)
    {
        using var icon = new Icon(ico, size, size);
        using Bitmap bitmap = icon.ToBitmap();

        g.DrawImage(bitmap, x, 24, size, size);
        g.DrawImage(bitmap, x, 76, 72, 72);
        g.DrawString($"{size} px", font, Brushes.DimGray, x, 154);

        x += 90;
    }

    strip.Save(outPath, ImageFormat.Png);
}
