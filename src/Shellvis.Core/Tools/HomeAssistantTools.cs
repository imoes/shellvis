using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shellvis.Core.HomeAssistant;

namespace Shellvis.Core.Tools;

/// <summary>
/// Home Assistant as four tools: what exists, what one thing is doing, what can be
/// called, and calling it.
///
/// The split is deliberate. A single "do something in the house" tool would need the
/// model to know entity ids it has never seen; four tools let it look, narrow, and then
/// act -- the same look-then-act shape the desktop tools use, and for the same reason:
/// an id guessed from a room name is wrong often enough to matter when the result is a
/// physical change.
/// </summary>
public sealed class HomeAssistantTools(HomeAssistantClient client)
{
    private readonly HomeAssistantClient _client = client;

    [ShellvisTool(
        "ha_list_entities",
        SideEffect.ReadOnly,
        Description =
            "List Home Assistant entities with their current state. Filter by domain "
            + "(light, switch, sensor, climate, cover, media_player) or by a word from "
            + "the name -- an unfiltered house can run to hundreds of entities. Use "
            + "this first to find the entity_id that the other tools take.",
        PreviewParameter = "filter",
        Glyph = "house")]
    public async Task<string> ListEntities(
        string? domain = null,
        string? filter = null,
        int limit = 80,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HomeAssistantEntity> entities =
            await _client.GetStatesAsync(cancellationToken).ConfigureAwait(false);

        if (entities.Count == 0)
            return "Home Assistant reports no entities.";

        int total = entities.Count;

        IEnumerable<HomeAssistantEntity> matches = entities;

        if (!string.IsNullOrWhiteSpace(domain))
        {
            string wanted = domain.Trim().TrimEnd('.');
            matches = matches.Where(e =>
                e.Domain.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            string needle = filter.Trim();

            // The friendly name is matched as well as the id, because that is what the
            // user will have said out loud ("the office lamp"), while the id is often
            // a slug nobody chose.
            matches = matches.Where(e =>
                e.EntityId.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (e.FriendlyName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        List<HomeAssistantEntity> found = [.. matches.OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase)];

        if (found.Count == 0)
        {
            // Naming the domains that do exist turns a dead end into the next step.
            string domains = string.Join(", ", entities
                .Select(e => e.Domain)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase));

            return $"No entity matches. {total} entities exist, in these domains: {domains}.";
        }

        var sb = new StringBuilder();
        sb.Append(found.Count).Append(" of ").Append(total).AppendLine(" entities:");

        foreach (HomeAssistantEntity entity in found.Take(limit))
            sb.Append("  ").AppendLine(entity.ToString());

        if (found.Count > limit)
        {
            sb.Append("  ... ").Append(found.Count - limit)
                .AppendLine(" more; narrow the filter to see them.");
        }

        return sb.ToString();
    }

    [ShellvisTool(
        "ha_get_state",
        SideEffect.ReadOnly,
        Description =
            "Read one Home Assistant entity in full, including its attributes -- "
            + "brightness and colour for a light, temperature and mode for a "
            + "thermostat. Use it when the state string alone is not enough.",
        PreviewParameter = "entityId",
        Glyph = "house")]
    public async Task<string> GetState(
        string entityId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return "Give an entity_id, for example light.office. ha_list_entities finds them.";

        HomeAssistantEntity? entity = await _client
            .GetStateAsync(entityId.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (entity is null)
        {
            return $"Home Assistant does not know '{entityId}'. "
                + "Use ha_list_entities with a filter to find the right id.";
        }

        var sb = new StringBuilder();
        sb.AppendLine(entity.ToString());

        if (entity.Attributes is { } attributes && attributes.Count > 0)
        {
            sb.AppendLine("attributes:");

            foreach (KeyValuePair<string, JsonNode?> pair in attributes)
            {
                // friendly_name is already on the first line; repeating it in a house
                // with fifty entities is pure waste over a long transcript.
                if (pair.Key is "friendly_name")
                    continue;

                sb.Append("  ").Append(pair.Key).Append(": ")
                    .AppendLine(Describe(pair.Value));
            }
        }

        return sb.ToString();
    }

    [ShellvisTool(
        "ha_list_services",
        SideEffect.ReadOnly,
        Description =
            "List what can be called in Home Assistant, per domain, with the data "
            + "fields each service accepts. Call this before ha_call_service when the "
            + "service name or its parameters are not certain.",
        PreviewParameter = "domain",
        Glyph = "house")]
    public async Task<string> ListServices(
        string? domain = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HomeAssistantDomain> domains =
            await _client.GetServicesAsync(cancellationToken).ConfigureAwait(false);

        if (domains.Count == 0)
            return "Home Assistant reports no services.";

        var sb = new StringBuilder();

        if (string.IsNullOrWhiteSpace(domain))
        {
            // Without a domain only the index is returned. Every service of every
            // domain with its fields is tens of thousands of characters and would be
            // paid for on every subsequent turn in the context.
            sb.Append(domains.Count).AppendLine(" service domain(s):");

            foreach (HomeAssistantDomain d in domains.OrderBy(d => d.Domain, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("  ").Append(d.Domain).Append(" (").Append(d.Services.Count)
                    .Append("): ")
                    .AppendLine(string.Join(", ", d.Services.Take(8).Select(s => s.Name))
                        + (d.Services.Count > 8 ? ", ..." : string.Empty));
            }

            sb.AppendLine("Pass a domain to see the fields each of its services takes.");
            return sb.ToString();
        }

        string wanted = domain.Trim();

        HomeAssistantDomain? match = domains.FirstOrDefault(d =>
            d.Domain.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            string names = string.Join(", ", domains
                .Select(d => d.Domain)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase));

            return $"No service domain '{wanted}'. Available: {names}.";
        }

        sb.Append(match.Domain).Append(": ").Append(match.Services.Count).AppendLine(" service(s)");

        foreach (HomeAssistantService service in match.Services.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("  ").Append(match.Domain).Append('.').Append(service.Name);

            if (service.Fields.Count > 0)
                sb.Append("  fields: ").Append(string.Join(", ", service.Fields));

            sb.AppendLine();

            if (service.Description is { Length: > 0 } description)
                sb.Append("      ").AppendLine(Shorten(description));
        }

        return sb.ToString();
    }

    [ShellvisTool(
        "ha_call_service",
        SideEffect.Mutating,
        Description =
            "Call a Home Assistant service to change something: turn a light on, set a "
            + "thermostat, open a cover. Pass the target entity_id and, if the service "
            + "takes any, extra data as a JSON object such as "
            + "{\"brightness_pct\": 40}. Returns the state of everything that changed.",
        PreviewParameter = "service",
        Glyph = "house")]
    public async Task<string> CallService(
        string domain,
        string service,
        string? entityId = null,
        string? dataJson = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(service))
            return "Give both a domain and a service, for example light and turn_on.";

        var data = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(dataJson))
        {
            JsonObject? parsed;

            try
            {
                parsed = JsonNode.Parse(dataJson) as JsonObject;
            }
            catch (JsonException ex)
            {
                // Reported rather than thrown, so the model sees what was wrong with
                // its own argument and can fix it in the next round.
                return $"dataJson is not valid JSON: {ex.Message}";
            }

            if (parsed is null)
                return "dataJson must be a JSON object, for example {\"brightness_pct\": 40}.";

            foreach (KeyValuePair<string, JsonNode?> pair in parsed)
                data[pair.Key] = pair.Value?.DeepClone();
        }

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            // A separate parameter as well as the data bag, because entity_id is the
            // target of nearly every call and burying it in free-form JSON makes it
            // invisible in the approval prompt -- which is exactly where the user needs
            // to see which device is about to move.
            data["entity_id"] = entityId.Trim();
        }

        IReadOnlyList<HomeAssistantEntity> changed = await _client
            .CallServiceAsync(domain.Trim(), service.Trim(), data, cancellationToken)
            .ConfigureAwait(false);

        if (changed.Count == 0)
        {
            // Home Assistant accepts a call it cannot route and returns an empty list,
            // so this is not proof of success and must not be reported as such.
            return $"{domain}.{service} was accepted, but Home Assistant reported no "
                + "changed entity. Read the state back with ha_get_state to confirm.";
        }

        var sb = new StringBuilder();
        sb.Append(domain).Append('.').Append(service).Append(" changed ")
            .Append(changed.Count).AppendLine(" entity/entities:");

        foreach (HomeAssistantEntity entity in changed)
            sb.Append("  ").AppendLine(entity.ToString());

        return sb.ToString();
    }

    /// <summary>
    /// Render an attribute value compactly.
    ///
    /// Lists are summarised rather than dumped: a media player's source list or a
    /// climate entity's preset modes can be long, and the point of showing attributes
    /// is to reveal what is settable, not to reproduce the whole integration.
    /// </summary>
    private static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray array when array.Count > 6 =>
            $"[{string.Join(", ", array.Take(6).Select(v => v?.ToJsonString()))}, ... {array.Count} total]",
        JsonArray array => $"[{string.Join(", ", array.Select(v => v?.ToJsonString()))}]",
        JsonObject obj => Shorten(obj.ToJsonString()),
        _ => node.ToString(),
    };

    private static string Shorten(string text)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length > 200 ? flat[..200] + " ..." : flat;
    }
}
