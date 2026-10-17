using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace Shellvis.Core.Office;

/// <summary>
/// Reads and writes .xlsx without Excel installed.
///
/// Tabular input arrives as delimited text: a Markdown pipe table, TSV, or CSV. The
/// format is detected rather than declared, because a model asked to specify it gets
/// it wrong often enough to matter, while the text itself is unambiguous.
///
/// Values are typed on the way in. A column of numbers written as text produces a
/// spreadsheet where sums and charts silently do not work, which is the single most
/// common way a generated workbook disappoints the person who opens it.
/// </summary>
public static class SheetWriter
{
    /// <summary>Create a workbook from delimited text. Overwrites the file.</summary>
    public static string Create(string path, string data, string sheetName = "Sheet1", bool autoFilter = true)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        List<string[]> rows = Parse(data);

        if (rows.Count == 0)
            return "error: no rows could be read from the data.";

        using var workbook = new XLWorkbook();
        IXLWorksheet sheet = workbook.AddWorksheet(SafeSheetName(sheetName));

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Length; c++)
                WriteCell(sheet.Cell(r + 1, c + 1), rows[r][c]);
        }

        // Treat the first row as a header. Every table a model produces has one, and a
        // frozen bold header is what makes a sheet usable rather than merely correct.
        IXLRange used = sheet.Range(1, 1, rows.Count, rows.Max(r => r.Length));
        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        if (autoFilter && rows.Count > 1)
            used.SetAutoFilter();

        sheet.Columns().AdjustToContents();

        workbook.Properties.Author = "Shellvis";
        workbook.SaveAs(full);

        var info = new FileInfo(full);
        return $"wrote {full} ({rows.Count} row(s) x {rows.Max(r => r.Length)} column(s), {info.Length:N0} bytes)";
    }

    /// <summary>Add or replace a sheet in an existing workbook, creating the file if needed.</summary>
    public static string AddSheet(string path, string data, string sheetName)
    {
        string full = Path.GetFullPath(path);

        if (!File.Exists(full))
            return Create(full, data, sheetName);

        List<string[]> rows = Parse(data);
        if (rows.Count == 0)
            return "error: no rows could be read from the data.";

        using var workbook = new XLWorkbook(full);
        string safe = SafeSheetName(sheetName);

        // Replacing rather than failing: a model that re-runs a step expects the
        // second attempt to win, not to hit a duplicate-name error.
        if (workbook.Worksheets.TryGetWorksheet(safe, out IXLWorksheet? existing))
            existing.Delete();

        IXLWorksheet sheet = workbook.AddWorksheet(safe);

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Length; c++)
                WriteCell(sheet.Cell(r + 1, c + 1), rows[r][c]);
        }

        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();

        workbook.Save();
        return $"added sheet '{safe}' to {full} ({rows.Count} row(s))";
    }

    /// <summary>Read a sheet back as text, for verification or for reasoning over data.</summary>
    public static string Read(string path, string? sheetName = null, int maxRows = 200)
    {
        string full = Path.GetFullPath(path);

        if (!File.Exists(full))
            return $"error: no file at {full}";

        using var workbook = new XLWorkbook(full);

        IXLWorksheet? sheet = sheetName is { Length: > 0 }
            ? workbook.Worksheets.FirstOrDefault(
                w => w.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase))
            : workbook.Worksheets.FirstOrDefault();

        if (sheet is null)
        {
            string names = string.Join(", ", workbook.Worksheets.Select(w => w.Name));
            return $"error: no sheet named '{sheetName}'. Sheets present: {names}";
        }

        IXLRange? used = sheet.RangeUsed();
        if (used is null)
            return $"sheet '{sheet.Name}' is empty.";

        var sb = new StringBuilder();
        sb.Append("sheet '").Append(sheet.Name).Append("', ")
          .Append(used.RowCount()).Append(" row(s) x ")
          .Append(used.ColumnCount()).AppendLine(" column(s)");

        int shown = 0;
        foreach (IXLRangeRow row in used.Rows())
        {
            if (shown++ >= maxRows)
            {
                sb.Append("... ").Append(used.RowCount() - maxRows)
                  .AppendLine(" more row(s) not shown");
                break;
            }

            sb.AppendLine(string.Join('\t', row.Cells().Select(c => c.GetFormattedString())));
        }

        return sb.ToString();
    }

    /// <summary>List the sheets in a workbook.</summary>
    public static string ListSheets(string path)
    {
        string full = Path.GetFullPath(path);

        if (!File.Exists(full))
            return $"error: no file at {full}";

        using var workbook = new XLWorkbook(full);

        var sb = new StringBuilder();
        sb.Append(workbook.Worksheets.Count).Append(" sheet(s) in ").AppendLine(full);

        foreach (IXLWorksheet sheet in workbook.Worksheets)
        {
            IXLRange? used = sheet.RangeUsed();
            sb.Append("  ").Append(sheet.Name)
              .Append("  ")
              .Append(used is null ? "(empty)" : $"{used.RowCount()}x{used.ColumnCount()}")
              .AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Write one cell with an appropriate type.
    ///
    /// Order matters: numbers before dates, because a bare year like "2024" parses as
    /// both and is almost always meant as a number.
    /// </summary>
    private static void WriteCell(IXLCell cell, string raw)
    {
        string value = raw.Trim();

        if (value.Length == 0)
            return;

        // Invariant first, then the current culture: a model writing "1234.5" means a
        // decimal point, while a user pasting German data means "1234,5".
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double number)
            || double.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out number))
        {
            cell.Value = number;
            return;
        }

        if (bool.TryParse(value, out bool flag))
        {
            cell.Value = flag;
            return;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            || DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
        {
            cell.Value = date;
            return;
        }

        cell.Value = value;
    }

    /// <summary>
    /// Parse delimited text, detecting the format.
    ///
    /// Markdown pipe tables are checked first because they are what a model produces
    /// when asked for a table in prose, and their separator row would otherwise be
    /// read as a data row full of dashes.
    /// </summary>
    private static List<string[]> Parse(string data)
    {
        string[] lines = data.ReplaceLineEndings("\n")
            .Split('\n')
            .Where(l => l.Trim().Length > 0)
            .ToArray();

        if (lines.Length == 0)
            return [];

        bool isMarkdown = lines[0].TrimStart().StartsWith('|');

        var rows = new List<string[]>();

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (isMarkdown)
            {
                if (IsSeparatorRow(trimmed))
                    continue;

                rows.Add(trimmed.Trim('|').Split('|').Select(c => c.Trim()).ToArray());
                continue;
            }

            // Tab wins over comma: a CSV cell may legitimately contain a comma inside
            // quotes, while a tab in a value is vanishingly rare.
            char delimiter = trimmed.Contains('\t', StringComparison.Ordinal) ? '\t' : ',';
            rows.Add(delimiter == '\t'
                ? trimmed.Split('\t')
                : SplitCsv(trimmed));
        }

        return rows;
    }

    private static bool IsSeparatorRow(string line) =>
        line.StartsWith('|') && line.All(c => c is '|' or '-' or ':' or ' ');

    /// <summary>
    /// Split a CSV line, honouring double quotes.
    ///
    /// A naive Split(',') breaks the moment a value contains a comma, which for
    /// generated data means any currency amount or sentence.
    /// </summary>
    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                // A doubled quote inside a quoted field is an escaped quote.
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        fields.Add(current.ToString());
        return [.. fields];
    }

    /// <summary>
    /// Excel rejects certain characters in sheet names and caps them at 31 characters.
    /// Sanitising here turns a confusing save failure into a slightly renamed sheet.
    /// </summary>
    private static string SafeSheetName(string name)
    {
        string cleaned = new(name
            .Where(c => c is not (':' or '\\' or '/' or '?' or '*' or '[' or ']'))
            .ToArray());

        cleaned = cleaned.Trim();

        if (cleaned.Length == 0)
            cleaned = "Sheet1";

        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }
}
