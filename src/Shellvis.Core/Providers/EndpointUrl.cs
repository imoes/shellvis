namespace Shellvis.Core.Providers;

/// <summary>
/// Turns what a person types into the URL an OpenAI-compatible client needs.
///
/// Nobody should have to know that a chat endpoint lives under <c>/v1</c>. Typing
/// <c>my-host/llama</c> is what a person means, and the two things that stand between that
/// and a working client -- a missing scheme and a missing version segment -- are both
/// mechanical. Making the user supply them is asking them to remember a detail of somebody
/// else's API in order to fill in a text box.
///
/// The failure this prevents is also the least informative one available: without
/// <c>/v1</c> the first request returns 404, which reads like an outage rather than like a
/// text field filled in slightly wrong.
///
/// The normalisation is deliberately shallow. It adds what is missing and never changes what
/// is there: a URL that already carries a scheme keeps it, and a path that already ends in a
/// version segment is left alone, including the shapes that are not <c>/v1</c> at all
/// (Google's OpenAI-compatible endpoint ends in <c>/v1beta/openai</c>).
/// </summary>
public static class EndpointUrl
{
    /// <summary>
    /// Path endings that mean "this is already an API root".
    ///
    /// Matched as the last segment, or the last two for the compound ones. A prefix or
    /// substring test would accept a path merely containing "v1" somewhere and then leave a
    /// URL with no API root at all.
    /// </summary>
    private static readonly string[] VersionSegments =
    [
        "v1", "v1beta", "openai", "api", "compat",
    ];

    /// <summary>
    /// Hosts where plain HTTP is the convention rather than a mistake.
    ///
    /// Defaulting everything to https and letting the user correct it would be tidier, but
    /// a local inference server almost never has a certificate, so the tidier rule would
    /// make the commonest case the one that fails.
    /// </summary>
    private static readonly string[] PlainHttpHosts =
    [
        "localhost", "127.0.0.1", "::1", "0.0.0.0",
    ];

    /// <summary>
    /// Normalise a base URL. Returns null for input that is not a URL at all, so the caller
    /// can refuse rather than build something that cannot work.
    /// </summary>
    public static string? Normalise(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        // Whitespace only. Trimming slashes here was a bug the harness caught: it turned
        // "http://" into "http:", which then looked like a bare host and came back as
        // "https://http/v1". The path is tidied later, by UriBuilder, where the scheme is
        // already accounted for.
        string text = input.Trim();

        if (text.Length == 0)
            return null;

        switch (SchemeOf(text))
        {
            case null:
                // A bare host, which is what a person types. The scheme is chosen from the
                // host rather than fixed, see PlainHttpHosts.
                string host = text.Split('/', 2)[0].Split(':', 2)[0];

                bool plain = PlainHttpHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
                    || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

                text = (plain ? "http://" : "https://") + text;
                break;

            case "http" or "https":
                break;

            default:
                // Some other scheme. Refused rather than rewritten: a "mailto:" here is a
                // mistake, and testing for "://" instead of for a scheme accepted it as a
                // host name and produced "https://mailto:a@b.c/v1".
                return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
            return null;

        // Only the two schemes an HTTP client can use. A file: or a mailto: here is a typo
        // that would otherwise reach the client library and fail somewhere less obvious.
        if (uri.Scheme is not ("http" or "https"))
            return null;

        if (uri.Host.Length == 0)
            return null;

        string path = uri.AbsolutePath.Trim('/');

        if (!EndsWithVersion(path))
        {
            path = path.Length == 0 ? "v1" : path + "/v1";
        }

        var builder = new UriBuilder(uri) { Path = path, Query = string.Empty, Fragment = string.Empty };

        // UriBuilder writes the default port explicitly; dropping it keeps the value
        // readable and identical to what the user would have typed.
        if (uri.IsDefaultPort)
            builder.Port = -1;

        return builder.Uri.ToString().TrimEnd('/');
    }

    /// <summary>
    /// The URI scheme, or null when the text begins with a host instead.
    ///
    /// The distinction that has to be got right is "scheme:" against "host:port", because
    /// both are a word, a colon and then something. What separates them is what follows:
    /// digits alone are a port, anything else is a scheme. Testing for "://" instead gets
    /// mailto: wrong, and testing for ":" alone gets localhost:8080 wrong.
    /// </summary>
    private static string? SchemeOf(string text)
    {
        int colon = text.IndexOf(':');

        if (colon <= 0)
            return null;

        foreach (char c in text[..colon])
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
                return null;
        }

        string rest = text[(colon + 1)..];

        if (rest.StartsWith("//", StringComparison.Ordinal))
            return text[..colon].ToLowerInvariant();

        // host:port -- a port is digits up to the end or the first slash.
        string port = rest.Split('/', 2)[0];

        if (port.Length > 0 && port.All(char.IsAsciiDigit))
            return null;

        return text[..colon].ToLowerInvariant();
    }

    /// <summary>
    /// Whether a path already ends in something that looks like an API root.
    ///
    /// The last TWO segments are considered, so "v1beta/openai" counts without needing an
    /// entry for every combination.
    /// </summary>
    private static bool EndsWithVersion(string path)
    {
        if (path.Length == 0)
            return false;

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return false;

        if (VersionSegments.Contains(segments[^1], StringComparer.OrdinalIgnoreCase))
            return true;

        // A version with a suffix, such as v1beta2 or v2.
        string last = segments[^1];

        return last.Length >= 2
            && (last[0] is 'v' or 'V')
            && char.IsAsciiDigit(last[1]);
    }
}
