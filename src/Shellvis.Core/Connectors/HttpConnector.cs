using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Shellvis.Core.Config;

namespace Shellvis.Core.Connectors;

/// <summary>
/// A declared REST API, running as tools.
///
/// <b>Where the credentials come from, and where they do not.</b> Never from the manifest.
/// The manifest names a variable; the value is looked up as an environment variable first
/// and in the DPAPI secret store second. That order is deliberate and matches the provider
/// keys: someone who exported a variable for their whole shell expects it to be the one in
/// use, and a stored value silently overriding it would be a setting they cannot see from
/// where they set it.
///
/// <b>What is done about a failure.</b> The same as the Home Assistant client: the status,
/// a hint that names the variable when the answer is 401, and the response body, clipped.
/// The body is the part that lets a model correct its own mistake -- Jira explains a bad
/// JQL in it, and that sentence is worth more than any wrapper this code could write.
/// </summary>
public sealed class HttpConnector : IDisposable
{
    private readonly ConnectorManifest _manifest;
    private readonly HttpClient _http;
    private readonly string? _secretName;

    private HttpConnector(ConnectorManifest manifest, HttpClient http, string? secretName)
    {
        _manifest = manifest;
        _http = http;
        _secretName = secretName;
    }

    /// <summary>The connector, or null when it is not configured on this machine.</summary>
    /// <remarks>
    /// Null rather than an exception, for the reason Home Assistant returns null: an absent
    /// integration is the normal case. Registering tools that fail on first use means the
    /// model plans around a capability that is not there, which costs a round and reads as a
    /// broken agent rather than as an unconfigured one.
    /// </remarks>
    public static HttpConnector? TryCreate(ConnectorManifest manifest, out string? reason)
    {
        reason = null;

        ConnectorAuth auth = manifest.Auth ?? new ConnectorAuth();

        string? secret = null;

        if (auth.Scheme != AuthScheme.None)
        {
            if (string.IsNullOrWhiteSpace(auth.Secret))
            {
                reason = $"{manifest.Name}: auth is declared but no secret name is given.";
                return null;
            }

            secret = Resolve(auth.Secret);

            if (string.IsNullOrWhiteSpace(secret))
            {
                reason = $"{manifest.Name} is not configured: set the {auth.Secret} "
                    + "environment variable, or store the secret under that name.";

                return null;
            }
        }

        // The base url is a ${VAR} in every shipped connector: an installation's host is a
        // local fact, and putting it in the package would make the package personal.
        string baseUrl = Expand(manifest.BaseUrl ?? string.Empty);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            reason = $"{manifest.Name} has no baseUrl.";
            return null;
        }

        if (baseUrl.Contains("${", StringComparison.Ordinal))
        {
            reason = $"{manifest.Name} is not configured: its baseUrl is still {baseUrl}. "
                + "Set that variable to the address of your installation.";

            return null;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            reason = $"{manifest.Name}: '{baseUrl}' is not an http or https address.";
            return null;
        }

        // A trailing slash, or Uri composition silently drops the last path segment of a
        // reverse-proxied install. The Home Assistant client carries the same line and the
        // same scar.
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";

        var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(45),
        };

        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        switch (auth.Scheme)
        {
            case AuthScheme.Basic:
            {
                string user = Resolve(auth.UserVar) ?? string.Empty;

                if (user.Length == 0)
                {
                    reason = $"{manifest.Name}: basic auth needs a user; set {auth.UserVar}.";
                    http.Dispose();
                    return null;
                }

                string pair = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}"));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", pair);
                break;
            }

            case AuthScheme.Bearer:
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                break;

            case AuthScheme.Header:
                http.DefaultRequestHeaders.TryAddWithoutValidation(
                    auth.HeaderName ?? "Authorization", secret);

                break;
        }

        if (manifest.Headers.Count > 0)
        {
            foreach ((string name, string value) in manifest.Headers)
                http.DefaultRequestHeaders.TryAddWithoutValidation(name, Expand(value));
        }

        return new HttpConnector(manifest, http, auth.Secret);
    }

    /// <summary>Run one declared tool.</summary>
    public async Task<string> CallAsync(
        ConnectorTool tool,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        string path = tool.Path.TrimStart('/');
        var query = new List<string>();
        var body = new JsonObject();

        foreach (ConnectorParameter parameter in tool.Params)
        {
            string? value = Value(arguments, parameter);

            if (string.IsNullOrEmpty(value))
            {
                if (parameter.Required)
                {
                    // Refused here rather than sent and rejected: the model gets a sentence
                    // about its own argument instead of a 400 it has to decode.
                    return $"error: {parameter.Name} is required.";
                }

                continue;
            }

            switch (parameter.In)
            {
                case ParameterPlace.Path:
                    path = path.Replace(
                        "{" + parameter.Name + "}",
                        Uri.EscapeDataString(value),
                        StringComparison.Ordinal);

                    break;

                case ParameterPlace.Body:
                    Place(body, parameter.Send ?? parameter.Name, value);
                    break;

                default:
                    query.Add($"{Uri.EscapeDataString(parameter.Send ?? parameter.Name)}={Uri.EscapeDataString(value)}");
                    break;
            }
        }

        // A path placeholder nobody filled would go out as a literal "{key}" and come back
        // as a puzzling 404. Caught here, named.
        int open = path.IndexOf('{', StringComparison.Ordinal);

        if (open >= 0)
        {
            int close = path.IndexOf('}', open);
            string missing = close > open ? path[(open + 1)..close] : "?";

            return $"error: the path needs {missing}, which was not given.";
        }

        if (query.Count > 0)
            path += "?" + string.Join('&', query);

        using var request = new HttpRequestMessage(new HttpMethod(tool.Method.ToUpperInvariant()), path);

        if (body.Count > 0)
        {
            request.Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        foreach ((string name, string value) in tool.Headers)
            request.Headers.TryAddWithoutValidation(name, Expand(value));

        using HttpResponseMessage response = await _http
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return Failure(response, text);

        return ResultShaper.Shape(text, Resolve(tool.Result, tool, arguments), tool.Name);
    }

    /// <summary>
    /// Expand <c>${VAR}</c> in the line template, so a result can carry a real link.
    ///
    /// <b>Why the template needs the variables at all.</b> A ticket key is only useful if you
    /// can open it, and the address of the installation is exactly the thing a manifest is
    /// forbidden to contain. Expanding here lets a line be written as
    /// <c>[{key}](${JIRA_URL}/browse/{key})</c>: the <c>${...}</c> half is resolved from the
    /// configuration, the <c>{...}</c> half from the response, and neither the package nor
    /// the model has to know the host.
    ///
    /// The two syntaxes cannot collide -- one requires the dollar -- which is why this can run
    /// before the shaper without touching its placeholders.
    /// </summary>
    /// <summary>
    /// Fill in everything the response cannot supply: configuration and the arguments.
    ///
    /// <b>Two kinds of placeholder, and the second one is why this grew.</b> <c>${VAR}</c>
    /// comes from the configuration and is how a line can carry a link to the installation
    /// without the address ever being in the package — or in front of the model.
    /// <c>{$name}</c> is an argument the tool was called with, which the response sometimes
    /// does not contain: the comments on a Jira issue arrive as a bare list, because a
    /// comment does not know the key of the issue it belongs to. Without this there was no
    /// way to put a link to the ticket next to its own comments.
    ///
    /// Applied to the heading, the line and the empty text alike. An empty result is exactly
    /// when a link matters most: "nothing has been said on this ticket" is more use with the
    /// ticket beside it.
    /// </summary>
    private static ConnectorResult? Resolve(
        ConnectorResult? result,
        ConnectorTool tool,
        IReadOnlyDictionary<string, object?> arguments)
    {
        if (result is null)
            return null;

        return new ConnectorResult
        {
            Items = result.Items,
            Total = result.Total,
            Heading = Fill(result.Heading, tool, arguments),
            Line = Fill(result.Line, tool, arguments),
            Empty = Fill(result.Empty, tool, arguments),
        };
    }

    private static string? Fill(
        string? template,
        ConnectorTool tool,
        IReadOnlyDictionary<string, object?> arguments)
    {
        if (template is not { Length: > 0 })
            return template;

        string filled = template.Contains("${", StringComparison.Ordinal)
            ? Expand(template)
            : template;

        if (!filled.Contains("{$", StringComparison.Ordinal))
            return filled;

        foreach (ConnectorParameter parameter in tool.Params)
        {
            string token = "{$" + parameter.Name + "}";

            if (!filled.Contains(token, StringComparison.Ordinal))
                continue;

            // The value that actually went out, defaults included, so a heading naming an
            // argument says what was asked rather than what the caller typed.
            filled = filled.Replace(
                token,
                Value(arguments, parameter) ?? string.Empty,
                StringComparison.Ordinal);
        }

        return filled;
    }

    private string Failure(HttpResponseMessage response, string body)
    {
        string hint = (int)response.StatusCode switch
        {
            401 => _secretName is { Length: > 0 }
                ? $" The credential in {_secretName} was rejected."
                : " The credential was rejected.",

            403 => " The credential is valid but not permitted to do this.",
            404 => $" Check the path and the baseUrl ({_http.BaseAddress}).",
            _ => string.Empty,
        };

        // The body is included because it is what lets the model fix its own call: Jira
        // explains a malformed JQL there, and that sentence is worth more than any wrapper
        // this code could write. Clipped, because a misrouted url answers with a whole page.
        string detail = body.Trim();

        if (detail.Length > 600)
            detail = detail[..600] + " ...";

        return $"error: {_manifest.Name} returned {(int)response.StatusCode} "
            + $"{response.ReasonPhrase}.{hint} {detail}".TrimEnd();
    }

    private static string? Value(IReadOnlyDictionary<string, object?> arguments, ConnectorParameter parameter)
    {
        // A fixed parameter ignores whatever was passed. Not "prefers the default" -- ignores:
        // it is not in the schema, so anything arriving under its name came from a model
        // guessing, and honouring that guess is how a scoped tool stops being scoped.
        if (parameter.Fixed)
            return Expand(parameter.Default ?? string.Empty) is { Length: > 0 } fixedValue
                ? fixedValue
                : null;

        if (arguments.TryGetValue(parameter.Name, out object? given) && given is not null)
        {
            string text = given switch
            {
                JsonElement element => element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.ToString(),

                _ => given.ToString() ?? string.Empty,
            };

            if (text.Length > 0)
                return text;
        }

        // Expanded, so a default can refer to the configured account.
        //
        // This is what makes "my open tickets" mean the person at the keyboard: the JQL
        // default is `assignee = "${JIRA_USER}"`, and without expansion it went to Jira as
        // the literal text ${JIRA_USER} -- which is not an error, it is a filter that matches
        // a user of that name, so the answer comes back empty or, worse, wrong. Only baseUrl
        // and headers were expanded before, which is exactly the sort of gap that shows up as
        // "it lists everybody's tickets".
        return Expand(parameter.Default ?? string.Empty) is { Length: > 0 } expanded
            ? expanded
            : null;
    }

    /// <summary>Put a value into the body, honouring a dotted target such as fields.summary.</summary>
    private static void Place(JsonObject body, string path, string value)
    {
        string[] steps = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        JsonObject current = body;

        for (int i = 0; i < steps.Length - 1; i++)
        {
            if (current[steps[i]] is JsonObject existing)
            {
                current = existing;
                continue;
            }

            var created = new JsonObject();
            current[steps[i]] = created;
            current = created;
        }

        // A value that is itself JSON goes in as JSON rather than as a string. That is what
        // lets a manifest declare a structured field -- Jira's issuetype and project are
        // objects -- without the manifest needing a type system.
        string last = steps[^1];

        if (value.StartsWith('{') || value.StartsWith('['))
        {
            try
            {
                current[last] = JsonNode.Parse(value);
                return;
            }
            catch (JsonException)
            {
                // Not JSON after all. Falls through to the string, which is what the caller
                // most likely meant if it merely began with a brace.
            }
        }

        current[last] = value;
    }

    /// <summary>Environment variable first, then the secret store.</summary>
    private static string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string? fromEnvironment = Environment.GetEnvironmentVariable(name);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment;

        return SecretStore.Get(name);
    }

    /// <summary>Expand a ${VAR} reference in a header value.</summary>
    private static string Expand(string value)
    {
        if (!value.Contains("${", StringComparison.Ordinal))
            return value;

        var sb = new StringBuilder(value);

        foreach (System.Text.RegularExpressions.Match match in
            System.Text.RegularExpressions.Regex.Matches(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}"))
        {
            string? resolved = Resolve(match.Groups[1].Value);

            // An unresolved reference is left literal rather than emptied, the rule the
            // config already follows: an empty string is a valid-looking wrong setting,
            // while ${NAME} shows up in whatever error it causes.
            if (resolved is { Length: > 0 })
                sb.Replace(match.Value, resolved);
        }

        return sb.ToString();
    }

    public void Dispose() => _http.Dispose();
}
