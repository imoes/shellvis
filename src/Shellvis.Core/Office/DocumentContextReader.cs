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
            _ when TextExtensions.Contains(extension) => ReadText(fullPath),
            _ => throw new NotSupportedException("Select a DOCX, XLSX, PPTX or text document."),
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

    /// <summary>
    /// Files read as plain text. Logs, exports, configuration and scripts are what arrives
    /// from Explorer's "Ask Shellvis" far more often than a Word document does.
    /// </summary>
    public static readonly IReadOnlySet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".tsv", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cfg",
        ".conf", ".toml", ".sql", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".py", ".cs",
        ".js", ".ts", ".html", ".htm", ".css", ".eml", ".reg",
    };

    /// <summary>
    /// The file's text when it can be read, and otherwise a description of it: name, type,
    /// size and when it changed.
    ///
    /// <b>Why not refuse.</b> From the workbench's picker a refusal is right -- the person can
    /// pick again. From Explorer they have already chosen, and "this type is not supported"
    /// ends the errand where the model could still help: say what a PDF called
    /// Angebot_4711.pdf most likely belongs to, or find the mail it came with.
    /// </summary>
    public static DocumentContext ReadOrDescribe(string path)
    {
        try
        {
            return Read(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException)
        {
            var info = new FileInfo(Path.GetFullPath(path));

            string why = ex is NotSupportedException
                ? "only Office and text files are read"
                : ex.Message.TrimEnd('.');

            string described =
                $"The contents of this file cannot be read here ({why}). What is known about it:\n"
                + $"name: {info.Name}\n"
                + $"type: {(info.Extension.Length > 0 ? info.Extension : "(none)")}\n"
                + $"size: {info.Length / 1024.0:F0} KB\n"
                + $"changed: {info.LastWriteTime:yyyy-MM-dd HH:mm}\n"
                + $"folder: {info.DirectoryName}";

            return new DocumentContext(info.Name, info.FullName, described, Truncated: false);
        }
    }

    private static string ReadText(string path)
    {
        // Detects a BOM and falls back to UTF-8; a legacy ANSI file loses its umlauts, which
        // is still a readable text and not worth a code page guess.
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        char[] buffer = new char[MaxChars + 1];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);

        return new string(buffer, 0, read);
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
