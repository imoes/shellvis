using System.Text;

namespace Shellvis.Core.Office;

/// <summary>What a running Office application has open.</summary>
public sealed record OpenDocument(string Application, string Name, string? Path, bool Saved)
{
    public override string ToString() =>
        $"{Application}: \"{Name}\"{(Saved ? string.Empty : " (unsaved changes)")}"
        + (Path is { Length: > 0 } ? $"  {Path}" : "  (never saved)");
}

/// <summary>
/// Drives Office applications that are ALREADY OPEN, and exports documents.
///
/// The counterpart to the OpenXML writers, not a replacement. OpenXML creates files
/// without Office and is the right tool for that. What it cannot do is see the document
/// the user is looking at right now, or ask Office to render a PDF -- and those are the
/// two things this is for.
///
/// Two rules run through everything here.
///
/// <b>Attach, never create, for reading.</b> Word, Excel and PowerPoint are multi-instance:
/// asking COM to create one starts a fresh invisible copy, which would read an empty
/// document and, if teardown ever failed, leave a process nobody can see. Reading goes
/// through the Running Object Table or reports that nothing is open.
///
/// <b>Every reference is released, and every launched instance is quit in a finally.</b>
/// A verification script earlier in this project left EXCEL and POWERPNT running because
/// it threw before its Quit call. That is the failure mode, it is not hypothetical, and
/// the probe for this file checks the process list afterwards.
/// </summary>
public sealed class OfficeComClient(ComApartment apartment)
{
    private readonly ComApartment _apartment = apartment;

    private static readonly (string ProgId, string Name)[] Applications =
    [
        ("Word.Application", "Word"),
        ("Excel.Application", "Excel"),
        ("PowerPoint.Application", "PowerPoint"),
    ];

    /// <summary>Which Office applications are running, and what they have open.</summary>
    public Task<IReadOnlyList<OpenDocument>> ListOpenAsync(
        CancellationToken cancellationToken = default) =>
        _apartment.InvokeAsync<IReadOnlyList<OpenDocument>>(() =>
        {
            var found = new List<OpenDocument>();

            foreach ((string progId, string name) in Applications)
            {
                dynamic? app = Com.TryGetActive(progId);

                if (app is null)
                    continue;

                try
                {
                    switch (name)
                    {
                        // Cast to object so the CALL is bound statically. With a dynamic
                        // argument the whole invocation becomes dynamic, and C# then
                        // refuses lambda arguments (CS1977).
                        case "Word":
                            Collect(found, name, (object)app.Documents,
                                d => d.FullName, d => d.Name, d => d.Saved);
                            break;

                        case "Excel":
                            Collect(found, name, (object)app.Workbooks,
                                w => w.FullName, w => w.Name, w => w.Saved);
                            break;

                        default:
                            Collect(found, name, (object)app.Presentations,
                                p => p.FullName, p => p.Name, p => p.Saved);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // One application that will not answer must not hide the others. This
                    // happens for real: Office in a modal dialog rejects automation.
                    found.Add(new OpenDocument(
                        name, $"(running, but not answering: {ex.Message})", null, true));
                }
                finally
                {
                    Com.Release(app);
                }
            }

            return found;
        }, cancellationToken);

    /// <summary>
    /// Walk a document collection.
    ///
    /// Generic over the accessors rather than three near-identical loops, because the
    /// only thing that differs between Word, Excel and PowerPoint here is the name of the
    /// collection -- and a copy-pasted loop is where a missing Release ends up.
    /// </summary>
    private static void Collect(
        List<OpenDocument> into,
        string application,
        object collectionObject,
        Func<dynamic, string> fullName,
        Func<dynamic, string> name,
        Func<dynamic, bool> saved)
    {
        dynamic collection = collectionObject;

        try
        {
            int count = (int)collection.Count;

            for (int i = 1; i <= count; i++)
            {
                // 1-based, as every Office collection is.
                dynamic item = collection[i];

                try
                {
                    string path;

                    try
                    {
                        path = fullName(item);
                    }
                    catch (Exception)
                    {
                        // A never-saved document has no FullName and throws rather than
                        // returning empty.
                        path = string.Empty;
                    }

                    into.Add(new OpenDocument(
                        application,
                        name(item),
                        path.Length > 0 && path.Contains('\\') ? path : null,
                        saved(item)));
                }
                finally
                {
                    Com.Release(item);
                }
            }
        }
        finally
        {
            Com.Release(collection);
        }
    }

    /// <summary>
    /// Read what is open in one application.
    /// </summary>
    public Task<string> ReadOpenAsync(
        string application, int maxChars = 6000, CancellationToken cancellationToken = default) =>
        _apartment.InvokeAsync(() =>
        {
            (string progId, string name) = Resolve(application);

            dynamic? app = Com.TryGetActive(progId);

            if (app is null)
            {
                return $"{name} is not running, or has no document open. This reads the "
                    + "document you are looking at; to read a file from disk without "
                    + "Office, use the read tools instead.";
            }

            try
            {
                string text = name switch
                {
                    "Word" => ReadWord(app),
                    "Excel" => ReadExcel(app),
                    _ => ReadPowerPoint(app),
                };

                return text.Length <= maxChars
                    ? text
                    : text[..maxChars] + $"\n... truncated at {maxChars} of {text.Length} characters.";
            }
            catch (Exception ex)
            {
                return $"{name} refused the request: {ex.Message}";
            }
            finally
            {
                Com.Release(app);
            }
        }, cancellationToken);

    private static string ReadWord(dynamic app)
    {
        dynamic document = app.ActiveDocument;

        try
        {
            dynamic content = document.Content;

            try
            {
                // Named intermediates again: document.Paragraphs.Count leaks the
                // Paragraphs collection, which would keep a reference into the user's own
                // Word for as long as Shellvis runs.
                dynamic paragraphs = document.Paragraphs;
                dynamic words = document.Words;

                try
                {
                    var sb = new StringBuilder();
                    sb.Append("Word: ").AppendLine((string)document.Name);
                    sb.Append((int)paragraphs.Count).Append(" paragraph(s), ")
                        .Append((int)words.Count).AppendLine(" word(s)");
                    sb.AppendLine();
                    sb.Append((string)content.Text);

                    return sb.ToString();
                }
                finally
                {
                    Com.ReleaseAll(paragraphs, words);
                }
            }
            finally
            {
                Com.Release(content);
            }
        }
        finally
        {
            Com.Release(document);
        }
    }

    private static string ReadExcel(dynamic app)
    {
        dynamic book = app.ActiveWorkbook;

        try
        {
            dynamic sheet = book.ActiveSheet;

            try
            {
                // UsedRange rather than the whole sheet: a worksheet is a million rows,
                // and reading it would produce megabytes of empty cells.
                dynamic used = sheet.UsedRange;

                dynamic usedRows = used.Rows;
                dynamic usedColumns = used.Columns;
                dynamic usedCells = used.Cells;

                try
                {
                    int rows = (int)usedRows.Count;
                    int columns = (int)usedColumns.Count;

                    var sb = new StringBuilder();
                    sb.Append("Excel: ").Append((string)book.Name).Append(" / ")
                        .AppendLine((string)sheet.Name);
                    sb.Append(rows).Append(" x ").Append(columns).AppendLine(" used cells");
                    sb.AppendLine();

                    // Capped: a used range can still be tens of thousands of rows, and the
                    // point is to show what the sheet holds, not to move it into context.
                    int rowLimit = Math.Min(rows, 200);
                    int columnLimit = Math.Min(columns, 30);

                    for (int r = 1; r <= rowLimit; r++)
                    {
                        var cells = new List<string>(columnLimit);

                        for (int c = 1; c <= columnLimit; c++)
                        {
                            dynamic cell = usedCells[r, c];

                            try
                            {
                                object? value = cell.Text;
                                cells.Add(value?.ToString() ?? string.Empty);
                            }
                            finally
                            {
                                Com.Release(cell);
                            }
                        }

                        sb.AppendLine(string.Join("\t", cells).TrimEnd('\t'));
                    }

                    if (rows > rowLimit)
                        sb.Append("... ").Append(rows - rowLimit).AppendLine(" more row(s).");

                    return sb.ToString();
                }
                finally
                {
                    Com.ReleaseAll(usedCells, usedColumns, usedRows);
                    Com.Release(used);
                }
            }
            finally
            {
                Com.Release(sheet);
            }
        }
        finally
        {
            Com.Release(book);
        }
    }

    private static string ReadPowerPoint(dynamic app)
    {
        dynamic presentation = app.ActivePresentation;

        try
        {
            dynamic slides = presentation.Slides;
            int count = (int)slides.Count;

            var sb = new StringBuilder();
            sb.Append("PowerPoint: ").AppendLine((string)presentation.Name);
            sb.Append(count).AppendLine(" slide(s)");
            sb.AppendLine();

            for (int i = 1; i <= count; i++)
            {
                dynamic slide = slides[i];

                try
                {
                    sb.Append("Slide ").Append(i).Append(": ");

                    dynamic shapes = slide.Shapes;

                    try
                    {
                        var texts = new List<string>();

                        for (int s = 1; s <= (int)shapes.Count; s++)
                        {
                            dynamic shape = shapes[s];

                            dynamic? frame = null;
                            dynamic? range = null;

                            try
                            {
                                // HasTextFrame first: a picture or a line has no TextFrame
                                // and asking for one throws.
                                if ((int)shape.HasTextFrame != 0)
                                {
                                    // Named, not chained. shape.TextFrame.TextRange.Text
                                    // would leak two references per shape, and a deck with
                                    // fifty shapes then holds PowerPoint open.
                                    frame = shape.TextFrame;

                                    if ((int)frame.HasText != 0)
                                    {
                                        range = frame.TextRange;
                                        texts.Add(((string)range.Text).Trim());
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                // A shape that will not answer is skipped rather than
                                // failing the whole slide.
                            }
                            finally
                            {
                                Com.ReleaseAll(range, frame, shape);
                            }
                        }

                        sb.AppendLine(string.Join(" | ", texts));
                    }
                    finally
                    {
                        Com.Release(shapes);
                    }
                }
                finally
                {
                    Com.Release(slide);
                }
            }

            Com.Release(slides);

            return sb.ToString();
        }
        finally
        {
            Com.Release(presentation);
        }
    }

    /// <summary>
    /// Export a document to PDF.
    ///
    /// This is the one operation that may START Office, because rendering a PDF is
    /// precisely what only Office can do and the file usually is not open. The launched
    /// instance is quit in a finally, and the probe checks the process list afterwards --
    /// that check is the whole reason this is safe to offer.
    /// </summary>
    public Task<string> ExportPdfAsync(
        string documentPath, string? outputPath = null, CancellationToken cancellationToken = default) =>
        _apartment.InvokeAsync(() =>
        {
            if (!File.Exists(documentPath))
                return $"there is no file at {documentPath}.";

            string extension = Path.GetExtension(documentPath).ToLowerInvariant();

            string target = outputPath is { Length: > 0 }
                ? outputPath
                : Path.ChangeExtension(documentPath, ".pdf");

            try
            {
                return extension switch
                {
                    ".doc" or ".docx" or ".rtf" or ".odt" => ExportWord(documentPath, target),
                    ".xls" or ".xlsx" or ".csv" => ExportExcel(documentPath, target),
                    ".ppt" or ".pptx" => ExportPowerPoint(documentPath, target),
                    _ => $"'{extension}' is not a document Office can export. "
                        + "Supported: Word, Excel and PowerPoint formats.",
                };
            }
            catch (Exception ex)
            {
                return $"the export failed: {ex.GetType().Name}: {ex.Message}";
            }
        }, cancellationToken);

    private static string ExportWord(string source, string target)
    {
        // Attach if Word is already open, so the user's session is reused and no second
        // process appears. Only launch when there is nothing to attach to.
        dynamic? app = Com.TryGetActive("Word.Application");
        bool launched = app is null;

        app ??= Com.GetOrCreate("Word.Application");

        dynamic? documents = null;
        dynamic? document = null;

        try
        {
            if (launched)
                app.Visible = false;

            // NO TWO DOTS IN ONE LINE. `app.Documents.Open(...)` would create a
            // Documents collection reference that nothing ever releases, and a leaked
            // reference keeps the Office process alive -- which is precisely how an
            // EXCEL.EXE survived an export in testing. Every intermediate gets a name so
            // it can be released.
            documents = app.Documents;

            // ReadOnly and AddToRecentFiles:false -- an export must not modify the file
            // or push itself into the user's recent list.
            document = documents.Open(source, ReadOnly: true, AddToRecentFiles: false);

            // 17 = wdExportFormatPDF.
            document.ExportAsFixedFormat(target, 17);

            return Describe(target, launched, "Word");
        }
        finally
        {
            Com.Release(documents);
            Close(document, app, launched);
        }
    }

    private static string ExportExcel(string source, string target)
    {
        dynamic? app = Com.TryGetActive("Excel.Application");
        bool launched = app is null;

        app ??= Com.GetOrCreate("Excel.Application");

        dynamic? books = null;
        dynamic? book = null;

        try
        {
            if (launched)
            {
                app.Visible = false;

                // Excel likes to ask about links and formats. Nobody is watching a
                // headless export, so a prompt would be an indefinite hang.
                app.DisplayAlerts = false;
            }

            // The Workbooks collection is named and released. Excel is the least
            // forgiving of the three about leaked references: this is the one that
            // actually survived, intermittently, when the collection was left anonymous.
            books = app.Workbooks;
            book = books.Open(source, ReadOnly: true, AddToMru: false);

            // 0 = xlTypePDF.
            book.ExportAsFixedFormat(0, target);

            return Describe(target, launched, "Excel");
        }
        finally
        {
            Com.Release(books);
            Close(book, app, launched);
        }
    }

    private static string ExportPowerPoint(string source, string target)
    {
        dynamic? app = Com.TryGetActive("PowerPoint.Application");
        bool launched = app is null;

        app ??= Com.GetOrCreate("PowerPoint.Application");

        dynamic? presentations = null;
        dynamic? presentation = null;

        try
        {
            presentations = app.Presentations;

            // PowerPoint refuses Visible = false: setting it throws, unlike Word and
            // Excel. WithWindow:false on Open is the supported way to keep it off screen.
            presentation = presentations.Open(
                source, ReadOnly: true, Untitled: false, WithWindow: false);

            // 2 = ppFixedFormatTypePDF.
            presentation.ExportAsFixedFormat(target, 2);

            return Describe(target, launched, "PowerPoint");
        }
        finally
        {
            Com.Release(presentations);
            Close(presentation, app, launched);
        }
    }

    private static string Describe(string target, bool launched, string application)
    {
        long size = File.Exists(target) ? new FileInfo(target).Length : 0;

        return $"Exported to {target} ({size / 1024} KB) using {application}"
            + (launched ? ", which was started and closed again." : ", which was already open.");
    }

    /// <summary>
    /// Close the document and, if we started the application, quit it.
    ///
    /// Every step is individually guarded. A close that throws must not prevent the quit,
    /// because the quit is what stops a process surviving -- and an application we
    /// attached to is never quit, since it belongs to the user.
    /// </summary>
    private static void Close(dynamic? document, dynamic? app, bool launched)
    {
        if (document is not null)
        {
            try
            {
                // SaveChanges:false explicitly. An export must never write to the source,
                // and a modal "do you want to save?" in an invisible instance is an
                // unkillable hang.
                document.Close(SaveChanges: false);
            }
            catch (Exception)
            {
            }

            Com.Release(document);
        }

        if (app is null)
            return;

        if (launched)
        {
            try
            {
                app.Quit();
            }
            catch (Exception)
            {
            }
        }

        Com.Release(app);
    }

    /// <summary>Whether any of the three applications is installed at all.</summary>
    public static bool IsAvailable =>
        Applications.Any(a => Com.IsAvailable(a.ProgId));

    private static (string ProgId, string Name) Resolve(string application)
    {
        string wanted = application.Trim().ToLowerInvariant();

        return wanted switch
        {
            "word" or "winword" or "doc" or "docx" => ("Word.Application", "Word"),
            "excel" or "xls" or "xlsx" => ("Excel.Application", "Excel"),
            "powerpoint" or "ppt" or "pptx" => ("PowerPoint.Application", "PowerPoint"),
            _ => throw new ArgumentException(
                $"'{application}' is not one of word, excel or powerpoint."),
        };
    }
}
