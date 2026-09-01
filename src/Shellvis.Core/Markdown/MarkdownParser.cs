using System.Text;

namespace Shellvis.Core.Markdown;

/// <summary>
/// Turns the Markdown a model actually writes into a document that can be drawn.
///
/// <b>Why hand-written.</b> A Markdown package brings a parser, a styling system and a
/// dependency that has to keep up with WinUI, to handle a handful of constructs; and it
/// would style the text its own way, where this console has already decided that prose is
/// proportional and machine output is monospace.
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

        string[] lines = markdown.ReplaceLineEndings("\n").Split('\n');

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

        // Indexed rather than foreach, because a table is only a table if the NEXT line is
        // its separator. Without that lookahead a single row of pipes, which is ordinary
        // prose about a shell pipeline, would open a table.
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd();

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
                fence.Add(lines[index]);
                continue;
            }

            // A blank line ends the paragraph. Runs of them collapse, because a model
            // padding its answer with empty lines should not push the next line off screen.
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (IsRule(line))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock.Rule());
                continue;
            }

            if (Heading(line) is { } heading)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock.Heading(heading.Level, Inlines(heading.Text)));
                continue;
            }

            if (TryTable(lines, index, out MarkdownBlock.Table? table, out int consumed))
            {
                FlushParagraph();
                blocks.Add(table!);
                index += consumed - 1;
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

    /// <summary>
    /// Whether the line is a thematic break.
    ///
    /// Checked BEFORE the bullet rule, because "---" also begins with a dash and would
    /// otherwise be read as a bullet with no text. Three or more of one character and
    /// nothing else, which is the CommonMark rule and also what a model writes.
    /// </summary>
    private static bool IsRule(string line)
    {
        string text = line.Replace(" ", string.Empty, StringComparison.Ordinal);

        if (text.Length < 3)
            return false;

        char first = text[0];

        return first is '-' or '*' or '_' && text.All(c => c == first);
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
    /// A GitHub-flavoured table, if this line starts one.
    ///
    /// <b>The separator row is what makes it a table</b>, not the pipes. A pipe is the most
    /// common character in this domain: every second answer contains a PowerShell pipeline,
    /// and treating one of those as a table row would swallow the sentence around it. So a
    /// header is only a header when the line under it is dashes and colons and nothing else.
    ///
    /// A table that is still arriving is NOT rendered as a table: while streaming, the
    /// separator may not have landed yet, and the header line stays ordinary prose until it
    /// does. That is one re-render later, and it avoids showing a one-column table that then
    /// reshapes itself.
    /// </summary>
    private static bool TryTable(string[] lines, int start, out MarkdownBlock.Table? table, out int consumed)
    {
        table = null;
        consumed = 0;

        if (start + 1 >= lines.Length)
            return false;

        string header = lines[start].Trim();

        if (!header.Contains('|', StringComparison.Ordinal))
            return false;

        string[] separators = SplitRow(lines[start + 1]);
        string[] headings = SplitRow(header);

        if (separators.Length == 0 || headings.Length == 0)
            return false;

        var alignment = new List<ColumnAlignment>(separators.Length);

        foreach (string cell in separators)
        {
            if (Alignment(cell) is not { } column)
                return false;

            alignment.Add(column);
        }

        // A separator with a different number of columns than the header is not a table
        // anyone meant to write, and guessing which side is right would reshape their data.
        if (alignment.Count != headings.Length)
            return false;

        var rows = new List<MarkdownRow>();
        int index = start + 2;

        while (index < lines.Length)
        {
            string line = lines[index].Trim();

            if (line.Length == 0 || !line.Contains('|', StringComparison.Ordinal))
                break;

            rows.Add(Row(SplitRow(lines[index]), headings.Length));
            index++;
        }

        table = new MarkdownBlock.Table(Row(headings, headings.Length), alignment, rows);
        consumed = index - start;

        return true;
    }

    /// <summary>The cells of one row, with the optional leading and trailing pipes removed.</summary>
    private static string[] SplitRow(string line)
    {
        string text = line.Trim();

        if (text.StartsWith('|'))
            text = text[1..];

        if (text.EndsWith('|'))
            text = text[..^1];

        if (text.Length == 0)
            return [];

        // An escaped pipe is a pipe: a cell may legitimately mention one, which in this
        // domain it often does.
        var cells = new List<string>();
        var cell = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                cell.Append('|');
                i++;
                continue;
            }

            if (text[i] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
                continue;
            }

            cell.Append(text[i]);
        }

        cells.Add(cell.ToString().Trim());

        return [.. cells];
    }

    /// <summary>The alignment a separator cell asks for, or null if it is not one.</summary>
    private static ColumnAlignment? Alignment(string cell)
    {
        string text = cell.Trim();

        if (text.Length == 0)
            return null;

        bool left = text.StartsWith(':');
        bool right = text.EndsWith(':');

        string dashes = text.Trim(':');

        if (dashes.Length == 0 || dashes.Any(c => c != '-'))
            return null;

        return (left, right) switch
        {
            (true, true) => ColumnAlignment.Center,
            (false, true) => ColumnAlignment.Right,
            _ => ColumnAlignment.Left,
        };
    }

    /// <summary>
    /// One row, squared off to the header's width.
    ///
    /// Short rows are padded and long ones trimmed rather than refused. A model miscounting
    /// pipes in one row of a ten-row table should cost that row's last cell, not the table.
    /// </summary>
    private static MarkdownRow Row(string[] cells, int width)
    {
        var built = new List<MarkdownCell>(width);

        for (int i = 0; i < width; i++)
            built.Add(new MarkdownCell(i < cells.Length ? Inlines(cells[i]) : []));

        return new MarkdownRow(built);
    }

    /// <summary>
    /// Turn one line's inline markup into spans.
    ///
    /// A hand-rolled scanner rather than a regex sweep, because the delimiters nest and
    /// overlap: <c>**bold `code`**</c> is ordinary, and a regex per delimiter would either
    /// miss it or match across the whole line. A single left-to-right pass with state flags
    /// is both shorter and correct for the cases that occur.
    /// </summary>
    public static IReadOnlyList<MarkdownSpan> Inlines(string text) => Inlines(text, href: null);

    private static IReadOnlyList<MarkdownSpan> Inlines(string text, string? href)
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

            if (href is not null)
                style |= SpanStyle.Link;

            spans.Add(new MarkdownSpan(buffer.ToString(), style, href));
            buffer.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // A backslash escape, so a model that means a literal asterisk can say so.
            //
            // The pipe is in the set because of tables: a cell containing one has to escape
            // it or the row splits there, so "FTP Server \| Update" is correct Markdown and
            // was reaching the screen with the backslash still in it. It is escaped even
            // outside a table, because a model that has learned the habit does not drop it
            // when the same sentence appears in a bullet.
            if (c == '\\' && i + 1 < text.Length && "*_`~\\[]|".Contains(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i++;
                continue;
            }

            // A link. Nested links are not a thing, so this only runs at the top level;
            // inside one, a bracket is a bracket.
            if (c == '[' && href is null && TryLink(text, i, out string? label, out string? target, out int end))
            {
                Emit();

                // The label is parsed in full, because a model writes bold and code inside
                // link text and it would be odd for the markup to stop working there.
                spans.AddRange(Inlines(label!, target));

                i = end;
                continue;
            }

            if (c == '`')
            {
                int close = text.IndexOf('`', i + 1);

                if (close > i)
                {
                    Emit();
                    spans.Add(new MarkdownSpan(
                        text[(i + 1)..close],
                        href is null ? SpanStyle.Code : SpanStyle.Code | SpanStyle.Link,
                        href));

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

    /// <summary>
    /// A link starting at <paramref name="open"/>, if there is one.
    ///
    /// Deliberately strict: the closing bracket must be followed immediately by a
    /// parenthesised target with no whitespace between them. A model writes prose containing
    /// brackets, and "[see note] (below)" is a sentence, not a link.
    /// </summary>
    private static bool TryLink(
        string text, int open, out string? label, out string? target, out int end)
    {
        label = null;
        target = null;
        end = open;

        int close = -1;
        int depth = 0;

        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '[')
            {
                depth++;
            }
            else if (text[i] == ']')
            {
                depth--;

                if (depth == 0)
                {
                    close = i;
                    break;
                }
            }
        }

        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(')
            return false;

        int stop = text.IndexOf(')', close + 2);

        if (stop < 0)
            return false;

        string href = text[(close + 2)..stop].Trim();

        // An empty target is not a link, and neither is a label nobody can click.
        if (href.Length == 0 || close == open + 1)
            return false;

        label = text[(open + 1)..close];
        target = href;
        end = stop;

        return true;
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
