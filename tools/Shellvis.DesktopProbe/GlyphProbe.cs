using System.Text.RegularExpressions;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Every icon button has something drawn on it.
///
/// <b>Why this is a harness and not a code review.</b> Three glyph constants in the history
/// panel were empty strings. The buttons existed, laid out, took the pointer and worked
/// perfectly -- there was simply nothing painted on them. Nothing threw, no test failed, and
/// the automation that drives this application by element id never noticed, because an
/// invisible button is a perfectly good automation target. What it is not is a button a
/// person can find, and the report that arrived was "where is the delete button" together
/// with "you cannot open a chat by clicking": one cause, two symptoms.
///
/// The glyphs are private-use characters. They cannot be typed, cannot be read back from a
/// diff, and disappear silently when a file passes through a tool that does not preserve
/// them -- which is what happened. Written as escapes they are plain ASCII in the source and
/// this checks that they stay that way.
/// </summary>
internal static class GlyphProbe
{
    public static int Run()
    {
        int failures = 0;

        void Check(string what, bool passed, string detail = "")
        {
            if (!passed)
                failures++;

            Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        Console.WriteLine("glyphs: every icon button has something on it\n");

        string? root = FindRepositoryRoot();

        if (root is null)
        {
            Console.WriteLine("  ..   not running from the repository, so the sources cannot be read.");
            Console.WriteLine("       This check is a source check and is skipped.");
            return 0;
        }

        var constant = new Regex(
            @"const\s+string\s+(?<name>Glyph\w+)\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.Compiled);

        int found = 0;

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            foreach (Match match in constant.Matches(text))
            {
                found++;

                string name = match.Groups["name"].Value;
                string value = match.Groups["value"].Value;

                // The literal in the SOURCE, before the compiler unescapes it. An empty
                // pair of quotes is the failure; "" is six characters here and one
                // at runtime, which is the point.
                Check($"{name} is not empty", value.Length > 0, Path.GetFileName(file));

                Check($"{name} is written as an escape",
                    value.StartsWith("\\u", StringComparison.Ordinal),
                    value.Length > 0 && !value.StartsWith("\\u", StringComparison.Ordinal)
                        ? "a literal character here is lost the next time a tool rewrites the file"
                        : string.Empty);
            }
        }

        Check("the glyph constants were found at all", found > 0, $"{found} found");

        XamlComments(root, Check);
        Shutdown(root, Check);
        RowSpacing(root, Check);

        Console.WriteLine(failures == 0
            ? $"\nVERIFIED: all {found} icon glyphs carry a character, written as an escape so\n"
                + "they survive a file rewrite. An invisible button is a working button nobody\n"
                + "can find, and nothing else in this suite can see one."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// No XAML comment contains a double dash.
    ///
    /// <b>Why a machine checks this.</b> It is not a style rule, it is XML: <c>--</c> may not
    /// appear inside a comment, so the XAML compiler refuses the file. That would be fine if
    /// it refused it legibly, but it reports <c>WMC9999 Xaml Internal Error</c> against a
    /// targets file in the SDK, names a line number in a file it does not name, and does not
    /// use the word "error" in the way a search for failures expects. This project has lost a
    /// build to it nine times, twice more looking for it in the wrong file, and four times in
    /// one sitting in the INSTALLER, which this check did not cover until then: wix reports it
    /// legibly, but only after building a 107 MB package.
    ///
    /// The comments here are prose, and prose written in this house style uses dashes. So the
    /// rule is checked in a second rather than remembered.
    /// </summary>
    private static void XamlComments(string root, Action<string, bool, string> check)
    {
        var comment = new Regex(@"<!--(?<body>.*?)-->", RegexOptions.Compiled | RegexOptions.Singleline);
        int files = 0;

        // The INSTALLER is scanned too, and it was added after the rule bit a fourth time in
        // one sitting. Only XAML was covered, so the same mistake in Shellvis.wxs was caught
        // by wix -- four times in a row, once per rebuild of a 107 MB package -- rather than
        // in a second by the harness. XML is XML.
        IEnumerable<string> Candidates() =>
            Directory.EnumerateFiles(Path.Combine(root, "src"), "*.xaml", SearchOption.AllDirectories)
                .Concat(Directory.Exists(Path.Combine(root, "install"))
                    ? Directory.EnumerateFiles(Path.Combine(root, "install"), "*.wxs", SearchOption.AllDirectories)
                    : []);

        foreach (string file in Candidates())
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            files++;
            string text = File.ReadAllText(file);

            foreach (Match match in comment.Matches(text))
            {
                string body = match.Groups["body"].Value;

                if (!body.Contains("--", StringComparison.Ordinal) && !body.EndsWith('-'))
                    continue;

                // The line number, because the compiler's own message points at a file in the
                // SDK and this is the whole reason the check exists.
                int line = text[..match.Index].Count(c => c == '\n') + 1;

                check(
                    $"{Path.GetFileName(file)} line {line}: a comment contains '--'",
                    false,

                    // Named per file type, because the point of this check is a legible
                    // message and pointing at the wrong compiler is how it stops being one.
                    file.EndsWith(".wxs", StringComparison.OrdinalIgnoreCase)
                        ? "XML forbids it; wix answers with WIX0104 after staging the whole payload"
                        : "XML forbids it; the XAML compiler answers with WMC9999 against an SDK targets file");
            }
        }

        check($"{files} XAML and installer file(s) have comments XML accepts", true, string.Empty);
    }

    /// <summary>
    /// Every step of the pill's shutdown is isolated.
    ///
    /// <b>The fault this guards against.</b> Closing the alert window was the one step in
    /// <c>OnClosed</c> that ran unguarded, and it sat in the middle of the sequence. Anything
    /// it threw escaped the Closed handler and abandoned everything after it -- by which point
    /// the tray icon and the pill window were already gone, so the surviving windows held the
    /// process open with nothing left on screen able to reach it. What the user sees is an
    /// application that cannot be quit, and there is nothing in a log to look at, because the
    /// console it would have written to was being torn down.
    ///
    /// A shutdown path is the one place where a later step must not depend on an earlier one
    /// succeeding, and that property is invisible in review: the code reads perfectly well
    /// with one unguarded line in it. So it is checked instead.
    /// </summary>
    private static void Shutdown(string root, Action<string, bool, string> check)
    {
        string file = Path.Combine(root, "src", "Shellvis.Shell", "Views", "PillWindow.xaml.cs");

        if (!File.Exists(file))
        {
            check("the pill's shutdown path was found", false, file);
            return;
        }

        string text = File.ReadAllText(file);
        int start = text.IndexOf("private void OnClosed(", StringComparison.Ordinal);

        if (start < 0)
        {
            check("OnClosed was found", false, "the method has been renamed; this check needs updating");
            return;
        }

        // To the end of the method: the next line that is a closing brace at method indent.
        int end = text.IndexOf("\n    }", start, StringComparison.Ordinal);
        string body = end > start ? text[start..end] : text[start..];

        int unguarded = 0;

        foreach (string line in body.ReplaceLineEndings("\n").Split('\n'))
        {
            string code = line.Trim();

            // Comments and the guard helper's own body are not steps.
            if (code.StartsWith("//", StringComparison.Ordinal) || code.Contains("step()", StringComparison.Ordinal))
                continue;

            bool isTeardown = code.Contains(".Dispose()", StringComparison.Ordinal)
                || code.Contains("Close();", StringComparison.Ordinal)
                || code.Contains("CloseAnswerWindow", StringComparison.Ordinal)
                || code.Contains("CloseStickies", StringComparison.Ordinal)
                || code.Contains("CloseToast", StringComparison.Ordinal);

            if (!isTeardown)
                continue;

            if (!code.Contains("Safe(", StringComparison.Ordinal))
            {
                unguarded++;
                check($"a teardown step runs unguarded: {code}", false,
                    "one throw here abandons the rest and leaves a process nothing can quit");
            }
        }

        check("every shutdown step is isolated", unguarded == 0, string.Empty);
    }

    /// <summary>
    /// The row spacing in C# still matches the one in the XAML.
    ///
    /// <b>Why a duplicated 6 is worth a check.</b> The docked bar's width is computed in
    /// <c>PillMetrics</c> from the number of buttons on it, and each button costs its own
    /// width plus one gap. The gap is declared in the XAML as <c>ColumnSpacing</c>, so the
    /// arithmetic has to know that number -- and a constant that exists in two places drifts.
    ///
    /// What drift looks like here is worth stating, because it is not a crash: the docked
    /// input field silently loses a few pixels every time a button is added to the bar. That
    /// field is the reason the docked bar exists, and it has now been asked for twice.
    /// </summary>
    private static void RowSpacing(string root, Action<string, bool, string> check)
    {
        string cs = Path.Combine(root, "src", "Shellvis.Shell", "Views", "PillMetrics.cs");
        string xaml = Path.Combine(root, "src", "Shellvis.Shell", "Views", "PillWindow.xaml");

        if (!File.Exists(cs) || !File.Exists(xaml))
        {
            check("the pill's metrics and layout were found", false, string.Empty);
            return;
        }

        Match declared = Regex.Match(
            File.ReadAllText(cs), @"RowSpacing\s*=\s*(?<n>[0-9]+(\.[0-9]+)?)");

        Match used = Regex.Match(
            File.ReadAllText(xaml), @"ColumnSpacing\s*=\s*""(?<n>[0-9]+(\.[0-9]+)?)""");

        if (!declared.Success || !used.Success)
        {
            check("row spacing is declared in both places", false,
                $"C#: {declared.Success}, XAML: {used.Success}");

            return;
        }

        string a = declared.Groups["n"].Value;
        string b = used.Groups["n"].Value;

        check($"PillMetrics.RowSpacing ({a}) matches the XAML ColumnSpacing ({b})",
            a == b,
            a == b ? string.Empty : "the docked field loses this difference for every button on the bar");
    }

    /// <summary>The repository root, found by the solution rather than by a fixed depth.</summary>
    private static string? FindRepositoryRoot()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null && !File.Exists(Path.Combine(here.FullName, "Shellvis.slnx")))
            here = here.Parent;

        return here?.FullName;
    }
}
