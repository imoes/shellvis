using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WordColor = DocumentFormat.OpenXml.Wordprocessing.Color;

namespace Shellvis.Core.Office;

/// <summary>
/// Writes a .docx from a small Markdown subset.
///
/// Markdown is the input format on purpose. The alternative is a tool with a dozen
/// parameters for headings, paragraphs, bullets and tables, which a model fills in
/// clumsily and which cannot express nesting at all. Markdown is a notation every
/// model already writes fluently, and the conversion to OpenXML is mechanical.
///
/// Supported: ATX headings (# to ######), paragraphs, unordered lists (- or *),
/// ordered lists (1.), pipe tables, bold and italic inline, horizontal rules, and
/// fenced code blocks. Anything unrecognised becomes a plain paragraph rather than
/// being dropped, because silently losing the user's content is the worst outcome.
///
/// No Office installation is involved, and nothing can deadlock: this is the path
/// that also works from a service.
/// </summary>
public static class WordWriter
{
    /// <summary>Create a document. Overwrites an existing file at that path.</summary>
    /// <returns>A description of what was written, for the tool result.</returns>
    public static string Create(string path, string markdown, string? title = null)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        int blocks;

        // Scoped so the package is closed before the file is measured. Reading
        // FileInfo.Length while the document is still open reported 0 bytes, which
        // looks exactly like a failed write in the tool result.
        using (WordprocessingDocument document = WordprocessingDocument.Create(
            full, WordprocessingDocumentType.Document))
        {
            MainDocumentPart main = document.AddMainDocumentPart();
            main.Document = new Document();
            Body body = main.Document.AppendChild(new Body());

            // Heading styles have to exist in the document before they can be
            // referenced. Without this part, "Heading1" is an unknown style name and
            // Word silently renders the paragraph as body text.
            AddStyles(main);

            if (!string.IsNullOrWhiteSpace(title))
                body.AppendChild(Heading(title, 0));

            blocks = Render(body, markdown);

            if (!string.IsNullOrWhiteSpace(title))
                SetDocumentTitle(document, title);

            main.Document.Save();
        }

        var info = new FileInfo(full);
        return $"wrote {full} ({blocks} block(s), {info.Length:N0} bytes)";
    }

    private static int Render(Body body, string markdown)
    {
        string[] lines = markdown.ReplaceLineEndings("\n").Split('\n');
        int blocks = 0;
        int index = 0;

        while (index < lines.Length)
        {
            string line = lines[index];
            string trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            // Fenced code: consume to the closing fence and emit as monospace so
            // indentation and symbols survive.
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var code = new List<string>();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    code.Add(lines[index++]);

                if (index < lines.Length)
                    index++; // closing fence

                body.AppendChild(CodeBlock(code));
                blocks++;
                continue;
            }

            // A pipe table is a multi-line construct, so it is consumed as a unit.
            if (trimmed.StartsWith('|') && index + 1 < lines.Length && IsTableSeparator(lines[index + 1]))
            {
                var rows = new List<string>();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('|'))
                    rows.Add(lines[index++]);

                body.AppendChild(BuildTable(rows));
                blocks++;
                continue;
            }

            if (trimmed.StartsWith("---", StringComparison.Ordinal) || trimmed.StartsWith("***", StringComparison.Ordinal))
            {
                body.AppendChild(HorizontalRule());
                blocks++;
                index++;
                continue;
            }

            int level = HeadingLevel(trimmed);
            if (level > 0)
            {
                body.AppendChild(Heading(trimmed[level..].Trim(), level));
                blocks++;
                index++;
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                body.AppendChild(ListItem(trimmed[2..].Trim(), ordered: false));
                blocks++;
                index++;
                continue;
            }

            if (IsOrderedItem(trimmed, out string orderedText))
            {
                body.AppendChild(ListItem(orderedText, ordered: true));
                blocks++;
                index++;
                continue;
            }

            body.AppendChild(Paragraph(trimmed));
            blocks++;
            index++;
        }

        return blocks;
    }

    private static int HeadingLevel(string line)
    {
        int hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
            hashes++;

        // "#hello" is not a heading; the space is what makes it one.
        return hashes is > 0 and <= 6 && hashes < line.Length && line[hashes] == ' ' ? hashes : 0;
    }

    private static bool IsOrderedItem(string line, out string text)
    {
        int dot = line.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0 && line[..dot].All(char.IsDigit))
        {
            text = line[(dot + 2)..].Trim();
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static bool IsTableSeparator(string line)
    {
        string trimmed = line.Trim();
        return trimmed.StartsWith('|')
            && trimmed.Contains('-', StringComparison.Ordinal)
            && trimmed.All(c => c is '|' or '-' or ':' or ' ');
    }

    private static Paragraph Heading(string text, int level)
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = level == 0 ? "Title" : $"Heading{Math.Clamp(level, 1, 6)}" }));

        AppendInline(paragraph, text);
        return paragraph;
    }

    private static Paragraph Paragraph(string text)
    {
        var paragraph = new Paragraph();
        AppendInline(paragraph, text);
        return paragraph;
    }

    private static Paragraph ListItem(string text, bool ordered)
    {
        // Real Word numbering needs a NumberingDefinitionsPart with abstract and
        // concrete numbering. For a generated document an indented bullet glyph is
        // visually equivalent and avoids a whole part that is easy to get subtly
        // wrong, so the marker is written as text.
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new Indentation { Left = "360" },
                new SpacingBetweenLines { After = "60" }));

        paragraph.AppendChild(new Run(new Text(ordered ? "•  " : "•  ") { Space = SpaceProcessingModeValues.Preserve }));
        AppendInline(paragraph, text);
        return paragraph;
    }

    private static Paragraph CodeBlock(IReadOnlyList<string> lines)
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new ParagraphBorders(
                    new LeftBorder { Val = BorderValues.Single, Size = 12, Color = "CCCCCC" }),
                new Indentation { Left = "240" },
                new SpacingBetweenLines { Before = "120", After = "120" }));

        for (int i = 0; i < lines.Count; i++)
        {
            var run = new Run(
                new RunProperties(new RunFonts { Ascii = "Cascadia Mono", HighAnsi = "Cascadia Mono" }),
                new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });

            paragraph.AppendChild(run);

            // A code block is one paragraph with explicit breaks, so the border and
            // indentation apply to the block as a whole rather than per line.
            if (i < lines.Count - 1)
                paragraph.AppendChild(new Run(new Break()));
        }

        return paragraph;
    }

    private static Paragraph HorizontalRule() =>
        new(new ParagraphProperties(
            new ParagraphBorders(
                new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "AAAAAA" })));

    private static Table BuildTable(IReadOnlyList<string> rows)
    {
        var table = new Table(
            new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4 },
                    new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4 },
                    new RightBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));

        bool isHeader = true;

        foreach (string row in rows)
        {
            if (IsTableSeparator(row))
            {
                isHeader = false;
                continue;
            }

            var tableRow = new TableRow();

            foreach (string cell in SplitCells(row))
            {
                var paragraph = new Paragraph();
                AppendInline(paragraph, cell, forceBold: isHeader);

                tableRow.AppendChild(new TableCell(
                    new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Auto }),
                    paragraph));
            }

            table.AppendChild(tableRow);
        }

        return table;
    }

    private static IEnumerable<string> SplitCells(string row)
    {
        string trimmed = row.Trim().Trim('|');
        return trimmed.Split('|').Select(c => c.Trim());
    }

    /// <summary>
    /// Turn inline markup into runs.
    ///
    /// Only bold and italic are handled, which covers what a model actually emits in
    /// prose. Unmatched markers are left as literal text rather than swallowed: a
    /// stray asterisk should look wrong, not make text vanish.
    /// </summary>
    private static void AppendInline(Paragraph paragraph, string text, bool forceBold = false)
    {
        int index = 0;

        while (index < text.Length)
        {
            int bold = text.IndexOf("**", index, StringComparison.Ordinal);
            int italic = FindSingleAsterisk(text, index);

            int next = bold >= 0 && (italic < 0 || bold <= italic) ? bold : italic;

            if (next < 0)
            {
                AppendRun(paragraph, text[index..], forceBold, false);
                return;
            }

            if (next > index)
                AppendRun(paragraph, text[index..next], forceBold, false);

            bool isBold = next == bold;
            string marker = isBold ? "**" : "*";
            int close = text.IndexOf(marker, next + marker.Length, StringComparison.Ordinal);

            if (close < 0)
            {
                // No closing marker: emit the rest literally.
                AppendRun(paragraph, text[next..], forceBold, false);
                return;
            }

            string inner = text[(next + marker.Length)..close];
            AppendRun(paragraph, inner, forceBold || isBold, !isBold);
            index = close + marker.Length;
        }
    }

    private static int FindSingleAsterisk(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] != '*')
                continue;

            // Skip a double marker, which belongs to bold.
            if (i + 1 < text.Length && text[i + 1] == '*')
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static void AppendRun(Paragraph paragraph, string text, bool bold, bool italic)
    {
        if (text.Length == 0)
            return;

        var run = new Run();

        if (bold || italic)
        {
            var properties = new RunProperties();
            if (bold)
                properties.AppendChild(new Bold());
            if (italic)
                properties.AppendChild(new Italic());

            run.AppendChild(properties);
        }

        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.AppendChild(run);
    }

    private static void SetDocumentTitle(WordprocessingDocument document, string title)
    {
        // Sets the Title in the file's own metadata, which is what Explorer and
        // SharePoint display, independently of the heading in the body.
        document.PackageProperties.Title = title;
        document.PackageProperties.Creator = "Shellvis";
        document.PackageProperties.Created = DateTime.Now;
    }

    /// <summary>
    /// Define the Title and Heading1..6 styles.
    ///
    /// Word does not invent these: a paragraph referencing "Heading1" when no such
    /// style exists renders as body text with no error, which looks like the tool
    /// silently ignoring the formatting.
    /// </summary>
    private static void AddStyles(MainDocumentPart main)
    {
        StyleDefinitionsPart part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles();

        styles.AppendChild(BuildStyle("Title", "Title", 56, bold: true, color: "1F1F23", spaceAfter: 240));

        // Sizes are in half-points, tapering from 32 (16pt) down to 20 (10pt).
        int[] sizes = [32, 28, 24, 22, 20, 20];
        for (int level = 1; level <= 6; level++)
        {
            styles.AppendChild(BuildStyle(
                $"Heading{level}",
                $"heading {level}",
                sizes[level - 1],
                bold: true,
                color: level <= 2 ? "1F1F23" : "44464F",
                spaceAfter: 120,
                spaceBefore: 240));
        }

        part.Styles = styles;
        part.Styles.Save();
    }

    private static Style BuildStyle(
        string id, string name, int halfPoints, bool bold, string color,
        int spaceAfter, int spaceBefore = 0)
    {
        return new Style(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                new SpacingBetweenLines
                {
                    After = spaceAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Before = spaceBefore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }),
            new StyleRunProperties(
                new Bold { Val = OnOffValue.FromBoolean(bold) },
                new WordColor { Val = color },
                new FontSize { Val = halfPoints.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
            CustomStyle = false,
        };
    }
}
