using System.Text.RegularExpressions;

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

        // ------------------------------------------------------------------ it is whole
        Console.WriteLine("\n-- the rules are all on it --");

        foreach ((string what, string marker) in new[]
        {
            ("the boundary that is never crossed", "Es wird nichts gesendet"),
            ("sorting before speaking", "Vorsortierung"),
            ("keeping work off the desk", "Rücken freihalten"),
            ("the follow-up that forgets nothing", "Wiedervorlage"),
            ("thinking ahead", "Vorausdenken"),
            ("writing as they write", "Wort und Schrift"),
            ("discretion", "Verschwiegenheit"),
            ("the sentinel that means say nothing", "SILENCE"),
            ("what code decides rather than judgement", "Was Code entscheidet"),
        })
        {
            Check(what, html.Contains(marker, StringComparison.Ordinal), marker);
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
              + "carries all six rules and the threshold, and designs both themes.\n"
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
