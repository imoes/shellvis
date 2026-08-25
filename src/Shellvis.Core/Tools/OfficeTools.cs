using Shellvis.Core.Office;

namespace Shellvis.Core.Tools;

/// <summary>
/// Word, Excel and PowerPoint files, created and read without Office installed.
///
/// Every tool here takes its content as text in a format a model already writes
/// fluently: Markdown for documents and slides, a Markdown table or CSV or TSV for
/// spreadsheets. The alternative -- a tool with parameters for headings, bullets, cell
/// ranges and slide layouts -- gets filled in badly, cannot express nesting, and
/// forces the model to think about file structure instead of content.
///
/// This is the headless path. It needs no Office installation, cannot deadlock on a
/// modal dialog, and is therefore the only path that will also work from the broker
/// service (KB 257757). Driving a *running* Word or Excel instance is a separate
/// capability that has to live in the interactive process.
/// </summary>
public sealed class OfficeTools
{
    [ShellvisTool(
        "word_create",
        SideEffect.Mutating,
        Description =
            "Create a Word .docx from Markdown. Supports # headings, paragraphs, "
            + "- bullets, numbered lists, pipe tables, **bold**, *italic*, fenced code "
            + "and horizontal rules. Write the document content as Markdown and it is "
            + "converted to real Word styles.",
        PreviewParameter = "path",
        Glyph = "document")]
    public string CreateWord(
        string path,
        string markdown,
        string? title = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "error: a file path is required.";

        if (string.IsNullOrWhiteSpace(markdown))
            return "error: there is no content to write.";

        try
        {
            return WordWriter.Create(EnsureExtension(path, ".docx"), markdown, title);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return $"error: could not write the document: {ex.Message}";
        }
    }

    [ShellvisTool(
        "excel_create",
        SideEffect.Mutating,
        Description =
            "Create an Excel .xlsx from tabular text: a Markdown pipe table, TSV or "
            + "CSV. The format is detected automatically. The first row becomes a "
            + "frozen bold header, and numbers and dates are stored as real values "
            + "rather than text so formulas and charts work.",
        PreviewParameter = "path",
        Glyph = "sheet")]
    public string CreateExcel(
        string path,
        string data,
        string sheetName = "Sheet1")
    {
        if (string.IsNullOrWhiteSpace(path))
            return "error: a file path is required.";

        if (string.IsNullOrWhiteSpace(data))
            return "error: there is no data to write.";

        try
        {
            return SheetWriter.Create(EnsureExtension(path, ".xlsx"), data, sheetName);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return $"error: could not write the workbook: {ex.Message}";
        }
    }

    [ShellvisTool(
        "excel_add_sheet",
        SideEffect.Mutating,
        Description =
            "Add a sheet to an existing .xlsx, or create the file if it does not exist. "
            + "A sheet of the same name is replaced, so re-running a step is safe.",
        PreviewParameter = "sheetName",
        Glyph = "sheet")]
    public string AddExcelSheet(
        string path,
        string data,
        string sheetName)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sheetName))
            return "error: a file path and a sheet name are required.";

        try
        {
            return SheetWriter.AddSheet(EnsureExtension(path, ".xlsx"), data, sheetName);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return $"error: could not update the workbook: {ex.Message}";
        }
    }

    [ShellvisTool(
        "excel_read",
        SideEffect.ReadOnly,
        Description =
            "Read a sheet from an .xlsx as tab-separated text. Use it to inspect data "
            + "before working with it, or to verify what was written.",
        PreviewParameter = "path",
        Glyph = "read")]
    public string ReadExcel(
        string path,
        string? sheetName = null,
        int maxRows = 200)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "error: a file path is required.";

        try
        {
            return SheetWriter.Read(path, sheetName, Math.Clamp(maxRows, 1, 5000));
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return $"error: could not read the workbook: {ex.Message}";
        }
    }

    [ShellvisTool(
        "excel_sheets",
        SideEffect.ReadOnly,
        Description = "List the sheets in an .xlsx with their used size.",
        PreviewParameter = "path",
        Glyph = "sheet")]
    public string ListExcelSheets(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "error: a file path is required.";

        try
        {
            return SheetWriter.ListSheets(path);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return $"error: could not read the workbook: {ex.Message}";
        }
    }

    [ShellvisTool(
        "powerpoint_create",
        SideEffect.Mutating,
        Description =
            "Create a PowerPoint .pptx from Markdown. Separate slides with ---, give "
            + "each a # heading for its title and - bullets for its body, and add a "
            + "line starting with 'Notes:' for speaker notes. A slide with no bullets "
            + "uses the title layout.",
        PreviewParameter = "path",
        Glyph = "slides")]
    public string CreatePowerPoint(
        string path,
        string markdown)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "error: a file path is required.";

        if (string.IsNullOrWhiteSpace(markdown))
            return "error: there is no content to write.";

        try
        {
            return SlideWriter.Create(EnsureExtension(path, ".pptx"), markdown);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return $"error: could not write the presentation: {ex.Message}";
        }
    }

    [ShellvisTool(
        "powerpoint_read",
        SideEffect.ReadOnly,
        Description =
            "Read the text of a .pptx slide by slide. Use it to verify a deck or to "
            + "summarise one that already exists.",
        PreviewParameter = "path",
        Glyph = "read")]
    public string ReadPowerPoint(string path, int maxSlides = 50)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "error: a file path is required.";

        try
        {
            return SlideWriter.Read(path, Math.Clamp(maxSlides, 1, 500));
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return $"error: could not read the presentation: {ex.Message}";
        }
    }

    /// <summary>
    /// Append the right extension if the model left it off.
    ///
    /// A .docx saved without its extension opens as nothing in particular, and the
    /// mistake is invisible in the tool result. Fixing it silently is kinder than
    /// refusing, since the intent is never ambiguous.
    /// </summary>
    private static string EnsureExtension(string path, string extension) =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)
            ? path
            : path + extension;

    /// <summary>
    /// The failures that are worth reporting as text rather than throwing.
    ///
    /// Notably includes IOException: a file open in Word is locked, and "the file is in
    /// use" is something the model can act on by choosing another name.
    /// </summary>
    private static bool IsFileFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException
            or FormatException;
}
