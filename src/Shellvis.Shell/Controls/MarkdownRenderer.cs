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
    /// <param name="onLink">
    /// What to do when a link is clicked. Passed in rather than handled here, because a
    /// <c>shellvis:</c> target is an action inside this application and only the shell knows
    /// how to carry it out. Without a handler, links are still drawn and simply do nothing,
    /// which beats drawing them as bare text.
    /// </param>
    public static void Render(
        RichTextBlock target,
        string markdown,
        FontFamily prose,
        FontFamily mono,
        double size,
        Brush foreground,
        Brush muted,
        Action<string>? onLink = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Blocks.Clear();
        target.FontFamily = prose;
        target.FontSize = size;
        target.Foreground = foreground;
        target.TextWrapping = TextWrapping.Wrap;

        var palette = new Palette(prose, mono, foreground, muted, size, onLink);

        // How wide the flow actually is, so wide content can be capped to it rather than
        // clipped by it. ActualWidth is zero before the first layout pass -- a streamed answer
        // renders repeatedly, so the second pass has it -- and the fallback is deliberately
        // narrow: a table that scrolls when it did not need to is a smaller fault than one
        // whose right-hand columns are missing.
        double available = target.ActualWidth > 40 ? target.ActualWidth - 8 : 480;

        MarkdownDocument document = MarkdownParser.Parse(markdown);

        foreach (MarkdownBlock block in document.Blocks)
        {
            switch (block)
            {
                case MarkdownBlock.Heading heading:
                    target.Blocks.Add(RenderHeading(heading, target.Blocks.Count, palette));
                    break;

                case MarkdownBlock.Bullet bullet:
                    target.Blocks.Add(RenderBullet(bullet, palette));
                    break;

                case MarkdownBlock.Code code:
                    target.Blocks.Add(RenderCode(code, palette, available));
                    break;

                case MarkdownBlock.Table table:
                    target.Blocks.Add(RenderTable(table, palette, target));
                    break;

                case MarkdownBlock.Rule:
                    target.Blocks.Add(RenderRule(palette));
                    break;

                case MarkdownBlock.Paragraph text:
                    target.Blocks.Add(RenderParagraph(text, palette));
                    break;
            }
        }

        // A RichTextBlock with no blocks measures zero and the row collapses; an empty
        // paragraph keeps the row's height while a streamed answer is still empty.
        if (target.Blocks.Count == 0)
            target.Blocks.Add(new Paragraph());

        // Give the tables the width, now and again after every layout.
        //
        // Re-subscribed rather than subscribed once, because Render is called for every
        // streamed delta and the handlers would otherwise stack up by the hundred.
        target.SizeChanged -= OnSizeChanged;
        target.SizeChanged += OnSizeChanged;

        StretchTables(target);
    }

    /// <summary>Marks the grids that <see cref="StretchTables"/> owns.</summary>
    private const string TableTag = "markdown-table";

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is RichTextBlock block)
            StretchTables(block);
    }

    /// <summary>
    /// Make every table in <paramref name="target"/> exactly as wide as the flow.
    ///
    /// <b>Why a handler and not a binding.</b> A table needs a definite width: an
    /// InlineUIContainer measures its child unbounded, and with no width of its own a table
    /// comes out as wide as its cells happen to be -- which is how a list of tickets ended up
    /// occupying two thirds of the window with the rest blank. Reading ActualWidth while
    /// rendering gives a value one layout pass old, and binding to ActualWidth does not help
    /// either: it is a dependency property that never raises a change, so the binding reads
    /// once and then goes quiet. SizeChanged is the notification that actually exists.
    /// </summary>
    private static void StretchTables(RichTextBlock target)
    {
        if (target.ActualWidth <= 40)
            return;

        foreach (Block block in target.Blocks)
        {
            if (block is not Paragraph paragraph)
                continue;

            foreach (Inline inline in paragraph.Inlines)
            {
                if (inline is InlineUIContainer { Child: Grid grid }
                    && grid.Tag as string == TableTag)
                {
                    grid.Width = target.ActualWidth;
                }
            }
        }
    }

    /// <summary>Everything a block needs to draw itself, so it is passed once rather than six times.</summary>
    private sealed record Palette(
        FontFamily Prose,
        FontFamily Mono,
        Brush Foreground,
        Brush Muted,
        double Size,
        Action<string>? OnLink);

    private static Paragraph RenderHeading(MarkdownBlock.Heading heading, int position, Palette palette)
    {
        var block = new Paragraph
        {
            Margin = new Thickness(0, position > 0 ? 6 : 0, 0, 2),
        };

        // Two sizes only. A console pill is not a document, and six heading levels in a
        // 340px panel would differ by fractions of a pixel.
        AddSpans(block.Inlines, heading.Inlines, palette,
            bold: true, size: heading.Level <= 2 ? palette.Size + 2 : palette.Size);

        return block;
    }

    private static Paragraph RenderBullet(MarkdownBlock.Bullet bullet, Palette palette)
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
            Foreground = palette.Muted,
        });

        AddSpans(block.Inlines, bullet.Inlines, palette, bold: false, size: palette.Size);

        return block;
    }

    /// <summary>
    /// A fenced block: wrapped at word boundaries, and still able to scroll sideways.
    ///
    /// <b>The first version did not wrap at all, and the reason was half right.</b> It said a
    /// wrapped command line cannot be read back, because the break lands wherever the width
    /// happens to fall and a path or a pipeline reads as two broken ones. That is true of a
    /// path. It is not true of the other thing that ends up in a fence, which is a tool result
    /// pasted verbatim: asked to give a Jira comment unchanged, the model fenced four hundred
    /// characters of German prose, and the answer arrived as a single line you had to drag
    /// sideways to read. Reported exactly that way.
    ///
    /// <c>WrapWholeWords</c> is the distinction rather than a guess about content: the text
    /// engine breaks only where it already sees a break opportunity, so nothing has to decide
    /// what kind of text it is looking at.
    ///
    /// <b>What that means in practice, measured rather than assumed.</b> Prose wraps at its
    /// spaces, which is the fix. A Windows path wraps too -- the engine counts a backslash as
    /// a break opportunity, so
    /// <c>C:\Users\...\leerzeichen\der</c> / <c>\sehr\lang\ist\datei.xml</c> came back over
    /// two lines. That is not what this comment said on the first attempt, and it is worth
    /// being straight about: a path is no longer guaranteed to stay on one line. It breaks at
    /// a separator, which is where a file dialog and a browser break one too, and the
    /// alternative was the defect that was reported. What still cannot be broken is a token
    /// with no opportunity in it at all -- a hash, a base64 blob -- and that is what the
    /// horizontal scroller is still here for.
    /// </summary>
    private static Paragraph RenderCode(MarkdownBlock.Code code, Palette palette, double available)
    {
        var block = new Paragraph
        {
            Margin = new Thickness(8, 2, 0, 4),
        };

        var text = new TextBlock
        {
            Text = code.Text,
            FontFamily = palette.Mono,
            FontSize = palette.Size - 1,
            Foreground = palette.Muted,

            // Wrapped only where there is a space to wrap at. See the note above: this is
            // the whole fix, and NoWrap was the whole defect.
            TextWrapping = TextWrapping.WrapWholeWords,

            // A width to wrap AT. Without it the block is measured unbounded inside the
            // InlineUIContainer and wrapping never happens -- the same trap the tables hit,
            // and the reason MaxWidth on the scroller alone was not enough.
            MaxWidth = available,
            IsTextSelectionEnabled = true,
        };

        block.Inlines.Add(new InlineUIContainer
        {
            Child = new ScrollViewer
            {
                Content = text,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
                MaxWidth = available,
            },
        });

        return block;
    }

    /// <summary>
    /// A thematic break, as a hairline rather than as three dashes.
    ///
    /// An InlineUIContainer for the same reason the table needs one: a RichTextBlock takes
    /// Paragraphs and nothing else, so a horizontal line has to be an element hosted inside
    /// one.
    /// </summary>
    private static Paragraph RenderRule(Palette palette)
    {
        var block = new Paragraph { Margin = new Thickness(0, 8, 0, 8) };

        block.Inlines.Add(new InlineUIContainer
        {
            Child = new Border
            {
                Height = 1,
                Width = 420,
                Background = palette.Muted,
                Opacity = 0.3,
            },
        });

        return block;
    }

    private static Paragraph RenderParagraph(MarkdownBlock.Paragraph text, Palette palette)
    {
        var block = new Paragraph();
        AddSpans(block.Inlines, text.Inlines, palette, bold: false, size: palette.Size);
        return block;
    }

    /// <summary>
    /// A table, as a real grid hosted inside the text flow.
    ///
    /// <b>Why an InlineUIContainer.</b> A RichTextBlock takes Paragraphs and nothing else,
    /// so a table cannot be a block of its own here. The alternative was to render it as
    /// column-padded monospace text, which reads acceptably in a log and badly in the answer
    /// window, where the whole point is that the answer is a document. An InlineUIContainer
    /// lets one Paragraph host an arbitrary element, so the table is an actual grid with
    /// actual columns while everything around it stays in the same flow.
    ///
    /// The known cost: an element in a container does not take part in text selection, so a
    /// table cannot be swept with the cursor along with the prose around it. Worth it, and
    /// the cell text can still be read.
    /// </summary>
    /// <summary>
    /// How much of the width each column should get, from how much text it holds.
    ///
    /// <b>The square root, not the length itself.</b> A ticket table has a key of ten
    /// characters beside a summary of a hundred and twenty; shared out in proportion, the
    /// key gets 55 pixels and breaks in half while the summary takes three quarters of the
    /// row and does not need it. Text that wraps does not need width in proportion to its
    /// length -- it needs enough to make a readable paragraph -- so the curve is flattened.
    /// On that same table the key ends up around 85 pixels and the summary around 300,
    /// which is what a person would have chosen.
    /// </summary>
    private static double[] Weights(MarkdownBlock.Table table, int columns)
    {
        var longest = new int[columns];

        Measure(table.Header);

        foreach (MarkdownRow row in table.Rows)
            Measure(row);

        var weights = new double[columns];

        for (int i = 0; i < columns; i++)
            weights[i] = Math.Sqrt(Math.Clamp(longest[i], 3, 400));

        return weights;

        void Measure(MarkdownRow row)
        {
            for (int i = 0; i < columns && i < row.Cells.Count; i++)
            {
                int length = row.Cells[i].Inlines.Sum(span => span.Text.Length);

                if (length > longest[i])
                    longest[i] = length;
            }
        }
    }

    private static Paragraph RenderTable(MarkdownBlock.Table table, Palette palette, RichTextBlock target)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 4, 0, 6),
            ColumnSpacing = 14,
            RowSpacing = 2,
        };

        // Tagged so the size-changed handler can find it again. The width itself is not set
        // here: see StretchTables.
        grid.Tag = TableTag;

        int columns = table.Header.Cells.Count;
        double[] weights = Weights(table, columns);

        for (int i = 0; i < columns; i++)
        {
            // Every column is a SHARE of the width. None of them is auto, and that is the
            // correction to the previous two attempts.
            //
            // First the grid went into a horizontal ScrollViewer, which did nothing: an
            // InlineUIContainer measures its child unbounded, so the viewer sized itself to
            // the table and clipped in the same place. Then the columns were auto with the
            // last one star -- and with five columns that failed loudly: the four auto ones
            // claimed the whole fixed width between them, the star column was left zero, and
            // its dates wrapped to one character per line. Rows 264 pixels tall, three
            // columns pushed off the right-hand edge.
            //
            // Shares cannot do that. They always sum to exactly the width available, so no
            // column can starve another, and the long prose column simply wraps.
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(weights[i], GridUnitType.Star),

                // A floor, so a column of short values still shows its heading. Twelve
                // columns would need 528 of the ~690 available, which still fits; beyond
                // that the table is unreadable at this size whatever is done to it.
                MinWidth = 44,
            });
        }

        for (int i = 0; i < table.Rows.Count + 2; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddRow(grid, table.Header, table.Alignment, palette, line: 0, bold: true);

        // A rule under the header rather than a full set of borders. A grid of boxes in a
        // 340px panel is mostly lines; one rule is enough to say where the data starts.
        var rule = new Border
        {
            Height = 1,
            Background = palette.Muted,
            Opacity = 0.35,
            Margin = new Thickness(0, 2, 0, 3),
        };

        Grid.SetRow(rule, 1);
        Grid.SetColumn(rule, 0);
        Grid.SetColumnSpan(rule, Math.Max(columns, 1));
        grid.Children.Add(rule);

        for (int r = 0; r < table.Rows.Count; r++)
            AddRow(grid, table.Rows[r], table.Alignment, palette, line: r + 2, bold: false);

        var block = new Paragraph();
        block.Inlines.Add(new InlineUIContainer { Child = grid });

        return block;
    }

    private static void AddRow(
        Grid grid,
        MarkdownRow row,
        IReadOnlyList<ColumnAlignment> alignment,
        Palette palette,
        int line,
        bool bold)
    {
        for (int c = 0; c < row.Cells.Count; c++)
        {
            var cell = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontFamily = palette.Prose,
                FontSize = palette.Size,
                Foreground = palette.Foreground,
                HorizontalAlignment = (c < alignment.Count ? alignment[c] : ColumnAlignment.Left) switch
                {
                    ColumnAlignment.Center => HorizontalAlignment.Center,
                    ColumnAlignment.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Left,
                },
            };

            AddSpans(cell.Inlines, row.Cells[c].Inlines, palette, bold, palette.Size);

            Grid.SetRow(cell, line);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
    }

    /// <param name="bold">
    /// The block's base weight, kept apart from the spans' own. Rolled together they would
    /// invert inside a heading: the first pair of asterisks in "## **Title**" would close a
    /// span that was never opened.
    /// </param>
    private static void AddSpans(
        InlineCollection target,
        IReadOnlyList<MarkdownSpan> spans,
        Palette palette,
        bool bold,
        double size)
    {
        // Adjacent spans of one link go into one Hyperlink, so a link whose text is partly
        // bold is still a single click target rather than several touching ones.
        Hyperlink? link = null;
        string? href = null;

        foreach (MarkdownSpan span in spans)
        {
            if (span.Href != href)
            {
                link = null;
                href = span.Href;

                if (href is not null)
                {
                    string target_ = href;

                    link = new Hyperlink { UnderlineStyle = UnderlineStyle.Single };

                    // Click rather than NavigateUri, because a shellvis: target is an action
                    // inside this application. NavigateUri would hand it to the shell, which
                    // would either fail or, worse, ask the user to pick an application for a
                    // scheme that is ours.
                    link.Click += (_, _) => palette.OnLink?.Invoke(target_);

                    target.Add(link);
                }
            }

            InlineCollection destination = link?.Inlines ?? target;

            destination.Add(Build(span, palette, bold, size));
        }
    }

    private static Run Build(MarkdownSpan span, Palette palette, bool bold, double size)
    {
        if (span.Has(SpanStyle.Code))
        {
            return new Run
            {
                Text = span.Text,
                FontFamily = palette.Mono,
                FontSize = size - 1,
                Foreground = palette.Muted,

                // Upright, always. The block's style is italic for announcements, and
                // italic monospace is what a slanted terminal font looks like: wrong.
                FontStyle = FontStyle.Normal,
                FontWeight = FontWeights.Normal,
            };
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

        return run;
    }
}
