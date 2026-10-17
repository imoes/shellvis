namespace Shellvis.Core.Mcp;

/// <summary>How to reach an MCP server.</summary>
public enum McpTransport
{
    /// <summary>Launch a local process and speak over its stdin and stdout.</summary>
    Stdio,

    /// <summary>Talk to an HTTP endpoint.</summary>
    Http,
}

/// <summary>
/// One configured MCP server.
/// </summary>
/// <param name="Name">
/// Short key. It becomes part of every tool name from this server, so it should be
/// short and stable.
/// </param>
/// <param name="Transport">stdio for a local process, http for a remote endpoint.</param>
/// <param name="Command">Executable to launch, for stdio.</param>
/// <param name="Arguments">Arguments for that executable.</param>
/// <param name="Url">Endpoint, for http.</param>
/// <param name="Environment">
/// Extra environment variables for a stdio child. Only these are passed through, on
/// top of a small safe base set; see <see cref="McpHost"/> for why.
/// </param>
/// <param name="Headers">Extra HTTP headers, typically for authentication.</param>
/// <param name="ConnectTimeoutSeconds">
/// How long to wait for the handshake. A stdio server that has to install itself on
/// first run legitimately takes a while.
/// </param>
/// <param name="Include">
/// If non-empty, only these tool names are exposed. Useful for a server with a large
/// surface where only part of it is wanted.
/// </param>
/// <param name="Exclude">Tool names to hide.</param>
/// <param name="TrustReadOnly">
/// Tool names from this server that may run without asking. Empty by default and
/// deliberately so: a remote server cannot be allowed to declare its own tools
/// harmless, because that is exactly what a malicious one would do.
/// </param>
public sealed record McpServerConfig(
    string Name,
    McpTransport Transport,
    string? Command = null,
    IReadOnlyList<string>? Arguments = null,
    string? Url = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    int ConnectTimeoutSeconds = 60,
    IReadOnlyList<string>? Include = null,
    IReadOnlyList<string>? Exclude = null,
    IReadOnlyList<string>? TrustReadOnly = null)
{
    /// <summary>Whether a tool from this server should be exposed at all.</summary>
    public bool Allows(string toolName)
    {
        if (Exclude is { Count: > 0 } && Exclude.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            return false;

        return Include is not { Count: > 0 }
            || Include.Contains(toolName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether a tool from this server may run without a prompt.</summary>
    public bool IsTrustedReadOnly(string toolName) =>
        TrustReadOnly is { Count: > 0 }
        && TrustReadOnly.Contains(toolName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Validate the configuration, returning a reason when it cannot work.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "an MCP server needs a name.";

        // The name becomes part of a tool identifier, so it has to survive
        // sanitisation into something still recognisable.
        if (!Name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            return $"MCP server name '{Name}' may only contain letters, digits, - and _.";

        return Transport switch
        {
            McpTransport.Stdio when string.IsNullOrWhiteSpace(Command) =>
                $"MCP server '{Name}' uses stdio but has no command to run.",
            McpTransport.Http when string.IsNullOrWhiteSpace(Url) =>
                $"MCP server '{Name}' uses http but has no url.",
            McpTransport.Http when !Uri.TryCreate(Url, UriKind.Absolute, out _) =>
                $"MCP server '{Name}' has an unusable url: {Url}",
            _ => null,
        };
    }
}
