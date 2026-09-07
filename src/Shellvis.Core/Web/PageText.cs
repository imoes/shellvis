using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Shellvis.Core.Web;

/// <summary>
/// HTML, reduced to the words on the page.
///
/// <b>Why by hand and not with a parser.</b> The job here is not to understand the document,
/// it is to throw nearly all of it away: script, style, navigation, the four kilobytes of
/// inline SVG every corporate template carries. A tolerant regex pass does that in one read
/// over the string and cannot fail on malformed markup, which most real pages are. A DOM
/// parser would give a tree nobody asks a question of, at the cost of a dependency.
///
/// <b>What matters is that the removals happen in the right order.</b> Script and style
/// bodies go first, tags second, entities last. Decoding entities before stripping tags would
/// turn a page's own <c>&amp;lt;script&amp;gt;</c> -- text, not markup -- into a tag and then
/// delete it, silently changing what the page said.
/// </summary>
public static class PageText
{
    /// <summary>Elements whose contents are code or styling rather than reading matter.</summary>
    private static readonly Regex Noise = new(
        @"<(script|style|noscript|template|svg|iframe|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Elements that end a line when they close.</summary>
    private static readonly Regex Breaks = new(
        @"</?(p|div|br|li|tr|h[1-6]|section|article|header|footer|blockquote|pre|table)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tags = new(
        "<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex Comments = new(
        "<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Spaces = new(
        @"[ \t\f\v]+", RegexOptions.Compiled);

    private static readonly Regex BlankLines = new(
        @"\n{3,}", RegexOptions.Compiled);

    private static readonly Regex TitleTag = new(
        @"<title\b[^>]*>(.*?)</title\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The page's own title, or null when it has none.</summary>
    /// <remarks>
    /// Read before <see cref="Noise"/> runs, because the title lives inside
    /// <c>&lt;head&gt;</c> and head is one of the elements thrown away wholesale.
    /// </remarks>
    public static string? TitleOf(string html)
    {
        Match found = TitleTag.Match(html);

        if (!found.Success)
            return null;

        string title = Collapse(WebUtility.HtmlDecode(found.Groups[1].Value));

        return title.Length > 0 ? title : null;
    }

    /// <summary>The readable text of an HTML document.</summary>
    public static string Of(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        string text = Comments.Replace(html, " ");
        text = Noise.Replace(text, "\n");
        text = Breaks.Replace(text, "\n");
        text = Tags.Replace(text, " ");

        // Last, and deliberately so: see the class remarks.
        text = WebUtility.HtmlDecode(text);

        text = text.ReplaceLineEndings("\n");
        text = Spaces.Replace(text, " ");

        var trimmed = new StringBuilder(text.Length);

        foreach (string line in text.Split('\n'))
            trimmed.Append(line.Trim()).Append('\n');

        return BlankLines.Replace(trimmed.ToString(), "\n\n").Trim();
    }

    /// <summary>Whether a response body should be treated as markup.</summary>
    /// <remarks>
    /// The content type is asked first because it is what the server asserts, and the sniff
    /// is only for the servers that assert nothing. Running the HTML pass over JSON would
    /// eat every value that happens to contain an angle bracket.
    /// </remarks>
    public static bool LooksLikeHtml(string? contentType, string body)
    {
        if (contentType is { Length: > 0 })
        {
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                return true;

            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string opening = body.Length > 400 ? body[..400] : body;

        return opening.Contains("<html", StringComparison.OrdinalIgnoreCase)
            || opening.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase);
    }

    private static string Collapse(string value) =>
        Spaces.Replace(value.ReplaceLineEndings(" "), " ").Trim();
}
