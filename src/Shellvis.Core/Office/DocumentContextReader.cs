using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Shellvis.Core.Office;

/// <summary>Text from a closed Office document for an explicitly selected prompt context.</summary>
public static class DocumentContextReader
{
    public const int MaxChars = 12_000;
    public const long MaxFileBytes = 50L * 1024 * 1024;

    public static DocumentContext Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("The selected document could not be found.", path);

        string fullPath = Path.GetFullPath(path);
        if (new FileInfo(fullPath).Length > MaxFileBytes)
            throw new InvalidDataException("The selected document is larger than 50 MB.");
        string extension = Path.GetExtension(fullPath).ToLowerInvariant();
        string text = extension switch
        {
            ".docx" => ReadWord(fullPath),
            ".xlsx" => ReadWorkbook(fullPath),
            ".pptx" => SlideWriter.Read(fullPath, maxSlides: 100),
            _ => throw new NotSupportedException("Select a DOCX, XLSX or PPTX document."),
        };

        text = text.Trim();
        if (text.Length == 0)
            throw new InvalidDataException("The selected document contains no readable text.");

        bool truncated = text.Length > MaxChars
            || text.Contains("more row(s) not shown", StringComparison.Ordinal)
            || text.Contains("columns beyond the first 30 not shown", StringComparison.Ordinal)
            || text.Contains("more slide(s) not shown", StringComparison.Ordinal);
        return new DocumentContext(Path.GetFileName(fullPath), fullPath,
            truncated ? text[..MaxChars] : text, truncated);
    }

    private static string ReadWord(string path)
    {
        using WordprocessingDocument document = WordprocessingDocument.Open(path, false);
        Body? body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
            return string.Empty;

        var result = new StringBuilder();
        foreach (Paragraph paragraph in body.Descendants<Paragraph>())
        {
            string line = paragraph.InnerText.Trim();
            if (line.Length > 0)
                result.AppendLine(line);

            // Only a bounded preview reaches the model. Avoid walking a huge document
            // once its visible context is already full.
            if (result.Length > MaxChars)
                break;
        }

        return result.ToString();
    }

    private static string ReadWorkbook(string path)
    {
        using var workbook = new XLWorkbook(path);
        var result = new StringBuilder();
        int rowsShown = 0;

        foreach (IXLWorksheet sheet in workbook.Worksheets)
        {
            if (rowsShown >= 500 || result.Length > MaxChars)
            {
                result.AppendLine("... more row(s) not shown");
                break;
            }

            IXLRange? used = sheet.RangeUsed();
            result.Append("Sheet: ").AppendLine(sheet.Name);
            if (used is null)
            {
                result.AppendLine("(empty)");
                continue;
            }

            result.Append(used.RowCount()).Append(" row(s) x ")
                .Append(used.ColumnCount()).AppendLine(" column(s)");
            if (used.ColumnCount() > 30)
                result.AppendLine("... columns beyond the first 30 not shown");

            foreach (IXLRangeRow row in used.Rows())
            {
                if (rowsShown++ >= 500 || result.Length > MaxChars)
                {
                    result.AppendLine("... more row(s) not shown");
                    break;
                }

                result.AppendLine(string.Join('\t', row.Cells().Take(30)
                    .Select(cell => cell.GetFormattedString())));
            }
        }

        return result.ToString();
    }
}

public sealed record DocumentContext(string Name, string Source, string Text, bool Truncated);
