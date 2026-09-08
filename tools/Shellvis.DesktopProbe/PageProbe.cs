using System.Text.RegularExpressions;

using Shellvis.Core.Desk;
using Shellvis.Core.Office;

namespace Shellvis.DesktopProbe;

/// <summary>
/// The reference page ships, and it ships without reaching the network.
///
/// <b>Why a harness for one HTML file.</b> It is carried as an embedded resource, and the
/// two ways that goes wrong are both silent. Drop the line from the project file and the
/// window opens with "The page is missing from this build" -- correct behaviour for a
/// missing resource, and invisible until somebody presses the button. Rename a section and
/// nothing anywhere notices; the page is not code and no compiler reads it.
///
/// <b>And the part that matters more than either.</b> The same page is published on the web,
/// where it links two typefaces from a font host. In the window that link is stripped,
/// because opening a local reference page inside an assistant that keeps everything on this
/// machine must not send a request to anybody. That strip is one regular expression against
/// a file nobody compiles -- exactly the arrangement that rots. So the regex is applied here
/// too, to the real file, and what comes out must contain no http reference at all.
/// </summary>
internal static class PageProbe
{
    /// <summary>The strip, kept identical to <c>VorzimmerWindow.Page</c> by this check.</summary>
    private const string LinkPattern = @"<link\b[^>]*href\s*=\s*""https?:[^""]*""[^>]*>";

    public static int Run()
    {
        int failures = 0;

        void Check(string what, bool passed, string detail = "")
        {
            if (!passed)
                failures++;

            Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        Console.WriteLine("page: the reference page ships, and it stays on this machine\n");

        string? root = FindRepositoryRoot();

        if (root is null)
        {
            Console.WriteLine("  ..   not running from the repository, so the sources cannot be read.");
            Console.WriteLine("       This check is a source check and is skipped.");
            return 0;
        }

        string page = Path.Combine(root, "src", "Shellvis.Shell", "Assets", "vorzimmer.html");
        string project = Path.Combine(root, "src", "Shellvis.Shell", "Shellvis.Shell.csproj");
        string view = Path.Combine(root, "src", "Shellvis.Shell", "Views", "VorzimmerWindow.xaml.cs");

        if (!File.Exists(page) || !File.Exists(project) || !File.Exists(view))
        {
            Check("the page, the project and the view were found", false,
                $"page={File.Exists(page)}, project={File.Exists(project)}, view={File.Exists(view)}");

            return failures;
        }

        string html = File.ReadAllText(page);

        // ------------------------------------------------------------------ it ships
        Console.WriteLine("-- it is in the build --");

        Check("the page is listed as an embedded resource",
            File.ReadAllText(project).Contains(@"<EmbeddedResource Include=""Assets\vorzimmer.html"" />", StringComparison.Ordinal),
            "without this line the window opens empty and says so");

        Check("it is a fragment, not a whole document",
            !html.Contains("<!doctype", StringComparison.OrdinalIgnoreCase)
                && !html.Contains("<html", StringComparison.OrdinalIgnoreCase),
            "the skeleton is supplied by the window and by the artifact host, once each");

        Check("it carries its own title", html.Contains("<title>Das Vorzimmer</title>", StringComparison.Ordinal));

        // ------------------------------------------------------------- nothing leaves
        Console.WriteLine("\n-- nothing leaves the machine --");

        // Compared as SOURCE text, so the quotes have to be doubled back into the verbatim
        // form the view is written in. Comparing the two runtime values would be no check at
        // all: this file would only be agreeing with itself.
        Check("the view strips external links with this exact pattern",
            File.ReadAllText(view).Contains(
                LinkPattern.Replace("\"", "\"\"", StringComparison.Ordinal),
                StringComparison.Ordinal),
            "the pattern here and the pattern there have to be the same one");

        string stripped = Regex.Replace(html, LinkPattern, string.Empty, RegexOptions.IgnoreCase);

        Check("after the strip no http reference survives",
            !stripped.Contains("http:", StringComparison.OrdinalIgnoreCase)
                && !stripped.Contains("https:", StringComparison.OrdinalIgnoreCase),
            Surviving(stripped));

        Check("and the page still declares fallback faces for every role",
            stripped.Contains("Georgia", StringComparison.Ordinal)
                && stripped.Contains("Segoe UI", StringComparison.Ordinal)
                && stripped.Contains("Consolas", StringComparison.Ordinal),
            "with the webfonts gone these are what actually renders");

        // --------------------------------------------------- the rules SHAPE it
        //
        // They used to be printed on it: a lede, six cards with their sources, a worked
        // morning, a list of what code decides. All of it true, none of it the desk -- and
        // the report, five times over, was that the page was full of placeholder text. The
        // rules are meant to SHAPE the page; a page that states them has put a description
        // where the thing should be. So these checks are inverted.
        Console.WriteLine("\n-- the rules shaped it rather than being listed on it --");

        foreach ((string what, string gone) in new[]
        {
            ("no lede explaining the purpose", "Der Auftrag ist nicht"),
            ("no card per professional rule", "Was der Beruf tatsächlich verlangt"),
            ("no worked morning", "Ein Morgen, durchgearbeitet"),
            ("no list of what code decides", "Was Code entscheidet"),
            ("no bibliography", "Woher die Regeln kommen"),
            ("no stated boundary, because a missing button says it", "Es wird nichts gesendet"),
            ("and nothing merely folded away either", "<details"),
        })
        {
            Check(what, !html.Contains(gone, StringComparison.Ordinal), gone);
        }

        // And what the rules produced instead, which is now the whole page.
        Console.WriteLine();

        foreach ((string rule, string shape) in new[]
        {
            ("sorted before anything is said: three trays", "class=\"trays\""),
            ("a handful rather than thirty: a list per tray", "data-list="),
            ("the rest behind a count", "data-count=\"ignore\""),
            ("an empty tray says so in words", "nichts davon"),
            ("look ahead: what is left today", "data-count=\"today\""),
            ("nothing dropped: what is overdue", "data-count=\"overdue\""),
        })
        {
            Check(rule, html.Contains(shape, StringComparison.Ordinal), shape);
        }

        // The page is the visible half of files the model reads. If one of those goes, the
        // page becomes a description of something that is no longer there.
        Console.WriteLine("\n-- and the files behind it still exist --");

        foreach (string path in new[]
        {
            Path.Combine("skills", "assistant", "secretary", "SKILL.md"),
            Path.Combine("skills", "assistant", "daily-briefing", "SKILL.md"),
            Path.Combine("src", "Shellvis.Core", "Office", "MailboxWatch.cs"),
        })
        {
            Check(path, File.Exists(Path.Combine(root, path)));
        }

        // ------------------------------------------------------------- the numbers
        //
        // The page and the snapshot agree on a set of keys, and neither side compiles
        // against the other. A renamed key does not break a build: it leaves a box on
        // screen showing a dash for ever, next to boxes that filled in correctly.
        Console.WriteLine("\n-- the counts on the page and the counts in the code --");

        // Two sources now, and the split is the point of the whole change. The snapshot
        // counts what a machine can count; the tally counts what the MODEL decided. A page
        // key has to come from one of them, or its box can only ever show a dash.
        var known = DeskSnapshot.Nothing.Counts.Keys
            .Concat(["answer", "information", "ignore", "pending"])
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> onPage = Regex.Matches(html, @"data-(?:count|badge)=""(?<key>[a-z]+)""")
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string key in onPage.OrderBy(k => k, StringComparer.Ordinal))
        {
            Check($"the page's '{key}' is a count something produces", known.Contains(key),
                known.Contains(key) ? string.Empty : "this box can only ever show a dash");
        }

        foreach (string key in new[] { "answer", "information", "ignore", "pending" })
        {
            Check($"the verdict '{key}' is shown", onPage.Contains(key),
                onPage.Contains(key) ? string.Empty : "the model judged it and nothing displays it");
        }

        // The sender split is GONE, and this is the check that keeps it gone. Sorting mail
        // by whether the address looked like a machine is what put a broadcast under
        // "braucht heute eine Antwort" -- reported as mail that needs no answer sitting in
        // the tray that means it does.
        foreach (string wrong in new[] { "people", "automated" })
        {
            Check($"the page no longer sorts by sender ('{wrong}')", !onPage.Contains(wrong),
                "who sent it is a fact about the envelope, not an answer to what it needs");
        }

        Check("no box is authored with a zero in it",
            !Regex.IsMatch(html, @"data-count=""[a-z]+"">0<"),
            "a zero claims the mailbox is empty; a dash says it was not measured");

        Check("the page admits what has not been judged yet",
            html.Contains("Noch unsortiert", StringComparison.Ordinal),
            "judging costs a model call each, so a busy morning arrives faster than it is read");

        Check("there is somewhere for the update notice to appear",
            html.Contains(@"id=""refreshed""", StringComparison.Ordinal));

        // ------------------------------------------------------------------ what is new
        Console.WriteLine("\n-- a badge means grown, not merely different --");

        var before = new DeskSnapshot(
            Unread: 80, FromPeople: 49, Automated: 31, MeetingRequests: 0, TicketMail: 31,
            AppointmentsToday: 2, NextAppointment: null, OverdueTasks: 1, Scanned: 80,
            TakenAt: new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Unspecified));

        var after = before with
        {
            Unread = 89,
            FromPeople = 53,
            AppointmentsToday = 0,
            OverdueTasks = 0,
            TakenAt = before.TakenAt.AddHours(2),
        };

        IReadOnlyDictionary<string, int> grown = after.NewSince(before);

        Check("what grew is reported as the increase",
            grown.TryGetValue("unread", out int unread) && unread == 9,
            $"unread: +{(grown.TryGetValue("unread", out int u) ? u : 0)}, expected +9");

        Check("and so is the second one",
            grown.TryGetValue("people", out int people) && people == 4);

        Check("what fell is NOT reported",
            !grown.ContainsKey("today") && !grown.ContainsKey("overdue"),
            "a badge that appears when you tidy up is a badge nobody believes twice");

        Check("what did not move is not reported",
            !grown.ContainsKey("automated") && !grown.ContainsKey("tickets"));

        Check("with nothing to compare against, nothing is new",
            after.NewSince(null).Count == 0,
            "a first look badging everything is the mistake the watcher refuses to make");

        Check("and an unmeasured baseline counts as nothing to compare against",
            after.NewSince(DeskSnapshot.Nothing).Count == 0);

        // ------------------------------------------------------- report, not control
        //
        // The remembering period is a SETTING and belongs in the settings. It was a slider
        // on this page first, and that was the wrong home: this page reports what is on the
        // desk, and a control among figures that report it makes a reader wonder which of
        // the numbers they can also drag.
        //
        // The checks below are the ones that keep it that way, and one of them is about
        // wording: while the slider lived here, the phrase for a period existed twice --
        // once in C# and once in JavaScript, so the label could follow the thumb without a
        // round trip. It now exists once, and this pins that.
        Console.WriteLine("\n-- the period is reported here and set elsewhere --");

        Check("the page carries no slider",
            !html.Contains(@"type=""range""", StringComparison.Ordinal),
            "the control lives in the settings form");

        Check("it states the period, and what the period governs",
            html.Contains(@"id=""remembering""", StringComparison.Ordinal)
                && html.Contains("Sortiert wird alles Ungelesene", StringComparison.Ordinal),
            "the unread count is the whole folder and the verdicts cover the period; "
                + "side by side without a word they read as a contradiction");

        Check("and says where it is changed",
            html.Contains("in den Einstellungen", StringComparison.Ordinal),
            "a value shown with no way to reach its control is a dead end");

        // Taken from DeskWindow rather than written out, so the check follows a change to
        // the wording instead of quietly passing against a phrase that no longer exists.
        Check("the wording of a period exists only in the code",
            !html.Contains(new DeskWindow(DeskWindow.Most).Describe(), StringComparison.Ordinal)
                && !html.Contains(new DeskWindow(14).Describe(), StringComparison.Ordinal),
            "the page receives the phrase already formed, so the two cannot disagree");

        // The phrases go into sentences the page builds -- "Auf dem Tisch liegen ..." -- and
        // one in the wrong case makes that sentence ungrammatical. It said "liegt den
        // letzten Monat" until this was fixed.
        Check("every period reads as a plural, whatever the sentence around it",
            Enumerable
                .Range(DeskWindow.Least, DeskWindow.Most - DeskWindow.Least + 1)
                .All(days => new DeskWindow(days).Describe()
                    .StartsWith("die letzten ", StringComparison.Ordinal)),
            "so 'auf dem Tisch liegen X' and \"neulich heißt X\" both work");

        Check("the period the settings offer is the period the store keeps",
            DeskWindow.Most <= DeskStore.DefaultRetention.TotalDays,
            $"slider to {DeskWindow.Most} days, kept for {DeskStore.DefaultRetention.TotalDays:F0}");

        // ---------------------------------------------------------- the real entries
        Console.WriteLine("\n-- the trays show real entries, and ship none --");

        foreach (string key in new[] { "answer", "information" })
        {
            Check($"there is a list for '{key}'",
                html.Contains($@"data-list=""{key}""", StringComparison.Ordinal));
        }

        Check("both lists are empty in the file",
            !Regex.IsMatch(html, @"data-list=""[a-z]+""[^>]*>\s*<li(?![^>]*class=""(waiting|empty)"")"),
            "this page is also published on the web; nobody's inbox belongs in a file");

        Check("a list says it is waiting rather than showing nothing",
            html.Contains("noch nicht sortiert", StringComparison.Ordinal));

        // Fresh first, older only when nothing fresh is waiting.
        //
        // Both alternatives were shipped and both were reported. Unbounded, the trays
        // filled with month-old alerts whose reasons paraphrased their subject lines.
        // Bounded hard, the summaries were finally worth reading and could not be seen at
        // all: two rows inside a fortnight against fifty-three in the store. So the rows
        // arrive newest first over everything held, each knowing whether it is inside the
        // period, and the page draws a line instead of dropping any.
        Check("older rows are marked rather than withheld",
            html.Contains("row.old", StringComparison.Ordinal)
                && html.Contains("older-line", StringComparison.Ordinal),
            "a tray that stands empty while summarised mail waits is the defect this "
                + "replaced, and so is a tray full of month-old alerts");

        // Two literals and the distance between them, not a regex. A pattern that has to
        // escape a quote, a plus and a backslash to match a line of JavaScript is a pattern
        // that fails for its own reasons -- this one did, on its first run.
        // The class ASSIGNMENT, not the CSS rule for it. The first occurrence of the bare
        // name is the stylesheet a few hundred lines earlier, which put the two literals
        // half a file apart and failed a distance check that was measuring the wrong pair.
        int lineAt = html.IndexOf("className = \"older-line\"", StringComparison.Ordinal);
        int daysAt = html.IndexOf("\"den letzten \" + days", StringComparison.Ordinal);

        Check("and the line says how far back the period reached",
            lineAt >= 0 && daysAt > lineAt && daysAt - lineAt < 600,
            "\"älter:\" with no number is a line nobody can check against the setting");

        Check("entries are built as text, not as markup",
            html.Contains("textContent", StringComparison.Ordinal)
                && !Regex.IsMatch(html, @"innerHTML\s*="),
            "a subject line with a bracket in it must be shown, not interpreted");

        // The tray heading is now TRUE rather than aspirational: the model decided that
        // these need an answer, so the heading says so and needs no label underneath
        // explaining that the list below it is something else.
        Check("the trays are named by the verdict they hold",
            html.Contains("Braucht eine Antwort", StringComparison.Ordinal)
                && html.Contains("Muss man wissen", StringComparison.Ordinal)
                && html.Contains("Nicht lesenswert", StringComparison.Ordinal));

        // The summary is what Shellvis adds; the subject belongs to Outlook. It was the
        // last line of the row at two thirds of caption size in the faintest grey -- 13
        // pixels tall -- and it was reported as missing. The order in the markup is the
        // reading order, so it is checked as such.
        Check("the model's summary is on the row",
            html.Contains("summary.className", StringComparison.Ordinal)
                && html.Contains("row.why", StringComparison.Ordinal),
            "a verdict nobody can see the reasoning for is one nobody can correct");

        Check("and it comes before the subject, not after it",
            html.IndexOf("summary.className", StringComparison.Ordinal)
                < html.IndexOf("what.className", StringComparison.Ordinal),
            "the sentence Shellvis wrote is the line to read; the subject is provenance");

        Check("the summary is set in reading size, not as a whisper",
            Regex.IsMatch(html, @"\.summary \{[^}]*font-size: var\(--step-0\)"),
            "0.66rem in the faintest grey is how it came to be reported as absent");

        // -------------------------------------------------------- every mail opens
        Console.WriteLine();

        Check("a row is a button, so it can be pressed and reached by keyboard",
            html.Contains('"' + "button.row" + '"', StringComparison.Ordinal)
                && html.Contains("createElement(\"button\")", StringComparison.Ordinal));

        Check("pressing one asks the host to open it",
            html.Contains("\"open:\" + id", StringComparison.Ordinal));

        Check("and it carries the DESK id, not an Outlook handle",
            html.Contains("data-id", StringComparison.Ordinal)
                && !html.Contains("entryId", StringComparison.OrdinalIgnoreCase),
            "a web view has no business holding a live handle into a mailbox");


        // The distinction moved with the control. Checked in the settings source rather
        // than in the page, because that is where the sentence now has to be: keeping a
        // quarter and consulting a quarter are different things, and a slider offered
        // without saying which one it moves invites somebody to drag it expecting the
        // other.
        string settings = Path.Combine(
            root, "src", "Shellvis.Shell", "Views", "PillWindow.Vorzimmer.cs");

        Check("the settings form says what the period does NOT change",
            File.Exists(settings)
                && File.ReadAllText(settings)
                    .Contains("does not change what is kept", StringComparison.Ordinal),
            "keeping three months and consulting three months are different things");

        // ------------------------------------------------- every tool it names is real
        //
        // The page names the tools each rule is kept with. A tool that has since been
        // renamed leaves a name on a reference page that resolves to nothing, and nothing
        // else in this repository would notice: the page is markup, and the name is prose.
        Console.WriteLine("\n-- every tool the page names exists --");

        string[] named = Regex.Matches(html, @"\b(?<tool>[a-z]+_[a-z_]+)\b")
            .Select(m => m.Groups["tool"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(t => !t.StartsWith("data_", StringComparison.Ordinal))
            .ToArray();

        string sources = string.Concat(
            Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(File.ReadAllText));

        string manifests = Directory.Exists(Path.Combine(root, "connectors"))
            ? string.Concat(
                Directory.EnumerateFiles(Path.Combine(root, "connectors"), "*.yaml", SearchOption.AllDirectories)
                    .Select(File.ReadAllText))
            : string.Empty;

        foreach (string tool in named)
        {
            // A built-in declares itself in an attribute; a connector tool declares itself
            // in a manifest. Both count, and nothing else does -- a name that merely appears
            // in a comment somewhere is not a tool.
            bool exists = sources.Contains($"\"{tool}\"", StringComparison.Ordinal)
                || manifests.Contains($"name: {tool}", StringComparison.Ordinal);

            Check($"{tool} is a tool that exists", exists,
                exists ? string.Empty : "named on the page and registered nowhere");
        }

        // ------------------------------------------------------------------- the theme
        Console.WriteLine("\n-- both themes are designed, not one --");

        Check("the light palette is on bare :root",
            Regex.IsMatch(html, @":root\s*\{[^}]*--paper:", RegexOptions.Singleline),
            "a colour defined only behind a media query never applies in the default state");

        Check("dark is guarded so an explicit light choice still wins",
            html.Contains(@":root:not([data-theme=""light""])", StringComparison.Ordinal));

        Check("and the stamp wins in the other direction too",
            html.Contains(@":root[data-theme=""dark""]", StringComparison.Ordinal),
            "this is the one the window sets from the application's theme");

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: the page is in the build as a fragment, strips every external\n"
              + "reference before it renders, still names a fallback face for each role,\n"
              + "is shaped by the rules instead of reciting them, and designs both themes.\n"
              + "\nNOT covered here: whether it LOOKS right, which needs eyes."
            : $"\n{failures} check(s) failed.");

        return failures;
    }

    /// <summary>What is still reaching out, so a failure names the line rather than the fact.</summary>
    private static string Surviving(string stripped)
    {
        MatchCollection found = Regex.Matches(stripped, @"https?:[^""'\s<>]{0,80}", RegexOptions.IgnoreCase);

        return found.Count == 0
            ? string.Empty
            : string.Join(", ", found.Select(m => m.Value).Distinct().Take(4));
    }

    private static string? FindRepositoryRoot()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null && !File.Exists(Path.Combine(here.FullName, "Shellvis.slnx")))
            here = here.Parent;

        return here?.FullName;
    }
}
