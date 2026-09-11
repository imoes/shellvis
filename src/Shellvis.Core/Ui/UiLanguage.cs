using System.Globalization;

namespace Shellvis.Core.Ui;

/// <summary>
/// Which language the interface speaks.
///
/// <b>Two, and that is a decision rather than a start.</b> German because that is the desk
/// this was built for and the language its mail arrives in; English because everything else
/// in the project already is -- the console, the tool catalogue, the commit log. A third
/// would need a translator who speaks it, and a machine translation of an interface is worse
/// than an English one: it reads as almost right, which is harder to correct than plainly
/// foreign.
///
/// <b>What is NOT translated, on purpose.</b> The tool descriptions and the sorting rules go
/// to the model, not to a person. They are tuned in English, the model answers in the user's
/// language anyway, and translating them would change behaviour rather than presentation --
/// a different thing entirely from what this file is for. The console below the pill stays
/// English too: it is a log of tool names and results, and half-translating a line that ends
/// in "browser_navigate" helps nobody.
/// </summary>
public static class UiLanguage
{
    /// <summary>The two-letter code for German.</summary>
    public const string German = "de";

    /// <summary>The two-letter code for English, and the fallback for everything else.</summary>
    public const string English = "en";

    /// <summary>
    /// The language to use, from the configured preference and the machine.
    /// </summary>
    /// <param name="configured">
    /// What the settings say: "de", "en", or null/"auto" to follow the machine.
    /// </param>
    /// <remarks>
    /// <b>CurrentUICulture, not CurrentCulture.</b> They are different settings and Windows
    /// lets them disagree: a German keyboard and date format with an English display language
    /// is an ordinary configuration on a managed desktop, and the display language is the one
    /// that says which language a person wants to READ. Picking the other would give an
    /// English installation a German interface because the dates are formatted dd.MM.
    ///
    /// Anything that is not German gets English, rather than an exception or an empty string.
    /// A Finnish machine should see an interface it can use, not a blank one.
    /// </remarks>
    public static string Resolve(string? configured)
    {
        string wanted = (configured ?? string.Empty).Trim().ToLowerInvariant();

        if (wanted is German or "de-de" or "deutsch")
            return German;

        if (wanted is English or "en-us" or "en-gb" or "english")
            return English;

        // "auto", empty, or something nobody recognises: ask the machine. An unreadable
        // setting falling back to the machine is friendlier than refusing to start, and the
        // settings dialog only ever writes one of the three known values.
        return Detect();
    }

    /// <summary>What the machine's display language is, as one of the two.</summary>
    public static string Detect() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals(German, StringComparison.OrdinalIgnoreCase)
            ? German
            : English;

    /// <summary>The strings for a resolved language code.</summary>
    public static UiText TextFor(string language) =>
        language.Equals(German, StringComparison.OrdinalIgnoreCase)
            ? UiText.De
            : UiText.En;

    /// <summary>The strings for what the settings say, resolving "auto" against the machine.</summary>
    public static UiText For(string? configured) => TextFor(Resolve(configured));
}
