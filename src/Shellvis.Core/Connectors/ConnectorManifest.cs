namespace Shellvis.Core.Connectors;

/// <summary>What a connector is made of.</summary>
public enum ConnectorKind
{
    /// <summary>A declarative REST description. No code, no process.</summary>
    Http,

    /// <summary>An MCP server, handed to the existing host unchanged.</summary>
    Mcp,
}

/// <summary>How a request proves who it is.</summary>
public enum AuthScheme
{
    /// <summary>Nothing. A public endpoint, or one behind a network boundary.</summary>
    None,

    /// <summary>HTTP Basic, from a user and a secret.</summary>
    Basic,

    /// <summary>A bearer token.</summary>
    Bearer,

    /// <summary>The secret goes in a named header, verbatim.</summary>
    Header,
}

/// <summary>Where a parameter goes.</summary>
public enum ParameterPlace
{
    Query,
    Path,
    Body,
}

/// <summary>
/// How a connector proves who it is.
///
/// Every field here is the NAME of something, never a value. The manifest is a file on
/// disk that gets read, quoted in error messages and shown in the install prompt; a
/// credential in it would be copied into all three.
/// </summary>
public sealed class ConnectorAuth
{
    /// <summary>none, basic, bearer or header.</summary>
    public AuthScheme Scheme { get; set; } = AuthScheme.None;

    /// <summary>The name of the variable holding the user, for basic.</summary>
    public string? UserVar { get; set; }

    /// <summary>
    /// The name the secret is looked up under: environment variable first, then the
    /// DPAPI store, so exporting it for a whole shell keeps working.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>Which header carries the secret, for <see cref="AuthScheme.Header"/>.</summary>
    public string? HeaderName { get; set; }
}

/// <summary>One argument of one tool.</summary>
public sealed class ConnectorParameter
{
    public string Name { get; set; } = string.Empty;

    /// <summary>query, path or body.</summary>
    public ParameterPlace In { get; set; } = ParameterPlace.Query;

    /// <summary>What it means, as the model reads it.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether the call is refused without it.
    ///
    /// Refused here rather than sent and rejected: the model gets a sentence about its
    /// own argument instead of a 400 it has to decode.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>Used when the model leaves it out.</summary>
    public string? Default { get; set; }

    /// <summary>
    /// Whether the value is fixed: always sent, never asked for, and not overridable.
    ///
    /// <b>Why a declared default is not enough.</b> A default is a suggestion. The model sees
    /// the parameter in the schema, and a description saying "leave this alone" is advice it
    /// is free to ignore -- which it did: asked for "my open tickets", it called the scoped
    /// tool and passed a JQL of its own, and the answer came back listing everybody's. The
    /// report was "I get all tickets when I ask for mine", and the fault was not the filter
    /// but that the filter could be replaced.
    ///
    /// A fixed parameter is absent from the schema the model is given, so there is nothing to
    /// override. That is the difference between a scope and a hint.
    /// </summary>
    public bool Fixed { get; set; }

    /// <summary>
    /// The name the server expects, when it differs from the one the model sees.
    /// Dotted for a body field: <c>fields.summary</c>.
    /// </summary>
    public string? Send { get; set; }
}

/// <summary>
/// How a response is turned into something a model can read.
///
/// Only three things are declarable, and that is the whole point: the rules that make a
/// result readable are enforced in <see cref="ResultShaper"/> where no manifest can reach
/// them.
/// </summary>
public sealed class ConnectorResult
{
    /// <summary>
    /// The property holding the list, or null when the response is one object. Dotted
    /// paths are allowed: <c>values.issues</c>.
    /// </summary>
    public string? Items { get; set; }

    /// <summary>
    /// One line per item, with <c>{dotted.path}</c> placeholders. The convention this
    /// project arrived at through the Home Assistant tools: the id leads, because it is
    /// the argument every follow-up call takes.
    /// </summary>
    public string? Line { get; set; }

    /// <summary>What to say when there is nothing. Naming the next step, not just "none".</summary>
    public string? Empty { get; set; }

    /// <summary>Where the server reports the full count, when the list is a page.</summary>
    public string? Total { get; set; }
}

/// <summary>One tool a connector offers.</summary>
public sealed class ConnectorTool
{
    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Path { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// read or write, as the package claims it.
    ///
    /// A claim and not a decision: the loader only honours <c>read</c> on a GET and
    /// floors everything else at <see cref="Tools.SideEffect.Mutating"/>.
    /// </summary>
    public string Effect { get; set; } = "write";

    /// <summary>Which argument the console shows in the one-line preview.</summary>
    public string? Preview { get; set; }

    public List<ConnectorParameter> Params { get; set; } = [];

    /// <summary>Extra headers for this call alone. <c>${VAR}</c> is expanded.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    public ConnectorResult? Result { get; set; }
}

/// <summary>
/// A connector package: one directory, one manifest, no build step.
///
/// <b>Why a manifest and not a class per integration.</b> The request was a connector
/// management like Cowork's: modular, load a package and it is installed. In Cowork a
/// connector IS an MCP server, and that is supported here as one of the two kinds -- but an
/// MCP server is a program, and "write a program" is not "load a package". A declarative
/// kind is what makes the promise true for an ordinary REST API.
///
/// <b>What the manifest deliberately cannot decide.</b> Three things, and each because a
/// package is content that may not have been written by the person running it:
/// <list type="bullet">
/// <item>It cannot carry a credential. A manifest that looks like it holds one is refused
/// rather than filtered, the same rule <c>SkillWriter</c> applies to a skill body.</item>
/// <item>It cannot declare itself harmless. Only a GET may end up read-only; the loader
/// floors everything else at Mutating, for the reason MCP's <c>trustReadOnly</c> is a local
/// setting rather than a server's claim.</item>
/// <item>It cannot decide how its output looks. The rules that make a result readable live
/// in <see cref="ResultShaper"/>, not here, so no manifest can opt out of them.</item>
/// </list>
/// </summary>
public sealed class ConnectorManifest
{
    /// <summary>Short key. It prefixes every tool name, so it should be short and stable.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>http or mcp.</summary>
    public ConnectorKind Kind { get; set; } = ConnectorKind.Http;

    /// <summary>A human name, for the connector list and the install prompt.</summary>
    public string? Title { get; set; }

    public string? Description { get; set; }

    /// <summary>Where the API lives. <c>${VAR}</c> is expanded when the connector starts.</summary>
    public string? BaseUrl { get; set; }

    public ConnectorAuth? Auth { get; set; }

    public List<ConnectorTool> Tools { get; set; } = [];

    /// <summary>Headers sent on every call. <c>${VAR}</c> is expanded.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    // kind: mcp -- passed to the existing host untouched.
    public string? Command { get; set; }

    public List<string> Args { get; set; } = [];

    public string? Url { get; set; }

    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Where the manifest was read from. Not part of the file.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// Why this manifest cannot be used, or null.
    ///
    /// Returns a sentence rather than throwing: a bad connector must leave the others
    /// working and tell the user which one it was, the same way one unreachable MCP server
    /// does not stop the agent from starting.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return "a connector needs a name.";

        if (!Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return $"'{Name}' is not a usable name: letters, digits, '-' and '_' only.";

        // The two fields that hold a name and must never hold a value. A real password
        // fails this: it carries punctuation, spaces or a leading digit. It is not a
        // strength test -- it is the shape of an environment variable and of a key in the
        // secret store, both of which reject anything else anyway.
        foreach ((string field, string? value) in
            new[] { ("auth.secret", Auth?.Secret), ("auth.userVar", Auth?.UserVar) })
        {
            if (value is { Length: > 0 } && !IsVariableName(value))
            {
                return $"{field} must be the NAME of a variable or stored secret, not a "
                    + "value. Names hold letters, digits, '_', '-' and '.' only.";
            }
        }

        if (Kind == ConnectorKind.Mcp)
        {
            return string.IsNullOrWhiteSpace(Command) && string.IsNullOrWhiteSpace(Url)
                ? "an mcp connector needs either a command or a url."
                : null;
        }

        if (string.IsNullOrWhiteSpace(BaseUrl))
            return "an http connector needs a baseUrl.";

        // A ${VAR} baseUrl cannot be parsed as a url until it is resolved, and refusing it
        // here would forbid exactly the form the security rules require.
        if (!BaseUrl.Contains("${", StringComparison.Ordinal)
            && (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
        {
            return $"baseUrl '{BaseUrl}' is not an http or https address.";
        }

        if (Tools.Count == 0)
            return "an http connector with no tools would register nothing.";

        foreach (ConnectorTool tool in Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
                return "every tool needs a name.";

            if (!tool.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                return $"tool name '{tool.Name}' may hold only letters, digits and '_'.";

            if (string.IsNullOrWhiteSpace(tool.Path))
                return $"tool '{tool.Name}' has no path.";

            if (!KnownMethods.Contains(tool.Method, StringComparer.OrdinalIgnoreCase))
            {
                return $"tool '{tool.Name}' uses method '{tool.Method}', which is not one of "
                    + string.Join(", ", KnownMethods) + ".";
            }
        }

        return null;
    }

    private static bool IsVariableName(string value) =>
        value.Length > 0
        && !char.IsAsciiDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static readonly string[] KnownMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];
}
