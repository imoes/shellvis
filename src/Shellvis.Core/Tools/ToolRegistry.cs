using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Shellvis.Core.Tools;

/// <summary>One registered tool: its metadata plus the callable the model invokes.</summary>
/// <param name="Name">Name as the model sees it.</param>
/// <param name="Description">When to use it.</param>
/// <param name="SideEffect">Whether it may run without asking.</param>
/// <param name="PreviewParameter">Which argument to show in the console preview.</param>
/// <param name="Glyph">Console glyph.</param>
/// <param name="Function">The invocable function, carrying its generated JSON Schema.</param>
public sealed record ToolEntry(
    string Name,
    string Description,
    SideEffect SideEffect,
    string? PreviewParameter,
    string? Glyph,
    AIFunction Function)
{
    /// <summary>
    /// A one-line summary of a pending call, for the console transcript.
    ///
    /// Shows the argument that identifies the target rather than the whole argument
    /// object: "read_file config.yaml" is readable at a glance, a JSON blob is not.
    /// </summary>
    public string Preview(IReadOnlyDictionary<string, object?> arguments)
    {
        if (PreviewParameter is not null
            && arguments.TryGetValue(PreviewParameter, out object? value)
            && value is not null)
        {
            string text = value.ToString() ?? string.Empty;
            if (text.Length > 90)
                text = text[..90] + "...";

            return $"{Name}  {text}";
        }

        return Name;
    }
}

/// <summary>
/// The catalog of tools the model can call.
///
/// Two properties matter more than the code volume here.
///
/// Schemas are generated, never written. <see cref="AIFunctionFactory"/> derives the
/// JSON Schema and the argument binding from the method signature, so a renamed
/// parameter cannot silently diverge from what the model was told, and arguments
/// arrive already typed. Hermes writes each schema as a literal dict and then needs a
/// separate coercion pass because models send "42" where an int was meant; that whole
/// class of bug does not exist here.
///
/// Registration refuses to overwrite. A tool name is how the model addresses a
/// capability, so silently shadowing one is a correctness bug rather than a
/// convenience -- collisions throw.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolEntry> _tools = new(StringComparer.Ordinal);

    /// <summary>Every registered tool, in registration order.</summary>
    public IReadOnlyList<ToolEntry> Tools => _tools.Values.ToList();

    /// <summary>Tool count, for status lines.</summary>
    public int Count => _tools.Count;

    /// <summary>
    /// Register every <see cref="ShellvisToolAttribute"/>-marked method on an object.
    ///
    /// Instance methods are supported on purpose: tools that hold state across calls
    /// are the norm rather than the exception here. The desktop tools have to remember
    /// which live element "@e12" referred to, and a PowerShell tool has to keep its
    /// runspace alive between turns.
    /// </summary>
    public void RegisterFrom(object target)
    {
        ArgumentNullException.ThrowIfNull(target);

        MethodInfo[] methods = target.GetType().GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (MethodInfo method in methods)
        {
            var attribute = method.GetCustomAttribute<ShellvisToolAttribute>();
            if (attribute is null)
                continue;

            Register(attribute, method, target);
        }
    }

    private void Register(ShellvisToolAttribute attribute, MethodInfo method, object? target)
    {
        if (_tools.ContainsKey(attribute.Name))
        {
            throw new InvalidOperationException(
                $"Tool '{attribute.Name}' is already registered. A tool name is how the "
                + "model addresses a capability, so shadowing one is never safe.");
        }

        string description = attribute.Description
            ?? $"Invokes {method.Name}."; // Better than empty, but a real description belongs on the attribute.

        AIFunction function = AIFunctionFactory.Create(
            method,
            target,
            new AIFunctionFactoryOptions
            {
                Name = attribute.Name,
                Description = description,
            });

        _tools[attribute.Name] = new ToolEntry(
            Name: attribute.Name,
            Description: description,
            SideEffect: attribute.SideEffect,
            PreviewParameter: attribute.PreviewParameter,
            Glyph: attribute.Glyph,
            Function: function);
    }

    /// <summary>
    /// Register a function that was not declared in C#.
    ///
    /// This is how MCP tools get in. They arrive from a remote server already carrying
    /// their own JSON Schema, so there is nothing to generate -- but they still need a
    /// side effect, and a remote server cannot be trusted to declare its own. The
    /// caller assigns one, and for MCP that assignment is deliberately pessimistic.
    /// </summary>
    /// <param name="function">The callable, with its schema already attached.</param>
    /// <param name="sideEffect">How dangerous it is. Never inferred from the server.</param>
    /// <param name="name">
    /// Name as the model sees it, usually namespaced. Defaults to the function's own
    /// name, which risks colliding with a built-in.
    /// </param>
    public void RegisterFunction(
        AIFunction function,
        SideEffect sideEffect,
        string? name = null,
        string? previewParameter = null,
        string? glyph = null)
    {
        ArgumentNullException.ThrowIfNull(function);

        string key = name ?? function.Name;

        if (_tools.ContainsKey(key))
        {
            throw new InvalidOperationException(
                $"Tool '{key}' is already registered. A tool name is how the model "
                + "addresses a capability, so shadowing one is never safe.");
        }

        _tools[key] = new ToolEntry(
            Name: key,
            Description: function.Description,
            SideEffect: sideEffect,
            PreviewParameter: previewParameter,
            Glyph: glyph,
            Function: function);
    }

    /// <summary>
    /// Remove a tool.
    ///
    /// Needed because an MCP server can change its tool list while connected, and
    /// leaving a vanished tool advertised means the model calls something that is no
    /// longer there.
    /// </summary>
    public bool Deregister(string name) => _tools.Remove(name);

    /// <summary>Remove every tool whose name starts with a prefix, for unloading one MCP server.</summary>
    public int DeregisterPrefixed(string prefix)
    {
        List<string> matching = _tools.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        foreach (string key in matching)
            _tools.Remove(key);

        return matching.Count;
    }

    /// <summary>Look up a tool, or null when the model invented a name.</summary>
    public ToolEntry? Find(string name) =>
        _tools.TryGetValue(name, out ToolEntry? entry) ? entry : null;

    /// <summary>
    /// The functions to advertise to the model, as the chat abstraction expects them.
    /// </summary>
    public IList<AITool> AsChatTools() =>
        _tools.Values.Select(t => (AITool)t.Function).ToList();

    /// <summary>
    /// Invoke a tool by name with JSON arguments.
    ///
    /// Failures come back as text rather than as thrown exceptions, because a tool
    /// error is information the model must be able to read and recover from. Throwing
    /// would abort the turn and lose the very message that explains what went wrong.
    /// </summary>
    public async Task<string> InvokeAsync(
        string name,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ToolEntry? entry = Find(name);
        if (entry is null)
            return DescribeUnknownTool(name);

        try
        {
            var arguments2 = new AIFunctionArguments(ParseArguments(arguments));
            object? result = await entry.Function
                .InvokeAsync(arguments2, cancellationToken)
                .ConfigureAwait(false);

            return Stringify(result);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the user interrupting, not a tool failure. It has to
            // propagate so the turn loop can unwind.
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad: a tool reaches into other processes, the registry,
            // the network and COM. Any of those can throw something unforeseen, and
            // none of it justifies taking the session down.
            return $"error: {entry.Name} failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Explain an unknown tool name, leading with the likely intended match.
    ///
    /// Listing all thirty-odd tool names is technically complete and practically
    /// useless: a live run showed a model ask for "echo", receive the full catalogue,
    /// and simply report the error instead of retrying with "mcp_probe_echo" -- which
    /// was in the list it had just been handed. Naming the near match makes the
    /// recovery obvious rather than merely possible.
    /// </summary>
    /// <inheritdoc cref="DescribeUnknownTool"/>
    public string DescribeUnknown(string name) => DescribeUnknownTool(name);

    private string DescribeUnknownTool(string name)
    {
        List<string> close = _tools.Keys
            .Where(k => IsNearMatch(k, name))
            .OrderBy(k => k.Length)
            .Take(3)
            .ToList();

        if (close.Count > 0)
        {
            return $"error: no tool named '{name}'. Did you mean {string.Join(" or ", close)}? "
                + "Tools from MCP servers are prefixed with mcp_<server>_.";
        }

        // No near match, so the full list is the only useful answer left.
        return $"error: no tool named '{name}'. Available tools: {string.Join(", ", _tools.Keys.Order())}";
    }

    /// <summary>
    /// Whether a registered name plausibly answers a requested one.
    ///
    /// Suffix and substring rather than edit distance: the dominant real case is a
    /// namespaced tool being asked for by its bare name, where the requested string is
    /// contained in the registered one.
    /// </summary>
    private static bool IsNearMatch(string registered, string requested)
    {
        if (requested.Length < 3)
            return false;

        return registered.EndsWith(requested, StringComparison.OrdinalIgnoreCase)
            || registered.Contains(requested, StringComparison.OrdinalIgnoreCase)
            || requested.Contains(registered, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> ParseArguments(JsonElement arguments)
    {
        var parsed = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (arguments.ValueKind != JsonValueKind.Object)
            return parsed;

        foreach (JsonProperty property in arguments.EnumerateObject())
            parsed[property.Name] = property.Value;

        return parsed;
    }

    /// <summary>
    /// Render a tool result as the text the model will read.
    ///
    /// The JsonElement case is the one that matters. AIFunction serializes whatever a
    /// tool returns, so a method declared to return string arrives here as a JSON
    /// string element, not as a string. Passing that straight through would deliver
    /// every result double-encoded, with literal \r\n and ö escapes -- harder to
    /// read and a pure waste of tokens on a long transcript.
    /// </summary>
    private static string Stringify(object? result) => result switch
    {
        null => "(no output)",
        string text => text,

        // An MCP tool returns typed content parts rather than a bare string. Letting
        // these fall through to the serializer delivers {"$type":"text","Text":"..."}
        // to the model, which reads protocol scaffolding instead of the answer and pays
        // tokens for it on every call.
        TextContent single => single.Text,
        IEnumerable<AIContent> parts => JoinContent(parts),

        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement { ValueKind: JsonValueKind.Null } => "(no output)",
        JsonElement element => UnwrapContent(element),
        _ => JsonSerializer.Serialize(result, JsonOptions),
    };

    /// <summary>
    /// Flatten a sequence of content parts to text.
    ///
    /// Non-text parts are named rather than dropped: a tool that returned an image has
    /// told the model something, and silently discarding it would leave the model
    /// believing the call produced nothing.
    /// </summary>
    private static string JoinContent(IEnumerable<AIContent> parts)
    {
        var pieces = new List<string>();

        foreach (AIContent part in parts)
        {
            switch (part)
            {
                case TextContent text when text.Text.Length > 0:
                    pieces.Add(text.Text);
                    break;

                case DataContent data:
                    pieces.Add($"[{data.MediaType} content, not shown]");
                    break;

                case UriContent uri:
                    pieces.Add($"[{uri.MediaType} at {uri.Uri}]");
                    break;
            }
        }

        return pieces.Count == 0 ? "(no output)" : string.Join("\n", pieces);
    }

    /// <summary>
    /// Unwrap MCP content blocks down to their text.
    ///
    /// An MCP tool returns content parts rather than a bare string, so the result
    /// arrives as {"$type":"text","Text":"..."} or as an array of those. Passing that
    /// through verbatim means the model reads protocol scaffolding instead of the
    /// answer -- and pays tokens for it on every single call.
    /// </summary>
    private static string UnwrapContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("Text", out JsonElement text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();

            foreach (JsonElement item in element.EnumerateArray())
            {
                string part = UnwrapContent(item);
                if (part.Length > 0)
                    parts.Add(part);
            }

            // Only collapse when every part actually yielded text; a mixed array of
            // text and images should keep its structure rather than silently dropping
            // the non-text parts.
            if (parts.Count > 0)
                return string.Join("\n", parts);
        }

        return element.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
