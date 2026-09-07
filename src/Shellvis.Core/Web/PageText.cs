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

    /// <summary>
    /// Whether what came back is a sign-in wall rather than the page that was asked for.
    /// </summary>
    /// <remarks>
    /// <b>Why this is worth detecting rather than returning.</b> A page behind a login
    /// answers 200 and sends a full document, so nothing about the response says the content
    /// is missing. A Jira Service Desk portal, for instance, returns eighty kilobytes whose
    /// readable text is the word "Service Management" and a block of configuration JSON
    /// containing a loginUrl -- and a model handed that will either invent a summary or
    /// report, correctly but unhelpfully, that it has no information. Named, it becomes a
    /// fact with a remedy: the browser tools keep their logins between sessions.
    ///
    /// Deliberately conservative. A page that merely HAS a sign-in link -- which is most of
    /// the web -- must not be reported as a wall, so the marker has to be the login and the
    /// readable text has to be too short to be the page itself.
    /// </remarks>
    public static bool LooksLikeSignIn(string html, string text)
    {
        // A real page's worth of words is a real page, whatever else is in its markup.
        //
        // Four thousand, measured rather than chosen: Atlassian's own portal comes back with
        // 1,603 characters of "readable" text, nearly all of it configuration JSON. A first
        // attempt at 900 let it through, which is how this number came to be a measurement.
        if (text.Length > 4000)
            return false;

        // Strong markers only. An earlier version counted "signin" and "sign-in" towards a
        // threshold of two, and those two both match the same navigation link -- so any
        // short page with a sign-in link in its header would have been reported as a wall.
        // None of these appears in ordinary navigation.
        string[] markers =
        [
            "loginUrl",
            "login_url",
            "\"login\":",
            "name=\"password\"",
            "type=\"password\"",
            "id=\"password\"",
            "j_username",
        ];

        foreach (string marker in markers)
        {
            if (html.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the readable text of an HTML page is configuration rather than content.
    /// </summary>
    /// <remarks>
    /// <b>The shape a script-built page leaves behind.</b> A single-page application ships a
    /// document with no prose in it: the body is an empty mount point and the only text is a
    /// JSON blob the script will read. Stripped of its markup that comes out as, for
    /// instance, <c>{"reasonKey":"com.atlassian.pocketknife...","xsrfToken":...}</c> --
    /// which is not nothing, so nothing about it says the content is missing, and presenting
    /// it under "what the page says" invites a summary of a configuration file.
    ///
    /// Only for pages that arrived as HTML. A url that answers with JSON <i>is</i> its JSON
    /// and must come back as it is; that is what <c>raw</c> and the content-type check are
    /// for, and this is never consulted for one.
    /// </remarks>
    public static bool LooksLikeScriptShell(string text)
    {
        string trimmed = text.TrimStart();

        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        // A JSON object's worth of key separators, in text that is mostly that object. Two
        // is enough to distinguish it from a page whose first character happens to be a
        // brace -- a code sample, say -- while a real document with an object at the top
        // has prose after it and fails the size ratio below.
        int separators = 0;

        for (int at = trimmed.IndexOf("\":", StringComparison.Ordinal);
             at >= 0 && separators < 3;
             at = trimmed.IndexOf("\":", at + 2, StringComparison.Ordinal))
        {
            separators++;
        }

        if (separators < 2)
            return false;

        // And the object has to BE the page rather than sit at the top of one. The closing
        // brace near the end is what says the rest is not prose.
        int lastBrace = Math.Max(
            trimmed.LastIndexOf('}'),
            trimmed.LastIndexOf(']'));

        return lastBrace >= trimmed.Length - Math.Max(80, trimmed.Length / 10);
    }

    private static string Collapse(string value) =>
        Spaces.Replace(value.ReplaceLineEndings(" "), " ").Trim();
}
