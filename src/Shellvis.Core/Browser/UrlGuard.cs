using System.Net;
using System.Net.Sockets;

namespace Shellvis.Core.Browser;

/// <summary>
/// Decides whether a url may be navigated to.
///
/// This exists because a browser under agent control is a request-forging tool pointed
/// at the inside of a network. The machine running Shellvis sits behind a corporate VPN
/// with reachable internal hosts, and a page or a tool description can name a url --
/// meaning the address may not have come from the user at all. Blocking private ranges
/// by default is therefore not paranoia about the user's own intranet; it is refusing to
/// let text from outside aim the browser at it.
/// </summary>
public sealed class UrlGuard
{
    /// <summary>Hosts refused outright, matched on the registrable suffix.</summary>
    public List<string> Blocklist { get; init; } = [];

    /// <summary>
    /// Whether loopback, link-local and RFC1918 addresses may be reached.
    ///
    /// Off by default. A local development server is a perfectly ordinary target, which
    /// is exactly why turning this on has to be the user's deliberate act.
    /// </summary>
    public bool AllowPrivate { get; init; }

    /// <summary>Schemes worth allowing. Everything else is a way to reach something else.</summary>
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "about" };

    /// <summary>Null when the url is acceptable, otherwise the reason it is not.</summary>
    public string? Refuse(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "No url was given.";

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return $"'{url}' is not an absolute url. Include the scheme, "
                + "for example https://example.com.";
        }

        if (!AllowedSchemes.Contains(uri.Scheme))
        {
            // file: would turn the browser into a file reader that bypasses every path
            // check the file tools apply; javascript: would execute in whatever page is
            // open, which is a different act from navigating.
            return $"The scheme '{uri.Scheme}:' is not navigable. Use http or https "
                + "-- read_file reads local files, and browser_evaluate runs script.";
        }

        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase))
            return null;

        string host = uri.Host;

        foreach (string blocked in Blocklist)
        {
            if (string.IsNullOrWhiteSpace(blocked))
                continue;

            string pattern = blocked.Trim().TrimStart('.');

            // Suffix match, so blocking example.com also blocks ads.example.com --
            // otherwise a blocklist is defeated by any subdomain.
            if (host.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + pattern, StringComparison.OrdinalIgnoreCase))
            {
                return $"{host} is on the blocklist in config.yaml.";
            }
        }

        if (AllowPrivate)
            return null;

        if (IsPrivateHost(host, out string? why))
        {
            return $"{host} is {why}. Set browser.allowPrivateUrls in config.yaml to "
                + "allow local and internal addresses -- it is off by default so that a "
                + "url coming from a web page cannot aim the browser at your network.";
        }

        return null;
    }

    /// <summary>
    /// Whether a host names something inside the network.
    ///
    /// Deliberately does not resolve DNS. A name that resolves to a private address
    /// today may not tomorrow, and resolving here would mean every refusal decision
    /// depends on a network round trip that can hang or be poisoned. Literal addresses
    /// and the obvious local names are what can be judged honestly without one.
    /// </summary>
    private static bool IsPrivateHost(string host, out string? reason)
    {
        reason = null;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            reason = "this machine";
            return true;
        }

        // Single-label names resolve through the search domain, which on a
        // domain-joined machine means an internal host.
        if (!host.Contains('.') && !IPAddress.TryParse(host, out _))
        {
            reason = "a single-label name, which resolves inside your own domain";
            return true;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".intranet", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
        {
            reason = "an internal name";
            return true;
        }

        if (!IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address))
            return false;

        if (IPAddress.IsLoopback(address))
        {
            reason = "a loopback address";
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] octets = address.GetAddressBytes();

            bool isPrivate = octets[0] switch
            {
                10 => true,
                127 => true,
                169 when octets[1] == 254 => true,          // link-local
                172 when octets[1] >= 16 && octets[1] <= 31 => true,
                192 when octets[1] == 168 => true,
                // Carrier-grade NAT. Reachable inside many corporate networks and not
                // routable from the internet, so it belongs on this list.
                100 when octets[1] >= 64 && octets[1] <= 127 => true,
                0 => true,
                _ => false,
            };

            if (isPrivate)
            {
                reason = "a private address";
                return true;
            }

            return false;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            reason = "a link-local address";
            return true;
        }

        // fc00::/7, the IPv6 equivalent of RFC1918.
        byte first = address.GetAddressBytes()[0];

        if ((first & 0xFE) == 0xFC)
        {
            reason = "a unique-local address";
            return true;
        }

        return false;
    }
}
