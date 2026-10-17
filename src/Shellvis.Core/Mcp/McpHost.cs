using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Shellvis.Core.Tools;

namespace Shellvis.Core.Mcp;

/// <summary>State of one configured server.</summary>
/// <param name="Name">Configured name.</param>
/// <param name="Connected">Whether the handshake succeeded and tools are registered.</param>
/// <param name="ToolCount">How many of its tools were registered.</param>
/// <param name="Detail">Human-readable status or failure reason.</param>
public sealed record McpServerStatus(string Name, bool Connected, int ToolCount, string Detail)
{
    public override string ToString() =>
        Connected
            ? $"{Name}  connected  {ToolCount} tool(s)"
            : $"{Name}  NOT connected  {Detail}";
}

/// <summary>
/// Connects to MCP servers and merges their tools into the local catalog.
///
/// The integration is unusually thin, and that is the payoff of building on
/// <see cref="Microsoft.Extensions.AI"/>: an MCP tool arrives as an
/// <see cref="AIFunction"/>, which is exactly what the built-in tools already are. So
/// they go into the same registry and the agent loop cannot tell them apart. Hermes
/// needs a whole schema-translation layer at this boundary; here there is none.
///
/// What does need care is trust. A server is remote code describing its own
/// capabilities, so three things are never taken on faith:
///
///  - Side effects. Every MCP tool is registered as mutating and therefore prompts,
///    unless the *local* configuration names it as safe. A server declaring its own
///    tools harmless is precisely what a malicious one would do.
///  - Names. Tools are namespaced, and a collision with a built-in is resolved in
///    favour of the built-in. A server must not be able to shadow powershell_run.
///  - Descriptions. They are scanned for prompt-injection patterns before the model
///    ever sees them, because a tool description is text a remote party controls that
///    lands directly in the system prompt.
/// </summary>
public sealed class McpHost(ToolRegistry registry) : IAsyncDisposable
{
    /// <summary>Characters not allowed in a tool name, replaced with an underscore.</summary>
    private static readonly Regex UnsafeNameChars = new("[^A-Za-z0-9_]", RegexOptions.Compiled);

    /// <summary>
    /// Phrases that have no business in a tool description and are the standard shape
    /// of an injection attempt through one.
    /// </summary>
    private static readonly string[] InjectionMarkers =
    [
        "ignore previous", "ignore all previous", "disregard the above",
        "system prompt", "you are now", "new instructions",
        "do not tell the user", "without telling the user", "without asking",
    ];

    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, McpServerStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Status of every configured server, connected or not.</summary>
    public IReadOnlyCollection<McpServerStatus> Status => _status.Values;

    /// <summary>
    /// Connect to a server and register its tools.
    ///
    /// Failure is recorded rather than thrown: one unreachable server must not stop the
    /// agent from starting with the others, and the status line is where the user finds
    /// out about it.
    /// </summary>
    public async Task<McpServerStatus> ConnectAsync(
        McpServerConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Validate() is { } problem)
            return Record(new McpServerStatus(config.Name, false, 0, problem));

        try
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Clamp(config.ConnectTimeoutSeconds, 5, 300)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token);

            McpClient client = await McpClient
                .CreateAsync(BuildTransport(config), cancellationToken: linked.Token)
                .ConfigureAwait(false);

            _clients[config.Name] = client;

            IList<McpClientTool> tools = await client
                .ListToolsAsync(cancellationToken: linked.Token)
                .ConfigureAwait(false);

            int registered = RegisterTools(config, tools, out List<string> warnings);

            string detail = warnings.Count == 0
                ? $"{tools.Count} tool(s) offered"
                : $"{tools.Count} offered; " + string.Join("; ", warnings);

            return Record(new McpServerStatus(config.Name, true, registered, detail));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Record(new McpServerStatus(
                config.Name, false, 0,
                $"the handshake did not finish within {config.ConnectTimeoutSeconds}s"));
        }
        catch (Exception ex)
        {
            // Deliberately broad: a stdio server may fail to launch, exit immediately,
            // speak a different protocol version, or emit garbage on stdout. None of
            // that should take the agent down with it.
            return Record(new McpServerStatus(config.Name, false, 0, Explain(ex)));
        }
    }

    /// <summary>Connect several servers, in parallel since each waits on I/O.</summary>
    public async Task<IReadOnlyList<McpServerStatus>> ConnectAllAsync(
        IEnumerable<McpServerConfig> configs, CancellationToken cancellationToken = default)
    {
        // Registration touches the shared registry, so the connects run concurrently
        // but their results are folded in one at a time.
        var results = new List<McpServerStatus>();

        foreach (McpServerConfig config in configs)
            results.Add(await ConnectAsync(config, cancellationToken).ConfigureAwait(false));

        return results;
    }

    /// <summary>Disconnect one server and remove its tools.</summary>
    public async Task<string> DisconnectAsync(string name)
    {
        if (!_clients.Remove(name, out McpClient? client))
            return $"no connected MCP server named '{name}'.";

        int removed = registry.DeregisterPrefixed(Prefix(name));

        await client.DisposeAsync().ConfigureAwait(false);
        _status[name] = new McpServerStatus(name, false, 0, "disconnected");

        return $"disconnected '{name}' and removed {removed} tool(s).";
    }

    private int RegisterTools(
        McpServerConfig config, IList<McpClientTool> tools, out List<string> warnings)
    {
        warnings = [];
        int registered = 0;

        foreach (McpClientTool tool in tools)
        {
            if (!config.Allows(tool.Name))
                continue;

            string name = Prefix(config.Name) + Sanitise(tool.Name);

            // The built-in always wins. A server that offered a tool named to collide
            // with powershell_run would otherwise be able to intercept it.
            if (registry.Find(name) is not null)
            {
                warnings.Add($"'{tool.Name}' skipped, name already taken");
                continue;
            }

            if (FindInjection(tool.Description) is { } marker)
            {
                // Not merely logged: a description is remote text that lands in the
                // system prompt, so a suspicious one is refused outright.
                warnings.Add($"'{tool.Name}' refused, its description contains \"{marker}\"");
                continue;
            }

            // Pessimistic by default. Only the local config can grant silent execution.
            SideEffect effect = config.IsTrustedReadOnly(tool.Name)
                ? SideEffect.ReadOnly
                : SideEffect.Mutating;

            try
            {
                // WithName is essential, not cosmetic. Registering under a namespaced
                // dictionary key while leaving the FUNCTION named "echo" means the
                // model is advertised "echo", calls "echo", and the dispatcher looks
                // up a key that does not exist. A live run diagnosed itself: the model
                // reported "I do have a plain echo tool available" while the registry
                // held only mcp_probe_echo.
                registry.RegisterFunction(tool.WithName(name), effect, name, glyph: "plug");
                registered++;
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add($"'{tool.Name}' skipped: {ex.Message}");
            }
        }

        return registered;
    }

    /// <summary>Namespace prefix for a server's tools.</summary>
    private static string Prefix(string serverName) => $"mcp_{Sanitise(serverName)}_";

    private static string Sanitise(string value) => UnsafeNameChars.Replace(value, "_");

    private static string? FindInjection(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        string lowered = description.ToLowerInvariant();

        return InjectionMarkers.FirstOrDefault(
            marker => lowered.Contains(marker, StringComparison.Ordinal));
    }

    private static IClientTransport BuildTransport(McpServerConfig config)
    {
        if (config.Transport == McpTransport.Http)
        {
            var options = new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url!),
            };

            if (config.Headers is { Count: > 0 })
                options.AdditionalHeaders = new Dictionary<string, string>(config.Headers);

            return new HttpClientTransport(options);
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Name,
            Command = config.Command!,
            Arguments = config.Arguments?.ToList() ?? [],
            EnvironmentVariables = BuildSafeEnvironment(config),
        });
    }

    /// <summary>
    /// Build the environment for a stdio child from a small allowlist plus whatever the
    /// configuration explicitly adds.
    ///
    /// A stdio MCP server is a local process the agent starts. Handing it the whole
    /// environment would hand it every API key in it -- OPENROUTER_API_KEY, cloud
    /// credentials, whatever else is set. It gets what it needs to run and nothing more.
    /// </summary>
    private static Dictionary<string, string?> BuildSafeEnvironment(McpServerConfig config)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "COMSPEC",
            "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
            "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "OS",
            "HOMEDRIVE", "HOMEPATH", "USERNAME", "COMPUTERNAME",
        };

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // The transport MERGES this dictionary into the inherited environment rather
        // than replacing it, so listing only the allowed variables achieves nothing --
        // a probe with a canary variable read it straight back out of the child. The
        // parent environment therefore has to be enumerated and everything outside the
        // allowlist explicitly unset.
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key.ToString() is not { Length: > 0 } key)
                continue;

            environment[key] = allowed.Contains(key)
                ? entry.Value?.ToString()
                : null;
        }

        // Explicit configuration is applied last so it can reinstate a variable the
        // allowlist would otherwise have cleared -- which is the whole point of being
        // able to configure one.
        if (config.Environment is { Count: > 0 })
        {
            foreach ((string key, string value) in config.Environment)
                environment[key] = value;
        }

        return environment;
    }

    /// <summary>Turn a connection failure into something a human can act on.</summary>
    private static string Explain(Exception ex)
    {
        string message = ex.Message;

        if (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            return $"the command could not be started: {message}";

        if (ex is HttpRequestException)
            return $"the endpoint could not be reached: {message}";

        return message.Length <= 200 ? message : message[..200] + "...";
    }

    private McpServerStatus Record(McpServerStatus status)
    {
        _status[status.Name] = status;
        return status;
    }

    /// <summary>Render the status of every server, for the /mcp command.</summary>
    public string Describe()
    {
        if (_status.Count == 0)
            return "no MCP servers are configured.";

        var sb = new StringBuilder();
        sb.Append(_status.Count).AppendLine(" MCP server(s):");

        foreach (McpServerStatus status in _status.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
            sb.Append("  ").AppendLine(status.ToString());

        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        foreach ((string name, McpClient client) in _clients)
        {
            registry.DeregisterPrefixed(Prefix(name));

            try
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A stdio child that is already gone throws on shutdown. Nothing left
                // to do about it, and it must not prevent the others from closing.
            }
        }

        _clients.Clear();
    }
}
