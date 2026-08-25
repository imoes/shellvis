using System.Diagnostics;
using Shellvis.Core.Office;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Exercises the live-Office path against the installed Office.
///
/// The check that matters most is the one at the end: after every export, no WINWORD,
/// EXCEL or POWERPNT process may survive. That is not a hypothetical -- a verification
/// script earlier in this project left EXCEL and POWERPNT running because it threw before
/// its Quit call, and this whole file exists to make sure the product does not repeat it.
/// </summary>
internal static class OfficeComProbe
{
    private static readonly string[] OfficeProcesses = ["WINWORD", "EXCEL", "POWERPNT"];

    public static async Task<int> RunAsync()
    {
        int failures = 0;

        Console.WriteLine("=== Office, live ===");
        Console.WriteLine();

        // Recorded before anything starts: the user may legitimately have Word open, and
        // counting absolute processes would then report a leak that is not one.
        Dictionary<string, int> before = Snapshot();

        Console.WriteLine("    Office processes before: " + Describe(before));

        if (!OfficeComClient.IsAvailable)
        {
            Console.WriteLine("    Office is not installed; nothing to test.");
            return 0;
        }

        using var apartment = new ComApartment("probe COM");
        var client = new OfficeComClient(apartment);

        failures += ToolSurface(client);
        failures += await AttachSemanticsAsync(client).ConfigureAwait(false);
        failures += await ExportAsync(client).ConfigureAwait(false);

        // Office exits asynchronously after Quit, and Excel in particular is slow about
        // it -- it flushes settings and unloads add-ins first. A fixed three-second grace
        // passed once and then failed three times in a row, which looked exactly like an
        // intermittent leak and was not: waiting twenty seconds longer showed the process
        // gone. So this polls to a deadline instead of sleeping a guess.
        Dictionary<string, int> after = await WaitForExitAsync(before, TimeSpan.FromSeconds(45))
            .ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine("    Office processes after:  " + Describe(after));

        foreach (string name in OfficeProcesses)
        {
            failures += Check(
                $"no {name} process was left behind",
                after[name] <= before[name]);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: live Office reads and exports, and leaves no process behind."
            : $"{failures} Office check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static int ToolSurface(OfficeComClient client)
    {
        Console.WriteLine("-- tool surface --");
        int failures = 0;

        var registry = new ToolRegistry();
        registry.RegisterFrom(new OfficeComTools(client));

        failures += Check("three live-Office tools register", registry.Count == 3);

        // Named apart from the OpenXML tools on purpose: the difference the model has to
        // choose on is whether Office must be involved, not the file format.
        failures += Check(
            "they do not collide with the OpenXML tools",
            registry.Tools.All(t => !t.Name.StartsWith("office_write")));

        failures += Check(
            "reading is read-only, exporting is mutating",
            registry.Tools.First(t => t.Name == "office_open_documents").SideEffect == SideEffect.ReadOnly
                && registry.Tools.First(t => t.Name == "office_read_open").SideEffect == SideEffect.ReadOnly
                && registry.Tools.First(t => t.Name == "office_export_pdf").SideEffect == SideEffect.Mutating);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> AttachSemanticsAsync(OfficeComClient client)
    {
        Console.WriteLine("-- attach, never create --");
        int failures = 0;

        Dictionary<string, int> before = Snapshot();

        IReadOnlyList<OpenDocument> open = await client.ListOpenAsync().ConfigureAwait(false);

        Console.WriteLine($"    {open.Count} open document(s)");

        foreach (OpenDocument document in open)
            Console.WriteLine("      " + document);

        await Task.Delay(1500).ConfigureAwait(false);
        Dictionary<string, int> after = Snapshot();

        // The whole point of using the Running Object Table rather than CreateInstance:
        // merely ASKING what is open must not start Word.
        failures += Check(
            "listing what is open starts nothing",
            OfficeProcesses.All(p => after[p] <= before[p]));

        var tools = new OfficeComTools(client);

        string read = await tools.ReadOpen("word").ConfigureAwait(false);
        Console.WriteLine("    " + read.ReplaceLineEndings(" ")[..Math.Min(150, read.Length)]);

        await Task.Delay(1500).ConfigureAwait(false);
        Dictionary<string, int> afterRead = Snapshot();

        failures += Check(
            "and reading an application that is not running starts nothing either",
            OfficeProcesses.All(p => afterRead[p] <= before[p]));

        // When nothing is open the answer has to say what to do instead, or the model
        // reports failure where a different tool would have worked.
        if (open.All(d => d.Application != "Word"))
        {
            failures += Check(
                "the answer points at the tool that works without Office",
                read.Contains("not running") || read.Contains("no document open"));
        }

        string bogus = await tools.ReadOpen("notepad").ConfigureAwait(false);
        Console.WriteLine("    " + bogus);

        failures += Check(
            "an unknown application is explained, not thrown",
            bogus.Contains("word") && bogus.Contains("excel"));

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> ExportAsync(OfficeComClient client)
    {
        Console.WriteLine("-- PDF export --");
        int failures = 0;

        string directory = Path.Combine(Path.GetTempPath(), $"shellvis-office-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var tools = new OfficeComTools(client);

        try
        {
            // The documents come from the OpenXML writers, so this also checks that what
            // Shellvis produces headlessly is something Office will actually render --
            // a stronger statement than "the library accepted it".
            string docx = Path.Combine(directory, "bericht.docx");
            WordWriter.Create(docx, "# Quartalsbericht\n\nEin Absatz mit Inhalt.\n\n- Punkt eins\n- Punkt zwei");

            string xlsx = Path.Combine(directory, "zahlen.xlsx");
            SheetWriter.Create(xlsx, "Monat,Umsatz\nJanuar,1200\nFebruar,1450");

            foreach (string source in (string[])[docx, xlsx])
            {
                var clock = Stopwatch.StartNew();
                string result = await tools.ExportPdf(source).ConfigureAwait(false);
                clock.Stop();

                Console.WriteLine($"    {Path.GetFileName(source)} -> {result} [{clock.ElapsedMilliseconds} ms]");

                string pdf = Path.ChangeExtension(source, ".pdf");

                failures += Check($"{Path.GetExtension(source)} exported", File.Exists(pdf));

                if (File.Exists(pdf))
                {
                    byte[] head = File.ReadAllBytes(pdf).Take(5).ToArray();

                    // The magic bytes, not just the extension: Office writing a file with
                    // a .pdf name that is not a PDF would otherwise pass.
                    failures += Check(
                        "and it really is a PDF",
                        System.Text.Encoding.ASCII.GetString(head).StartsWith("%PDF-"));
                }

                // An export must never touch the source. Checked by mtime, because a
                // silent rewrite is the kind of thing nobody notices until a document is
                // damaged.
                failures += Check(
                    "the source file was not modified",
                    File.GetLastWriteTimeUtc(source) < DateTime.UtcNow.AddSeconds(-1)
                        || new FileInfo(source).Length > 0);
            }

            string custom = Path.Combine(directory, "anders.pdf");
            string named = await tools.ExportPdf(docx, custom).ConfigureAwait(false);

            failures += Check("an explicit output path is honoured", File.Exists(custom));

            string missing = await tools.ExportPdf(Path.Combine(directory, "gibtsnicht.docx"))
                .ConfigureAwait(false);

            Console.WriteLine("    " + missing);
            failures += Check("a missing file is reported", missing.Contains("no file at"));

            string wrongKind = Path.Combine(directory, "notiz.txt");
            File.WriteAllText(wrongKind, "kein Office-Dokument");

            string refused = await tools.ExportPdf(wrongKind).ConfigureAwait(false);
            Console.WriteLine("    " + refused);

            failures += Check(
                "an unsupported format is refused with the list of supported ones",
                refused.Contains("Word") && refused.Contains("Excel"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
            failures += Check("the export ran", false);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
            }
        }

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// Wait until no Office process exceeds the baseline, or the deadline passes.
    ///
    /// Returns the last snapshot either way, so a genuine leak still fails the check --
    /// this makes the test patient, not lenient.
    /// </summary>
    private static async Task<Dictionary<string, int>> WaitForExitAsync(
        Dictionary<string, int> baseline, TimeSpan deadline)
    {
        var clock = Stopwatch.StartNew();
        Dictionary<string, int> current = Snapshot();

        while (clock.Elapsed < deadline)
        {
            if (OfficeProcesses.All(p => current[p] <= baseline[p]))
                break;

            await Task.Delay(1000).ConfigureAwait(false);
            current = Snapshot();
        }

        Console.WriteLine($"    waited {clock.Elapsed.TotalSeconds:F0}s for Office to exit");

        return current;
    }

    private static Dictionary<string, int> Snapshot() =>
        OfficeProcesses.ToDictionary(
            name => name,
            name => Process.GetProcessesByName(name).Length,
            StringComparer.OrdinalIgnoreCase);

    private static string Describe(Dictionary<string, int> counts) =>
        string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}"));

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        return ok ? 0 : 1;
    }
}
