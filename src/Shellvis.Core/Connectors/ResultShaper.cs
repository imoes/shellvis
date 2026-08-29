using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shellvis.Core.Connectors;

/// <summary>
/// Turning a JSON response into something a model can read.
///
/// <b>This is the part a declarative connector gets wrong if it is left to the manifest.</b>
/// The obvious implementation hands the raw JSON to the model, and that is precisely what
/// the Home Assistant tools refused to do: a page of JSON costs a fortune in context, buries
/// the one field that matters, and gets re-sent on every following turn of the conversation.
///
/// So the rules live here rather than in the package, and no manifest can opt out of them:
///
/// <list type="bullet">
/// <item><b>The count comes before the content.</b> "12 of 340" tells the reader whether to
/// narrow the search before they have read a single line.</item>
/// <item><b>A truncation says so.</b> A list that silently stops looks complete, and a model
/// that thinks it has seen everything answers as if it had.</item>
/// <item><b>The id leads the line.</b> It is the argument every follow-up call takes; put it
/// last and the model has to hunt for it.</item>
/// <item><b>Nothing found is an answer.</b> It is said in words, with the next step named,
/// because an empty result that reads as a failure invites the model to invent one. This
/// application has produced a calendar of six imaginary appointments exactly that way.</item>
/// </list>
/// </summary>
public static class ResultShaper
{
    /// <summary>How many items are rendered before the rest are counted instead.</summary>
    /// <remarks>
    /// Forty is enough to answer "what is on my plate" and short enough that a careless
    /// query cannot fill the context. The number the server reports is still shown, so the
    /// reader learns the search was too broad rather than that the system is small.
    /// </remarks>
    private const int MaxItems = 40;

    /// <summary>A single value is clipped here; a whole description belongs behind a fetch.</summary>
    private const int MaxValue = 300;

    public static string Shape(string json, ConnectorResult? result, string toolName)
    {
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // Not JSON at all. Returned as text rather than as an error: some endpoints
            // answer with a plain string, and a body that cannot be parsed is still the
            // most informative thing available.
            return Clip(json, 2000);
        }

        if (root is null)
            return "(empty response)";

        if (result?.Items is { Length: > 0 } path)
            return ShapeList(root, path, result, toolName);

        return ShapeOne(root, result);
    }

    private static string ShapeList(JsonNode root, string path, ConnectorResult result, string toolName)
    {
        JsonNode? node = Dig(root, path);

        if (node is not JsonArray items)
        {
            // The declared list is not there. Said plainly with what WAS there, because the
            // usual cause is a manifest pointing at the wrong property, and a silent empty
            // list would look like "nothing found" instead of "wrong path".
            return $"{toolName}: the response has no list at '{path}'. "
                + $"It holds: {string.Join(", ", Names(root))}.";
        }

        if (items.Count == 0)
        {
            return result.Empty is { Length: > 0 } empty
                ? empty
                : "nothing found. That is the answer; do not fill it in.";
        }

        int total = result.Total is { Length: > 0 } totalPath
            && Dig(root, totalPath) is JsonValue value
            && value.TryGetValue(out int reported)
                ? reported
                : items.Count;

        var sb = new StringBuilder();

        int shown = Math.Min(items.Count, MaxItems);

        sb.Append(shown == total
            ? string.Create(CultureInfo.InvariantCulture, $"{total} result(s):")
            : string.Create(CultureInfo.InvariantCulture, $"{shown} of {total} result(s):"));

        sb.AppendLine();

        for (int i = 0; i < shown; i++)
            sb.Append("  ").AppendLine(Line(items[i], result.Line));

        if (total > shown)
        {
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"  ... {total - shown} more; narrow the query to see them."));

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ShapeOne(JsonNode root, ConnectorResult? result)
    {
        if (result?.Line is { Length: > 0 } template)
            return Line(root, template);

        // No template: the flattened object, which is far better than the raw JSON and far
        // worse than a template. The manifest is expected to supply one; this is the
        // fallback that keeps a connector usable while it is being written.
        var sb = new StringBuilder();

        if (root is JsonObject obj)
        {
            foreach ((string name, JsonNode? child) in obj)
            {
                string rendered = Render(child);

                if (rendered.Length > 0)
                    sb.Append(name).Append(": ").AppendLine(rendered);
            }

            return sb.ToString();
        }

        return Render(root);
    }

    /// <summary>Fill a line template. An unresolved placeholder becomes empty, not literal.</summary>
    private static string Line(JsonNode? item, string? template)
    {
        if (item is null)
            return string.Empty;

        if (string.IsNullOrWhiteSpace(template))
            return Clip(item.ToJsonString(), MaxValue);

        var sb = new StringBuilder();

        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                sb.Append(template[i]);
                continue;
            }

            int close = template.IndexOf('}', i + 1);

            if (close < 0)
            {
                sb.Append(template[i]);
                continue;
            }

            string path = template[(i + 1)..close];

            // Left empty rather than printed as {fields.status.name}: a placeholder that
            // survives into the answer teaches the model that the template is data.
            sb.Append(Render(Dig(item, path)));

            i = close;
        }

        return sb.ToString().Trim();
    }

    /// <summary>Follow a dotted path. Missing links yield null rather than throwing.</summary>
    private static JsonNode? Dig(JsonNode? node, string path)
    {
        foreach (string step in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(step, out JsonNode? next))
            {
                node = next;
                continue;
            }

            if (node is JsonArray array && int.TryParse(step, out int index)
                && index >= 0 && index < array.Count)
            {
                node = array[index];
                continue;
            }

            return null;
        }

        return node;
    }

    /// <summary>One value as text, with arrays summarised rather than dumped.</summary>
    private static string Render(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value => Clip(value.ToString(), MaxValue),
        JsonArray array when array.Count == 0 => string.Empty,

        // A nested array is named and counted. Expanding it inline is how a single line
        // becomes a paragraph and a list becomes unreadable.
        JsonArray array => string.Create(CultureInfo.InvariantCulture, $"[{array.Count} items]"),
        JsonObject obj => Clip(obj.ToJsonString(), MaxValue),
        _ => string.Empty,
    };

    private static IEnumerable<string> Names(JsonNode node) =>
        node is JsonObject obj ? obj.Select(p => p.Key).Take(12) : ["(not an object)"];

    private static string Clip(string text, int max)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();

        while (flat.Contains("  ", StringComparison.Ordinal))
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);

        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
