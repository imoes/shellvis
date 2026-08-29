using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using Shellvis.Core.Markdown;

using Windows.UI.Text;

namespace Shellvis.Shell.Controls;

/// <summary>
/// Draws a parsed answer into a <see cref="RichTextBlock"/>.
///
/// <b>Parsing happens elsewhere.</b> <see cref="MarkdownParser"/> in Core turns the text
/// into a document; this class only decides how each block looks. The split is what made
/// the Markdown testable at all: the parser used to live here, which meant it could not run
/// without a XAML app, so the one piece of this project the user complained about was also
/// the only piece with no harness.
///
/// The one hard rule that survives the split: this is for PROSE only. Tool output goes
/// through unrendered, because an asterisk in a command line and a backtick in a PowerShell
/// string are data, and turning them into italics would corrupt what the console exists to
/// show faithfully.
/// </summary>
internal static class MarkdownRenderer
{
    /// <summary>
    /// Hanging indent for a bullet, in DIP.
    ///
    /// The left margin has to be at LEAST this, or the marker is drawn outside the block and
    /// clipped by whatever padding the container has. Not hypothetical: in the answer window
    /// the markers were invisible for exactly that reason, leaving lines that looked
    /// indented for no stated reason.
    /// </summary>
    private const double Hang = 14;

    /// <summary>
    /// Fill <paramref name="target"/> with <paramref name="markdown"/>.
    ///
    /// Replaces the content rather than appending: the streaming path re-renders the whole
    /// answer on every delta, which is simpler than diffing and cheap at these sizes.
    /// </summary>
    /// <param name="prose">Font for ordinary text.</param>
    /// <param name="mono">Font for code spans and fenced blocks.</param>
    /// <param name="size">Base font size. Headings scale from it.</param>
    public static void Render(
        RichTextBlock target,
        string markdown,
        FontFamily prose,
        FontFamily mono,
        double size,
        Brush foreground,
        Brush muted)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Blocks.Clear();
        target.FontFamily = prose;
        target.FontSize = size;
        target.Foreground = foreground;
        target.TextWrapping = TextWrapping.Wrap;

        MarkdownDocument document = MarkdownParser.Parse(markdown);

        foreach (MarkdownBlock block in document.Blocks)
        {
            switch (block)
            {
                case MarkdownBlock.Heading heading:
                    target.Blocks.Add(RenderHeading(heading, target.Blocks.Count, mono, muted, size));
                    break;

                case MarkdownBlock.Bullet bullet:
                    target.Blocks.Add(RenderBullet(bullet, mono, muted, size));
                    break;

                case MarkdownBlock.Code code:
                    target.Blocks.Add(RenderCode(code, mono, muted, size));
                    break;

                case MarkdownBlock.Paragraph text:
                    target.Blocks.Add(RenderParagraph(text, mono, muted, size));
                    break;
            }
        }

        // A RichTextBlock with no blocks measures zero and the row collapses; an empty
        // paragraph keeps the row's height while a streamed answer is still empty.
        if (target.Blocks.Count == 0)
            target.Blocks.Add(new Paragraph());
    }

    private static Paragraph RenderHeading(
        MarkdownBlock.Heading heading, int position, FontFamily mono, Brush muted, double size)
    {
        var block = new Paragraph
        {
            Margin = new Thickness(0, position > 0 ? 6 : 0, 0, 2),
        };

        // Two sizes only. A console pill is not a document, and six heading levels in a
        // 340px panel would differ by fractions of a pixel.
        AddSpans(block.Inlines, heading.Inlines, mono, muted,
            bold: true, size: heading.Level <= 2 ? size + 2 : size);

        return block;
    }

    private static Paragraph RenderBullet(
        MarkdownBlock.Bullet bullet, FontFamily mono, Brush muted, double size)
    {
        // Hanging indent, so a wrapped bullet lines up under its own text rather than under
        // the marker.
        var block = new Paragraph
        {
            Margin = new Thickness(Hang + (bullet.Depth * Hang), 1, 0, 1),
            TextIndent = -Hang,
        };

        block.Inlines.Add(new Run
        {
            Text = bullet.Marker + "  ",
            Foreground = muted,
        });

        AddSpans(block.Inlines, bullet.Inlines, mono, muted, bold: false, size: size);

        return block;
    }

    private static Paragraph RenderCode(
        MarkdownBlock.Code code, FontFamily mono, Brush muted, double size)
    {
        var block = new Paragraph
        {
            Margin = new Thickness(8, 2, 0, 4),
        };

        block.Inlines.Add(new Run
        {
            // Line breaks intact and NOT wrapped: code that wraps is code that cannot be
            // read back, and the console already scrolls.
            Text = code.Text,
            FontFamily = mono,
            FontSize = size - 1,
            Foreground = muted,
        });

        return block;
    }

    private static Paragraph RenderParagraph(
        MarkdownBlock.Paragraph text, FontFamily mono, Brush muted, double size)
    {
        var block = new Paragraph();
        AddSpans(block.Inlines, text.Inlines, mono, muted, bold: false, size: size);
        return block;
    }

    /// <param name="bold">
    /// The block's base weight, kept apart from the spans' own. Rolled together they would
    /// invert inside a heading: the first pair of asterisks in "## **Title**" would close a
    /// span that was never opened.
    /// </param>
    private static void AddSpans(
        InlineCollection target,
        IReadOnlyList<MarkdownSpan> spans,
        FontFamily mono,
        Brush muted,
        bool bold,
        double size)
    {
        foreach (MarkdownSpan span in spans)
        {
            if (span.Has(SpanStyle.Code))
            {
                target.Add(new Run
                {
                    Text = span.Text,
                    FontFamily = mono,
                    FontSize = size - 1,
                    Foreground = muted,

                    // Upright, always. The block's style is italic for announcements, and
                    // italic monospace is what a slanted terminal font looks like: wrong.
                    FontStyle = FontStyle.Normal,
                    FontWeight = FontWeights.Normal,
                });

                continue;
            }

            var run = new Run
            {
                Text = span.Text,
                FontSize = size,
                FontWeight = bold || span.Has(SpanStyle.Strong)
                    ? FontWeights.SemiBold
                    : FontWeights.Normal,
                FontStyle = span.Has(SpanStyle.Emphasis) ? FontStyle.Italic : FontStyle.Normal,
            };

            if (span.Has(SpanStyle.Strike))
                run.TextDecorations = TextDecorations.Strikethrough;

            target.Add(run);
        }
    }
}
