using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shellvis.Core.HomeAssistant;

/// <summary>
/// A thin client over the Home Assistant REST API.
///
/// Deliberately thin: Home Assistant's own API is already a good shape for an agent --
/// entities carry a human-readable friendly name, a state string and a bag of
/// attributes, and services are addressed as domain plus name. Wrapping that in a
/// domain model would mean guessing at the schema of every integration a user happens
/// to have installed, and getting it wrong for the next one. The JSON is passed through
/// with only the flattening the model actually needs to read it.
/// </summary>
public sealed class HomeAssistantClient : IDisposable
{
    /// <summary>Environment variable holding the long-lived access token.</summary>
    public const string TokenVariable = "HASS_TOKEN";

    /// <summary>Environment variable holding the base url, when the config does not.</summary>
    public const string UrlVariable = "HASS_URL";

    private readonly HttpClient _http;

    public HomeAssistantClient(string baseUrl, string token, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Home Assistant serves its API under /api/, so the base has to end in a slash
        // or Uri composition silently drops the last path segment of a reverse-proxied
        // install (https://host/hass -> https://host/api/states).
        string normalised = baseUrl.TrimEnd('/') + "/";

        _http = new HttpClient
        {
            BaseAddress = new Uri(normalised, UriKind.Absolute),
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Build a client from configuration, or explain why one is not available.
    ///
    /// Returns null rather than throwing when there is no token: an absent Home
    /// Assistant is the normal case, not an error, and it decides whether the tools are
    /// offered at all.
    /// </summary>
    public static HomeAssistantClient? TryCreate(string? baseUrl, string? tokenVariable = null)
    {
        string? token = Environment.GetEnvironmentVariable(tokenVariable ?? TokenVariable);

        if (string.IsNullOrWhiteSpace(token))
            return null;

        string? url = baseUrl;

        if (string.IsNullOrWhiteSpace(url))
            url = Environment.GetEnvironmentVariable(UrlVariable);

        if (string.IsNullOrWhiteSpace(url))
            return null;

        return new HomeAssistantClient(url, token);
    }

    /// <summary>Every entity with its current state.</summary>
    public async Task<IReadOnlyList<HomeAssistantEntity>> GetStatesAsync(
        CancellationToken cancellationToken = default)
    {
        JsonNode? node = await GetJsonAsync("api/states", cancellationToken).ConfigureAwait(false);

        if (node is not JsonArray array)
            return [];

        var entities = new List<HomeAssistantEntity>(array.Count);

        foreach (JsonNode? item in array)
        {
            if (HomeAssistantEntity.TryParse(item) is { } entity)
                entities.Add(entity);
        }

        return entities;
    }

    /// <summary>One entity, or null when Home Assistant does not know it.</summary>
    public async Task<HomeAssistantEntity?> GetStateAsync(
        string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        using HttpResponseMessage response = await _http
            .GetAsync($"api/states/{Uri.EscapeDataString(entityId)}", cancellationToken)
            .ConfigureAwait(false);

        // A missing entity is an ordinary answer to an ordinary question, not a fault.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        JsonNode? node = await response.Content
            .ReadFromJsonAsync<JsonNode>(cancellationToken)
            .ConfigureAwait(false);

        return HomeAssistantEntity.TryParse(node);
    }

    /// <summary>The services each domain exposes.</summary>
    public async Task<IReadOnlyList<HomeAssistantDomain>> GetServicesAsync(
        CancellationToken cancellationToken = default)
    {
        JsonNode? node = await GetJsonAsync("api/services", cancellationToken).ConfigureAwait(false);

        if (node is not JsonArray array)
            return [];

        var domains = new List<HomeAssistantDomain>(array.Count);

        foreach (JsonNode? item in array)
        {
            if (HomeAssistantDomain.TryParse(item) is { } domain)
                domains.Add(domain);
        }

        return domains;
    }

    /// <summary>
    /// Call a service and return the entities Home Assistant reports as changed.
    ///
    /// That return value is the reason this is worth more than a fire-and-forget POST:
    /// Home Assistant answers with the post-call state of everything the service
    /// touched, which is the difference between "the call was accepted" and "the light
    /// is off".
    /// </summary>
    public async Task<IReadOnlyList<HomeAssistantEntity>> CallServiceAsync(
        string domain,
        string service,
        IReadOnlyDictionary<string, object?>? data = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        string path = $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}";

        using HttpResponseMessage response = await _http
            .PostAsJsonAsync(path, data ?? new Dictionary<string, object?>(), cancellationToken)
            .ConfigureAwait(false);

        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        JsonNode? node = await response.Content
            .ReadFromJsonAsync<JsonNode>(cancellationToken)
            .ConfigureAwait(false);

        if (node is not JsonArray array)
            return [];

        var changed = new List<HomeAssistantEntity>(array.Count);

        foreach (JsonNode? item in array)
        {
            if (HomeAssistantEntity.TryParse(item) is { } entity)
                changed.Add(entity);
        }

        return changed;
    }

    private async Task<JsonNode?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http
            .GetAsync(path, cancellationToken)
            .ConfigureAwait(false);

        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<JsonNode>(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Turn a failure into a message that says what to do about it.
    ///
    /// The body is included because Home Assistant explains service-call rejections
    /// there ("extra keys not allowed @ data['brightnes']"), and that text is what lets
    /// the model correct itself instead of retrying the same call. It is truncated,
    /// because a misrouted url can answer with a whole HTML page.
    /// </summary>
    private static async Task ThrowIfFailedAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (body.Length > 600)
            body = body[..600] + " ...";

        string hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $" The {TokenVariable} token was rejected -- long-lived access tokens can be revoked.",
            HttpStatusCode.NotFound =>
                " Check the base url: the REST API lives under /api on the Home Assistant host.",
            _ => string.Empty,
        };

        throw new HttpRequestException(
            $"Home Assistant returned {(int)response.StatusCode} {response.ReasonPhrase}.{hint} {body}".TrimEnd());
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>One entity as Home Assistant reports it.</summary>
/// <param name="EntityId">Domain-qualified id, for example <c>light.office</c>.</param>
/// <param name="State">The state string, verbatim: on, off, 21.5, unavailable.</param>
/// <param name="FriendlyName">The name a person gave it, when there is one.</param>
/// <param name="Attributes">Everything else, untouched.</param>
public sealed record HomeAssistantEntity(
    string EntityId,
    string State,
    string? FriendlyName,
    JsonObject? Attributes)
{
    /// <summary>Everything before the dot: light, sensor, switch, climate.</summary>
    public string Domain
    {
        get
        {
            int dot = EntityId.IndexOf('.');
            return dot > 0 ? EntityId[..dot] : EntityId;
        }
    }

    /// <summary>The unit for a numeric sensor, so a reading can be shown as read.</summary>
    public string? UnitOfMeasurement =>
        Attributes?["unit_of_measurement"]?.GetValue<string>();

    public static HomeAssistantEntity? TryParse(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return null;

        string? id = obj["entity_id"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(id))
            return null;

        var attributes = obj["attributes"] as JsonObject;

        return new HomeAssistantEntity(
            id,
            obj["state"]?.GetValue<string>() ?? "unknown",
            attributes?["friendly_name"]?.GetValue<string>(),
            attributes);
    }

    /// <summary>
    /// One line per entity, id first.
    ///
    /// The id leads because it is what every other call takes as its argument; the
    /// friendly name is what the user will have said. Both have to be present or the
    /// model has to guess one from the other -- the formatting lesson from
    /// <c>WindowInfo</c>, applied again.
    /// </summary>
    public override string ToString()
    {
        string value = State;

        if (UnitOfMeasurement is { Length: > 0 } unit)
            value += " " + unit;

        string name = FriendlyName is { Length: > 0 } friendly ? $" \"{friendly}\"" : string.Empty;

        return $"{EntityId}{name} = {value}";
    }
}

/// <summary>One domain and the services it offers.</summary>
public sealed record HomeAssistantDomain(string Domain, IReadOnlyList<HomeAssistantService> Services)
{
    public static HomeAssistantDomain? TryParse(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return null;

        string? domain = obj["domain"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(domain))
            return null;

        var services = new List<HomeAssistantService>();

        if (obj["services"] is JsonObject serviceMap)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in serviceMap)
            {
                var body = pair.Value as JsonObject;

                List<string> fields = body?["fields"] is JsonObject fieldMap
                    ? [.. fieldMap.Select(f => f.Key)]
                    : [];

                services.Add(new HomeAssistantService(
                    pair.Key,
                    body?["name"]?.GetValue<string>(),
                    body?["description"]?.GetValue<string>(),
                    fields));
            }
        }

        return new HomeAssistantDomain(domain, services);
    }
}

/// <summary>One callable service.</summary>
/// <param name="Fields">
/// Accepted data keys. Listed by name only: the full field schema of a domain like
/// <c>light</c> runs to hundreds of lines, and the names are what the model needs to
/// build a call it can then correct from the error text if a value is wrong.
/// </param>
public sealed record HomeAssistantService(
    string Name,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string> Fields);
