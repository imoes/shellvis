using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Shellvis.Shell.Controls;

/// <summary>
/// Renders the Markdown a model actually writes into a <see cref="RichTextBlock"/>.
///
/// <b>Why hand-written.</b> The models answering here emit Markdown whether or not they are
/// asked to, and until now the console showed it raw: a calendar answer read
/// "- **Montag, 24. August**: ...", asterisks and all. The obvious fix is a Markdown control
/// from a toolkit package, and it was not taken. A general renderer brings a parser, a
/// styling system and a dependency that has to keep up with WinUI, to render six
/// constructs; and it would style the text its own way, where this console has already
/// decided that prose is proportional and machine output is monospace.
///
/// <b>What it deliberately does not do.</b> Tables, images, links, block quotes, nested
/// lists, HTML. The system prompt tells the model exactly this subset, so what arrives is
/// what renders -- the alternative, silently dropping a table, would leave an answer with a
/// hole in it. Anything unrecognised is shown as the literal text it is, which is the same
/// behaviour as before for that fragment rather than a loss.
///
/// The one hard rule: this is for PROSE only. Tool output goes through unrendered, because
/// an asterisk in a command line or a backtick in a PowerShell string is data, and turning
/// it into italics would corrupt what the console exists to show faithfully.
/// </summary>
internal static class MarkdownRenderer
{
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

        string[] lines = (markdown ?? string.Empty)
            .ReplaceLineEndings("\n")
            .Split('\n');

        var paragraph = new Paragraph();
        bool inFence = false;
        var fence = new List<string>();

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();

            // Fenced code. Handled before anything else, because inside a fence nothing is
            // Markdown -- a leading dash there is a command-line switch, not a bullet.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    Flush(target, ref paragraph);
                    target.Blocks.Add(CodeBlock(fence, mono, size, muted));
                    fence.Clear();
                }

                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                fence.Add(raw);
                continue;
            }

            // A blank line ends the paragraph. Runs of them collapse, because a model
            // padding its answer with empty lines should not push the next line off screen.
            if (line.Length == 0)
            {
                Flush(target, ref paragraph);
                continue;
            }

            if (Heading(line) is { } heading)
            {
                Flush(target, ref paragraph);

                var block = new Paragraph
                {
                    Margin = new Thickness(0, target.Blocks.Count > 0 ? 6 : 0, 0, 2),
                };

                // Two sizes only. A console pill is not a document, and six heading levels
                // in a 340px panel would differ by fractions of a pixel.
                AddInlines(block.Inlines, heading.Text, mono, muted,
                    bold: true, size: heading.Level <= 2 ? size + 2 : size);

                target.Blocks.Add(block);
                continue;
            }

            if (Bullet(line) is { } bullet)
            {
                Flush(target, ref paragraph);

                // Hanging indent, so a wrapped bullet lines up under its own text rather
                // than under the marker. The left margin has to be at LEAST the indent, or
                // the marker is drawn outside the block and clipped by whatever padding the
                // container has. That is not hypothetical: in the answer window the markers
                // were invisible for exactly that reason, leaving lines that looked indented
                // for no stated reason.
                const double Hang = 14;

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

                AddInlines(block.Inlines, bullet.Text, mono, muted, bold: false, size: size);
                target.Blocks.Add(block);
                continue;
            }

            // An ordinary line. Joined to the paragraph with a space rather than a break:
            // a model wrapping its prose at 80 columns should not produce a ragged column
            // in a panel of a different width.
            if (paragraph.Inlines.Count > 0)
                paragraph.Inlines.Add(new Run { Text = " " });

            AddInlines(paragraph.Inlines, line, mono, muted, bold: false, size: size);
        }

        // An unterminated fence is normal while streaming: the closing ``` has not arrived
        // yet. Rendered as code anyway, so a code block appears as it is typed instead of
        // sitting invisible until the last three characters land.
        if (fence.Count > 0)
        {
            Flush(target, ref paragraph);
            target.Blocks.Add(CodeBlock(fence, mono, size, muted));
        }

        Flush(target, ref paragraph);

        // A RichTextBlock with no blocks measures zero and the row collapses; an empty
        // paragraph keeps the row's height while a streamed answer is still empty.
        if (target.Blocks.Count == 0)
            target.Blocks.Add(new Paragraph());
    }

    private static void Flush(RichTextBlock target, ref Paragraph paragraph)
    {
        if (paragraph.Inlines.Count > 0)
        {
            target.Blocks.Add(paragraph);
            paragraph = new Paragraph();
        }
    }

    private static Paragraph CodeBlock(List<string> lines, FontFamily mono, double size, Brush muted)
    {
        var block = new Paragraph
        {
            Margin = new Thickness(8, 2, 0, 4),
        };

        block.Inlines.Add(new Run
        {
            // Joined with real line breaks and NOT wrapped: code that wraps is code that
            // cannot be read back, and the console already scrolls.
            Text = string.Join('\n', lines),
            FontFamily = mono,
            FontSize = size - 1,
            Foreground = muted,
        });

        return block;
    }

    private static (int Level, string Text)? Heading(string line)
    {
        int hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
            hashes++;

        // A '#' with no space after it is not a heading, it is a C# reference or a comment.
        if (hashes is 0 or > 6 || hashes >= line.Length || line[hashes] != ' ')
            return null;

        return (hashes, line[(hashes + 1)..].Trim());
    }

    private static (int Depth, string Marker, string Text)? Bullet(string line)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
            indent++;

        string rest = line[indent..];

        // Two spaces per level, which is what a model emits; deeper than two levels is
        // flattened, because a 340px panel cannot show a third indent usefully.
        int depth = Math.Min(indent / 2, 2);

        if (rest.Length > 1 && rest[0] is '-' or '*' or '+' && rest[1] == ' ')
            return (depth, "•", rest[2..].Trim());

        // Ordered items keep their own number rather than being renumbered: the model may
        // be continuing a list, and renumbering from one would silently renumber a
        // sequence that meant something.
        int digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            digits++;

        if (digits > 0 && digits + 1 < rest.Length && rest[digits] == '.' && rest[digits + 1] == ' ')
            return (depth, rest[..(digits + 1)], rest[(digits + 2)..].Trim());

        return null;
    }

    /// <summary>
    /// Turn one line's inline markup into runs.
    ///
    /// A hand-rolled scanner rather than a regex sweep, because the delimiters nest and
    /// overlap: <c>**bold `code`**</c> is ordinary, and a regex per delimiter would either
    /// miss it or match across the whole line. A single left-to-right pass with a state
    /// flag is both shorter and correct for the cases that occur.
    /// </summary>
    private static void AddInlines(
        InlineCollection target,
        string text,
        FontFamily mono,
        Brush muted,
        bool bold,
        double size)
    {
        // Kept apart from the caller's `bold`, which is the base weight for a heading. Rolled
        // together they invert: "## **Title**" would start strong, and the first "**" would
        // then CLOSE the span instead of opening it, leaving the title unbold and the rest
        // of the line bold.
        bool strong = false;
        bool emphasis = false;
        bool strike = false;

        var buffer = new System.Text.StringBuilder();

        void Emit()
        {
            if (buffer.Length == 0)
                return;

            var run = new Run
            {
                Text = buffer.ToString(),
                FontSize = size,
                FontWeight = bold || strong ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle = emphasis ? FontStyle.Italic : FontStyle.Normal,
            };

            if (strike)
                run.TextDecorations = TextDecorations.Strikethrough;

            target.Add(run);
            buffer.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // A backslash escape, so a model that means a literal asterisk can say so.
            if (c == '\\' && i + 1 < text.Length && "*_`~\\".Contains(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);

                if (close > i)
                {
                    Emit();

                    target.Add(new Run
                    {
                        Text = text[(i + 1)..close],
                        FontFamily = mono,
                        FontSize = size - 1,
                        Foreground = muted,

                        // Upright, always. The block's style is italic for announcements,
                        // and italic monospace is what a slanted terminal font looks like:
                        // wrong. A code span is quoted machine text and should read as
                        // machine text whatever the paragraph around it is doing.
                        FontStyle = FontStyle.Normal,
                        FontWeight = FontWeights.Normal,
                    });

                    i = close;
                    continue;
                }

                // An unclosed backtick is a backtick. Common while streaming, and common in
                // PowerShell, where it is the escape character.
                buffer.Append(c);
                continue;
            }

            // The guard is asymmetric on purpose, and getting that wrong was the first bug
            // this renderer shipped with. Requiring a later matching delimiter is right for
            // an OPENING one -- it stops a stray "**" mid-stream from turning the rest of
            // the answer bold. Applied to a CLOSING one it is wrong: the last "**" of a
            // pair has nothing after it, so the guard refused to close and both asterisks
            // came out literally. Headings rendered as "Montag, 24.08.2026**".
            //
            // So: if the span is open, the delimiter closes it, unconditionally.
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                if (strong || Closes(text, i + 2, c, 2))
                {
                    Emit();
                    strong = !strong;
                    i++;
                    continue;
                }
            }
            else if (c == '*' || c == '_')
            {
                // A lone underscore inside a word is part of the word: snake_case names are
                // everywhere in this domain, and italicising half of one is worse than
                // leaving the character alone.
                bool insideWord = c == '_'
                    && i > 0 && char.IsLetterOrDigit(text[i - 1])
                    && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);

                if (!insideWord && (emphasis || Closes(text, i + 1, c, 1)))
                {
                    Emit();
                    emphasis = !emphasis;
                    continue;
                }
            }

            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~'
                && (strike || Closes(text, i + 2, '~', 2)))
            {
                Emit();
                strike = !strike;
                i++;
                continue;
            }

            buffer.Append(c);
        }

        Emit();
    }

    /// <summary>Whether a matching delimiter appears later in the line.</summary>
    private static bool Closes(string text, int from, char delimiter, int length)
    {
        for (int i = from; i + length <= text.Length; i++)
        {
            bool match = true;

            for (int k = 0; k < length; k++)
            {
                if (text[i + k] != delimiter)
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }
}
