using System.Text;
using Shellvis.Core.Office;

namespace Shellvis.Core.Tools;

/// <summary>
/// The live-Office tools: see what is open, read it, render a PDF.
///
/// Kept apart from the OpenXML tools on purpose, and named so the model can tell them
/// apart. <c>office_write_*</c> creates files without Office; these three reach the
/// application. Merging them behind one name would leave the model unable to choose,
/// because the difference that matters is not the file format but whether Office has to
/// be involved at all.
/// </summary>
public sealed class OfficeComTools(OfficeComClient client)
{
    private readonly OfficeComClient _client = client;

    [ShellvisTool(
        "office_open_documents",
        SideEffect.ReadOnly,
        Description =
            "List the Word, Excel and PowerPoint documents that are open right now, with "
            + "their paths and whether they have unsaved changes. Use this to find out "
            + "what the user is working on before offering to act on it.",
        Glyph = "document")]
    public async Task<string> OpenDocuments(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OpenDocument> open = await _client
            .ListOpenAsync(cancellationToken)
            .ConfigureAwait(false);

        if (open.Count == 0)
        {
            return "No Word, Excel or PowerPoint document is open. "
                + "office_write_* creates files without needing Office at all.";
        }

        var sb = new StringBuilder();
        sb.Append(open.Count).AppendLine(" open document(s):");

        foreach (OpenDocument document in open)
            sb.Append("  ").AppendLine(document.ToString());

        return sb.ToString();
    }

    [ShellvisTool(
        "office_read_open",
        SideEffect.ReadOnly,
        Description =
            "Read the document the user currently has open in Word, Excel or PowerPoint: "
            + "the text, the used cells of the active sheet, or the text of every slide. "
            + "Pass word, excel or powerpoint.",
        PreviewParameter = "application",
        Glyph = "document")]
    public async Task<string> ReadOpen(
        string application,
        int maxChars = 6000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(application))
            return "Say which application: word, excel or powerpoint.";

        try
        {
            return await _client
                .ReadOpenAsync(application, maxChars, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // Returned as text, not thrown: the convention throughout this project is
            // that a bad argument is something the model can correct next round.
            return ex.Message;
        }
    }

    [ShellvisTool(
        "office_export_pdf",
        SideEffect.Mutating,
        Description =
            "Render a Word, Excel or PowerPoint file to PDF using Office. This is the "
            + "only way to get Office's own layout; it starts Office briefly if it is "
            + "not already running, and never modifies the source file.",
        PreviewParameter = "documentPath",
        Glyph = "document")]
    public async Task<string> ExportPdf(
        string documentPath,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
            return "Give the path of the document to export.";

        return await _client
            .ExportPdfAsync(documentPath, outputPath, cancellationToken)
            .ConfigureAwait(false);
    }
}
