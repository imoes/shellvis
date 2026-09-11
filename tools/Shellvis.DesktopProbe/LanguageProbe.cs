using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using Shellvis.Core.Ui;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Two languages, and the page that has to be written in whichever is chosen.
///
/// <b>Why a harness for strings.</b> Because every failure here is a blank. A token in the
/// page with no property behind it renders as <c>{{Whatever}}</c> or as nothing at all; a
/// property nobody uses is dead weight that looks like coverage; a translation left as the
/// English original passes every compiler check and reads as an oversight to exactly the
/// people the translation was for. None of that throws, and none of it shows up until
/// somebody opens the window in the other language -- which, for the author, is never.
///
/// The one class of mistake that CANNOT happen is a missing property: UiText's members are
/// <c>required</c>, so a string added to one language and forgotten in the other does not
/// compile. What is checked here is everything that compiles fine and is still wrong.
/// </summary>
internal static class LanguageProbe
{
    public static int Run()
    {
        Console.WriteLine("language: German and English, and the page in both\n");

        int failures = 0;

        failures += Detecting();
        failures += Completeness();
        failures += ThePage();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: the machine's display language is followed unless the settings say\n"
                + "otherwise, both languages answer for every string, and the page renders with\n"
                + "no token left standing in either."
            : $"{failures} check(s) failed.");

        Console.WriteLine();
        Console.WriteLine("NOT covered here: whether the German reads well, which needs a reader,");
        Console.WriteLine("and the console, which is English on purpose.");

        return failures == 0 ? 0 : 1;
    }

    private static int Detecting()
    {
        Console.WriteLine("-- which language, and who decides --");

        int failures = 0;

        failures += Check("an explicit 'de' is honoured", UiLanguage.Resolve("de") == "de");
        failures += Check("an explicit 'en' is honoured", UiLanguage.Resolve("en") == "en");

        failures += Check("case and region do not matter",
            UiLanguage.Resolve("DE-de") == "de" && UiLanguage.Resolve("en-GB") == "en");

        // The setting exists so somebody can disagree with their machine. "auto" and an
        // empty value both mean "do not".
        failures += Check("'auto' follows the machine",
            UiLanguage.Resolve("auto") == UiLanguage.Detect());

        failures += Check("and so does an empty setting",
            UiLanguage.Resolve(null) == UiLanguage.Detect()
                && UiLanguage.Resolve("  ") == UiLanguage.Detect());

        // A value nobody recognises must not produce a blank interface. It falls back to
        // the machine rather than to English, which is the friendlier of the two: somebody
        // who mistypes their own language keeps it.
        failures += Check("an unsupported setting falls back to the machine",
            UiLanguage.Resolve("klingon") == UiLanguage.Detect()
                && UiLanguage.Resolve("fr") == UiLanguage.Detect());

        // A DIFFERENT question, and the first version of this check confused the two: what
        // a machine in a third language gets. Forced here rather than inferred, because
        // this machine reads German and would answer "de" to everything.
        CultureInfo was = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("fi-FI");

            failures += Check("a machine in a third language gets English",
                UiLanguage.Detect() == "en",
                "a Finnish desktop should get an interface it can use, not an empty one");

            CultureInfo.CurrentUICulture = new CultureInfo("de-AT");

            failures += Check("and any German-speaking region gets German",
                UiLanguage.Detect() == "de",
                "Austria and Switzerland read German too; the region is not the language");
        }
        finally
        {
            CultureInfo.CurrentUICulture = was;
        }

        Console.WriteLine($"     · this machine reads {CultureInfo.CurrentUICulture.Name}, "
            + $"so the interface would be '{UiLanguage.Detect()}'");

        return failures;
    }

    private static int Completeness()
    {
        Console.WriteLine("\n-- both languages answer for everything --");

        int failures = 0;

        PropertyInfo[] strings = typeof(UiText)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .ToArray();

        Console.WriteLine($"     · {strings.Length} strings in each language");

        var blankEn = strings.Where(p => string.IsNullOrWhiteSpace((string?)p.GetValue(UiText.En))).ToArray();
        var blankDe = strings.Where(p => string.IsNullOrWhiteSpace((string?)p.GetValue(UiText.De))).ToArray();

        failures += Check("nothing is blank in English",
            blankEn.Length == 0, string.Join(", ", blankEn.Select(p => p.Name)));

        failures += Check("nothing is blank in German",
            blankDe.Length == 0, string.Join(", ", blankDe.Select(p => p.Name)));

        // An untranslated string is identical in both, and a handful legitimately are:
        // a product name, a punctuation-only fragment. Anything with real words in it that
        // is byte-identical is a translation that was never made.
        var same = strings
            .Where(p => (string?)p.GetValue(UiText.En) == (string?)p.GetValue(UiText.De))
            .Where(p => ((string?)p.GetValue(UiText.En) ?? string.Empty)
                .Any(char.IsLetter))
            .Select(p => p.Name)
            .ToArray();

        failures += Check("no string was left in English on the German side",
            same.Length == 0,
            same.Length > 0 ? string.Join(", ", same) : "every one differs");

        // The token map is what the page substitutes from. A property that never reaches it
        // is a string the page cannot use.
        failures += Check("every string is offered to the page as a token",
            UiText.En.Tokens.Count == strings.Length,
            $"{UiText.En.Tokens.Count} tokens for {strings.Length} strings");

        return failures;
    }

    private static int ThePage()
    {
        Console.WriteLine("\n-- the page, rendered in each language --");

        int failures = 0;

        string raw = PageSource();

        if (raw.Length == 0)
        {
            return Check("the page is in the build", false,
                "vorzimmer.html was not found as an embedded resource");
        }

        // Every token the page asks for must have a string behind it. A missing one is not
        // an exception -- the substitution simply leaves it -- so it would ship as
        // "{{DeskTitle}}" on somebody's screen.
        var asked = Regex.Matches(raw, @"\{\{([A-Za-z]+)\}\}")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Console.WriteLine($"     · the page asks for {asked.Length} tokens");

        string[] unknown = [.. asked.Where(a => !UiText.En.Tokens.ContainsKey(a))];

        failures += Check("every token the page asks for exists",
            unknown.Length == 0,
            unknown.Length > 0 ? string.Join(", ", unknown) : string.Empty);

        foreach (UiText text in new[] { UiText.En, UiText.De })
        {
            string rendered = Substitute(raw, text);
            string label = ReferenceEquals(text, UiText.En) ? "English" : "German";

            failures += Check($"nothing is left unsubstituted in {label}",
                !rendered.Contains("{{", StringComparison.Ordinal),
                Leftovers(rendered));

            failures += Check($"the {label} page carries its own title",
                rendered.Contains(text.DeskTitle, StringComparison.Ordinal));
        }

        // The German page must not still be hard-coded German: if a word was left in the
        // markup instead of tokenised, the English render still contains it.
        string english = Substitute(raw, UiText.En);

        string[] leftInPlace =
        [
            "Braucht eine Antwort", "Muss man wissen", "Jetzt zählen",
            "Nicht lesenswert", "Ungelesen", "Überfällig",
        ];

        string[] survived = [.. leftInPlace.Where(w => english.Contains(w, StringComparison.Ordinal))];

        failures += Check("no German survives into the English page",
            survived.Length == 0,
            survived.Length > 0
                ? string.Join(", ", survived) + " -- still hard-coded in the markup"
                : "every label came from the token map");

        return failures;
    }

    /// <summary>The same substitution the window does, so this checks the real thing.</summary>
    private static string Substitute(string page, UiText text)
    {
        foreach ((string key, string value) in text.Tokens)
            page = page.Replace("{{" + key + "}}", value, StringComparison.Ordinal);

        return page;
    }

    /// <summary>
    /// The page, out of the source tree.
    /// </summary>
    /// <remarks>
    /// <b>The file rather than the embedded copy, and that was a correction.</b> The first
    /// version loaded Shellvis.Shell.dll and read the resource out of it, on the reasoning
    /// that what ships is what matters. What it actually did was pick one of four build
    /// outputs by timestamp -- Debug, Release, x64\Debug, x64\Release -- and report "the
    /// page is in the build" against whichever the last command happened to touch. A check
    /// that examines a different artefact each run is worse than no check.
    ///
    /// That the page IS embedded is <c>probe page</c>'s job and it already asserts it. This
    /// harness is about the WORDS, so it reads the file those words live in.
    /// </remarks>
    private static string PageSource()
    {
        for (DirectoryInfo? up = new(AppContext.BaseDirectory); up is not null; up = up.Parent)
        {
            string candidate = Path.Combine(
                up.FullName, "src", "Shellvis.Shell", "Assets", "vorzimmer.html");

            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        return string.Empty;
    }

    private static string Leftovers(string rendered)
    {
        var left = Regex.Matches(rendered, @"\{\{[A-Za-z]*\}\}")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        return left.Length > 0 ? string.Join(", ", left) : string.Empty;
    }

    private static int Check(string what, bool condition, string detail = "")
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}"
            + (detail.Length > 0 ? $"  {detail}" : string.Empty));

        return condition ? 0 : 1;
    }
}
