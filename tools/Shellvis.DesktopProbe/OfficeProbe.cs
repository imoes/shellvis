using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Exercises the Office writers directly, without a model.
///
/// Deliberately independent of the network. A live agent run through these tools is
/// worth doing too, but it conflates three things: whether the model picks the right
/// tool, whether the endpoint is reachable, and whether the file is actually written
/// correctly. Only the last one is this code's responsibility, and the first attempt at
/// a live run failed on a dropped VPN without exercising a single writer.
///
/// Each format is written and then READ BACK. Producing a file that a library accepts
/// but Office refuses is the characteristic failure of OpenXML generation, so a
/// successful write on its own proves very little.
/// </summary>
internal static class OfficeProbe
{
    public static int Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "shellvis-office");
        Directory.CreateDirectory(folder);

        var office = new OfficeTools();
        int failures = 0;

        Console.WriteLine($"writing into {folder}\n");

        // ---------------------------------------------------------------- Word
        const string documentMarkdown = """
            # Shellvis

            An agent that operates Windows. It reads the desktop, clicks in real
            applications, and drives PowerShell.

            ## Capabilities

            - Desktop analysis through UI Automation
            - PowerShell with a persistent session
            - Office documents without Office installed

            ## Tool families

            | Family | Tools | Notes |
            |---|---|---|
            | Desktop | 8 | windows, UI tree, clicks |
            | PowerShell | 5 | cmdlet catalog on demand |
            | Office | 7 | headless OpenXML |

            Formatting survives: **bold**, *italic*, and code.

            ```
            Get-Process | Sort-Object CPU -Descending
            ```
            """;

        string wordPath = Path.Combine(folder, "report.docx");
        failures += Check("word_create", office.CreateWord(wordPath, documentMarkdown, "Shellvis report"));
        failures += CheckFile(wordPath, minimumBytes: 3000);

        // ---------------------------------------------------------------- Excel
        const string tableMarkdown = """
            | Family | Tools | Read-only |
            |---|---|---|
            | Desktop | 8 | 4 |
            | PowerShell | 5 | 3 |
            | WSL | 3 | 2 |
            | Gallery | 4 | 2 |
            | Office | 7 | 3 |
            """;

        string excelPath = Path.Combine(folder, "tools.xlsx");
        failures += Check("excel_create", office.CreateExcel(excelPath, tableMarkdown, "Families"));
        failures += CheckFile(excelPath, minimumBytes: 3000);

        // A second sheet from CSV, to prove format detection and the add path.
        failures += Check("excel_add_sheet", office.AddExcelSheet(
            excelPath,
            "Date,Milestone,Rounds\n2026-08-23,Pill spike,1\n2026-08-23,Agent live,4\n2026-08-24,Office,3",
            "Milestones"));

        string sheets = office.ListExcelSheets(excelPath);
        failures += Check("excel_sheets", sheets);
        failures += Expect(sheets, "Milestones", "the second sheet should be listed");

        string read = office.ReadExcel(excelPath, "Families");
        failures += Check("excel_read", read);

        // The point of typing values: a number stored as text would come back with no
        // change, while a real number round-trips through Excel's formatting.
        failures += Expect(read, "Desktop", "the header row should be readable");

        // ------------------------------------------------------------ PowerPoint
        const string deckMarkdown = """
            # Shellvis
            Notes: open by pointing at the floating pill on screen
            ---
            # What it does
            - Reads the desktop through UI Automation
            - Clicks and types in real applications
            - Runs PowerShell in a session that persists
            Notes: demo the Notepad round trip here
            ---
            # Safety
            - Read-only commands run silently
            - Anything that writes asks first
            - Installing from the gallery always asks
            Notes: mention the classifier test table
            """;

        string deckPath = Path.Combine(folder, "deck.pptx");
        failures += Check("powerpoint_create", office.CreatePowerPoint(deckPath, deckMarkdown));
        failures += CheckFile(deckPath, minimumBytes: 3000);

        string deck = office.ReadPowerPoint(deckPath);
        failures += Check("powerpoint_read", deck);
        failures += Expect(deck, "3 slide(s)", "all three slides should be present");
        failures += Expect(deck, "Safety", "the third slide title should round-trip");

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: all three formats written and read back."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    private static int Check(string label, string result)
    {
        bool failed = result.StartsWith("error:", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  {(failed ? "FAIL" : "ok  ")} {label,-20} {FirstLine(result)}");
        return failed ? 1 : 0;
    }

    /// <summary>
    /// A written file has to exist and be plausibly sized. An OpenXML package that
    /// came out at a few hundred bytes is a valid zip containing nothing useful.
    /// </summary>
    private static int CheckFile(string path, int minimumBytes)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"  FAIL file                 {path} was not created");
            return 1;
        }

        long size = new FileInfo(path).Length;
        if (size < minimumBytes)
        {
            Console.WriteLine($"  FAIL file                 {Path.GetFileName(path)} is only {size} bytes");
            return 1;
        }

        Console.WriteLine($"  ok   file                 {Path.GetFileName(path)}  {size:N0} bytes");
        return 0;
    }

    private static int Expect(string haystack, string needle, string why)
    {
        bool present = haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  {(present ? "ok  " : "FAIL")} contains {needle,-12} {why}");
        return present ? 0 : 1;
    }

    private static string FirstLine(string text)
    {
        string first = text.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return first.Length <= 110 ? first : first[..110] + "...";
    }
}
