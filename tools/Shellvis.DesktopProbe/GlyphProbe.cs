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

        Console.WriteLine(failures == 0
            ? $"\nVERIFIED: all {found} icon glyphs carry a character, written as an escape so\n"
                + "they survive a file rewrite. An invisible button is a working button nobody\n"
                + "can find, and nothing else in this suite can see one."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
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
