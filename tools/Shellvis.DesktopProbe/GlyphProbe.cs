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
    /// build to it nine times, and twice more looking for it in the wrong file.
    ///
    /// The comments here are prose, and prose written in this house style uses dashes. So the
    /// rule is checked in a second rather than remembered.
    /// </summary>
    private static void XamlComments(string root, Action<string, bool, string> check)
    {
        var comment = new Regex(@"<!--(?<body>.*?)-->", RegexOptions.Compiled | RegexOptions.Singleline);
        int files = 0;

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(root, "src"), "*.xaml", SearchOption.AllDirectories))
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
                    "XML forbids it; the XAML compiler answers with WMC9999 against an SDK targets file");
            }
        }

        check($"{files} XAML file(s) have comments XML accepts", true, string.Empty);
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
