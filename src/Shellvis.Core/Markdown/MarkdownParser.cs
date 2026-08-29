using System.Text;

namespace Shellvis.Core.Markdown;

/// <summary>
/// Turns the Markdown a model actually writes into a document that can be drawn.
///
/// <b>Why hand-written.</b> A Markdown package brings a parser, a styling system and a
/// dependency that has to keep up with WinUI, to handle six constructs; and it would style
/// the text its own way, where this console has already decided that prose is proportional
/// and machine output is monospace.
///
/// <b>Why it lives in Core, away from the renderer.</b> It was inside the WinUI renderer,
/// which meant it could not be instantiated without a XAML app and so was the one piece of
/// this project with no harness at all. It was also the piece the user's Markdown complaint
/// pointed at, and answering that complaint meant reading code rather than running it. A
/// pure function over strings can be run in a console, so it is one.
///
/// <b>What it deliberately does not parse.</b> Images, block quotes, nested lists, HTML.
/// The system prompt names the subset it does handle, so what arrives is what renders.
/// Anything unrecognised comes back as the literal text it is, which for that fragment is
/// the same result as before rather than a loss.
///
/// This is for PROSE only. Tool output goes through unparsed, because an asterisk in a
/// command line and a backtick in a PowerShell string are data, and turning them into
/// italics would corrupt what the console exists to show faithfully.
/// </summary>
public static class MarkdownParser
{
    /// <summary>Two spaces per level is what a model emits; deeper is flattened.</summary>
    private const int MaxDepth = 2;

    public static MarkdownDocument Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return MarkdownDocument.Empty;

        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<MarkdownSpan>();
        var fence = new List<string>();

        bool inFence = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;

            blocks.Add(new MarkdownBlock.Paragraph([.. paragraph]));
            paragraph.Clear();
        }

        foreach (string raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.TrimEnd();

            // Fences first, because inside one nothing is Markdown: a leading dash there is
            // a command-line switch, not a bullet.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    FlushParagraph();
                    blocks.Add(new MarkdownBlock.Code(string.Join('\n', fence), Closed: true));
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
                FlushParagraph();
                continue;
            }

            if (Heading(line) is { } heading)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock.Heading(heading.Level, Inlines(heading.Text)));
                continue;
            }

            if (Bullet(line) is { } bullet)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock.Bullet(
                    bullet.Depth, bullet.Marker, Inlines(bullet.Text)));
                continue;
            }

            // An ordinary line, joined to the paragraph with a space rather than a break: a
            // model wrapping its prose at 80 columns should not produce a ragged column in a
            // panel of a different width.
            if (paragraph.Count > 0)
                paragraph.Add(new MarkdownSpan(" ", SpanStyle.None));

            paragraph.AddRange(Inlines(line));
        }

        // An unterminated fence is normal while streaming. Marked as such so the renderer
        // could show it differently, and kept as code either way.
        if (fence.Count > 0)
        {
            FlushParagraph();
            blocks.Add(new MarkdownBlock.Code(string.Join('\n', fence), Closed: false));
        }

        FlushParagraph();

        return new MarkdownDocument(blocks);
    }

    private static (int Level, string Text)? Heading(string line)
    {
        int hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
            hashes++;

        // A hash with no space after it is not a heading, it is a C# reference or a comment.
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
        int depth = Math.Min(indent / 2, MaxDepth);

        if (rest.Length > 1 && rest[0] is '-' or '*' or '+' && rest[1] == ' ')
            return (depth, "•", rest[2..].Trim());

        // Ordered items keep their own number rather than being renumbered: the model may be
        // continuing a list, and renumbering from one would silently rewrite a sequence that
        // meant something.
        int digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            digits++;

        if (digits > 0 && digits + 1 < rest.Length && rest[digits] == '.' && rest[digits + 1] == ' ')
            return (depth, rest[..(digits + 1)], rest[(digits + 2)..].Trim());

        return null;
    }

    /// <summary>
    /// Turn one line's inline markup into spans.
    ///
    /// A hand-rolled scanner rather than a regex sweep, because the delimiters nest and
    /// overlap: <c>**bold `code`**</c> is ordinary, and a regex per delimiter would either
    /// miss it or match across the whole line. A single left-to-right pass with state flags
    /// is both shorter and correct for the cases that occur.
    /// </summary>
    public static IReadOnlyList<MarkdownSpan> Inlines(string text)
    {
        var spans = new List<MarkdownSpan>();
        var buffer = new StringBuilder();

        bool strong = false;
        bool emphasis = false;
        bool strike = false;

        void Emit()
        {
            if (buffer.Length == 0)
                return;

            SpanStyle style = SpanStyle.None;

            if (strong)
                style |= SpanStyle.Strong;

            if (emphasis)
                style |= SpanStyle.Emphasis;

            if (strike)
                style |= SpanStyle.Strike;

            spans.Add(new MarkdownSpan(buffer.ToString(), style));
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
                    spans.Add(new MarkdownSpan(text[(i + 1)..close], SpanStyle.Code));
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
            // an OPENING one: it stops a stray asterisk pair mid-stream from turning the
            // rest of the answer bold. Applied to a CLOSING one it is wrong, because the
            // last pair of a span has nothing after it, so the guard refused to close and
            // both asterisks came out literally. Headings rendered as "24.08.2026**".
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

        return spans;
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
