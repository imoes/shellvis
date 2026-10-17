using System.Net;
using System.Text;
using System.Text.Json;
using Shellvis.Core.HomeAssistant;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Exercises the Home Assistant tools against a stub HTTP server that speaks the real
/// REST API shapes.
///
/// A stub rather than a live instance, because the property that matters most here is
/// not "can it reach a house" but "does an absent house stay absent": if the ha_* tools
/// are advertised without a token they will be planned around and fail on first use.
/// That is testable without any Home Assistant at all, and it is the check that would
/// otherwise never run on a machine that has none.
/// </summary>
internal static class HomeAssistantProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        Console.WriteLine("=== Home Assistant ===");
        Console.WriteLine();

        failures += Gating();

        using var server = new StubHomeAssistant();
        server.Start();

        using var client = new HomeAssistantClient(server.BaseUrl, "stub-token");
        var tools = new HomeAssistantTools(client);

        failures += await ListingAsync(tools).ConfigureAwait(false);
        failures += await SingleEntityAsync(tools).ConfigureAwait(false);
        failures += await ServicesAsync(tools).ConfigureAwait(false);
        failures += await CallingAsync(tools, server).ConfigureAwait(false);
        failures += await FailuresAsync(tools, server).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "All Home Assistant checks passed."
            : $"{failures} Home Assistant check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The gate: no token means no client, and therefore no tools in the catalogue.
    /// </summary>
    private static int Gating()
    {
        Console.WriteLine("-- availability --");
        int failures = 0;

        const string variable = "SHELLVIS_PROBE_HASS_TOKEN";
        Environment.SetEnvironmentVariable(variable, null);

        failures += Check(
            "no token -> no client",
            HomeAssistantClient.TryCreate("http://homeassistant.local:8123", variable) is null);

        Environment.SetEnvironmentVariable(variable, "a-token");

        // A token with nowhere to send it is just as unusable as no token.
        failures += Check(
            "token but no url -> no client",
            HomeAssistantClient.TryCreate(null, variable) is null);

        using HomeAssistantClient? both =
            HomeAssistantClient.TryCreate("http://homeassistant.local:8123", variable);

        failures += Check("token and url -> client", both is not null);

        Environment.SetEnvironmentVariable(variable, null);

        // And the consequence that actually matters: the registry stays clean.
        var registry = new ToolRegistry();
        registry.RegisterFrom(new DesktopTools());
        int without = registry.Count;

        using var live = new HomeAssistantClient("http://example.invalid", "t");
        registry.RegisterFrom(new HomeAssistantTools(live));

        failures += Check(
            $"four ha_* tools appear once configured ({without} -> {registry.Count})",
            registry.Count == without + 4);

        failures += Check(
            "the four are named ha_list_entities, ha_get_state, ha_list_services, ha_call_service",
            registry.Tools.Any(t => t.Name == "ha_list_entities")
                && registry.Tools.Any(t => t.Name == "ha_get_state")
                && registry.Tools.Any(t => t.Name == "ha_list_services")
                && registry.Tools.Any(t => t.Name == "ha_call_service"));

        // Turning a light off is a change in the physical world; silence is the wrong
        // default for it even in the mode that runs read-only commands unattended.
        failures += Check(
            "ha_call_service is mutating, the three read tools are not",
            registry.Tools.First(t => t.Name == "ha_call_service").SideEffect == SideEffect.Mutating
                && registry.Tools.First(t => t.Name == "ha_list_entities").SideEffect == SideEffect.ReadOnly
                && registry.Tools.First(t => t.Name == "ha_get_state").SideEffect == SideEffect.ReadOnly
                && registry.Tools.First(t => t.Name == "ha_list_services").SideEffect == SideEffect.ReadOnly);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> ListingAsync(HomeAssistantTools tools)
    {
        Console.WriteLine("-- ha_list_entities --");
        int failures = 0;

        string all = await tools.ListEntities().ConfigureAwait(false);
        Console.WriteLine(Indent(all));

        failures += Check("lists every entity", all.Contains("5 of 5 entities"));

        // The id is the argument every other call takes; the friendly name is what the
        // user said. Both have to be on the line or one has to be guessed from the other.
        failures += Check(
            "each line carries id and friendly name",
            all.Contains("light.office \"Office Lamp\" = on"));

        failures += Check(
            "a numeric sensor carries its unit",
            all.Contains("sensor.living_temperature \"Living Room Temperature\" = 21.4 °C"));

        string lights = await tools.ListEntities(domain: "light").ConfigureAwait(false);
        failures += Check(
            "domain filter narrows to lights",
            lights.Contains("2 of 5") && !lights.Contains("sensor."));

        string named = await tools.ListEntities(filter: "Office Lamp").ConfigureAwait(false);
        failures += Check(
            "filter matches the friendly name, not just the id",
            named.Contains("1 of 5") && named.Contains("light.office"));

        string none = await tools.ListEntities(filter: "greenhouse").ConfigureAwait(false);
        Console.WriteLine(Indent(none));

        // A dead end that names the domains that do exist is the difference between
        // the model correcting itself and the model reporting failure.
        failures += Check(
            "no match names the domains that do exist",
            none.Contains("No entity matches")
                && none.Contains("light")
                && none.Contains("sensor")
                && none.Contains("climate"));

        string capped = await tools.ListEntities(limit: 2).ConfigureAwait(false);
        failures += Check(
            "a truncated list says so instead of looking complete",
            capped.Contains("3 more"));

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> SingleEntityAsync(HomeAssistantTools tools)
    {
        Console.WriteLine("-- ha_get_state --");
        int failures = 0;

        string light = await tools.GetState("light.office").ConfigureAwait(false);
        Console.WriteLine(Indent(light));

        failures += Check("state line first", light.StartsWith("light.office \"Office Lamp\" = on"));
        failures += Check("attributes follow", light.Contains("brightness: 180"));

        failures += Check(
            "friendly_name is not repeated in the attribute list",
            !light.Contains("friendly_name"));

        // A media player's source list or a climate entity's preset modes can be long;
        // the point of showing attributes is to reveal what is settable.
        failures += Check(
            "a long list is summarised rather than dumped",
            light.Contains("... 9 total"));

        string missing = await tools.GetState("light.nonexistent").ConfigureAwait(false);
        Console.WriteLine(Indent(missing));

        failures += Check(
            "an unknown entity is reported with the way to find the right one",
            missing.Contains("does not know") && missing.Contains("ha_list_entities"));

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> ServicesAsync(HomeAssistantTools tools)
    {
        Console.WriteLine("-- ha_list_services --");
        int failures = 0;

        string index = await tools.ListServices().ConfigureAwait(false);
        Console.WriteLine(Indent(index));

        failures += Check("index lists the domains", index.Contains("2 service domain(s)"));

        // Every service of every domain with its full field schema is tens of thousands
        // of characters, and it would be paid for on every later turn in the context.
        failures += Check(
            "the index omits field detail",
            !index.Contains("brightness_pct"));

        string light = await tools.ListServices("light").ConfigureAwait(false);
        Console.WriteLine(Indent(light));

        failures += Check("a named domain lists its fields", light.Contains("brightness_pct"));
        failures += Check("service names are domain-qualified", light.Contains("light.turn_on"));

        string wrong = await tools.ListServices("lights").ConfigureAwait(false);
        failures += Check(
            "an unknown domain lists the real ones",
            wrong.Contains("No service domain 'lights'") && wrong.Contains("climate"));

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> CallingAsync(HomeAssistantTools tools, StubHomeAssistant server)
    {
        Console.WriteLine("-- ha_call_service --");
        int failures = 0;

        string turnOn = await tools
            .CallService("light", "turn_on", "light.office", "{\"brightness_pct\": 40}")
            .ConfigureAwait(false);

        Console.WriteLine(Indent(turnOn));

        failures += Check("reports what changed", turnOn.Contains("changed 1 entity"));

        // The post-call state is the difference between "the call was accepted" and
        // "the light is on".
        failures += Check("shows the resulting state", turnOn.Contains("light.office"));

        failures += Check(
            "entity_id reaches the service call",
            server.LastBody?.Contains("\"entity_id\":\"light.office\"") == true);

        failures += Check(
            "extra data reaches the service call",
            server.LastBody?.Contains("\"brightness_pct\":40") == true);

        string noData = await tools.CallService("light", "turn_off", "light.office").ConfigureAwait(false);
        failures += Check("a call without data works", noData.Contains("light.turn_off"));

        // A malformed argument the model wrote is something it can fix next round --
        // but only if it is told, rather than the turn dying on an exception.
        string bad = await tools
            .CallService("light", "turn_on", "light.office", "{brightness")
            .ConfigureAwait(false);

        Console.WriteLine(Indent(bad));
        failures += Check("malformed dataJson is explained, not thrown", bad.Contains("not valid JSON"));

        string notObject = await tools
            .CallService("light", "turn_on", "light.office", "[1,2]")
            .ConfigureAwait(false);

        failures += Check("a JSON array is rejected with the expected shape",
            notObject.Contains("must be a JSON object"));

        // Home Assistant accepts a call it cannot route and answers with an empty list.
        // Reporting that as success would be a lie the user acts on.
        string empty = await tools.CallService("light", "no_such_service", "light.office").ConfigureAwait(false);
        Console.WriteLine(Indent(empty));

        failures += Check(
            "an empty change list is not reported as success",
            empty.Contains("no changed entity") && empty.Contains("ha_get_state"));

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> FailuresAsync(HomeAssistantTools tools, StubHomeAssistant server)
    {
        Console.WriteLine("-- failure reporting --");
        int failures = 0;

        server.NextStatus = HttpStatusCode.Unauthorized;

        try
        {
            await tools.ListEntities().ConfigureAwait(false);
            failures += Check("a rejected token is reported", false);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine(Indent(ex.Message));

            // 401 has one overwhelmingly likely cause, and saying so saves the user
            // reading a stack trace to learn their token was revoked.
            failures += Check(
                "401 names the token variable",
                ex.Message.Contains("401") && ex.Message.Contains("HASS_TOKEN"));
        }

        server.NextStatus = HttpStatusCode.BadRequest;
        server.NextBody = "extra keys not allowed @ data['brightnes']";

        try
        {
            await tools.CallService("light", "turn_on", "light.office", "{\"brightnes\": 4}")
                .ConfigureAwait(false);

            failures += Check("a rejected call is reported", false);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine(Indent(ex.Message));

            // This body is what lets a model fix its own typo instead of retrying it.
            failures += Check(
                "the rejection body reaches the caller",
                ex.Message.Contains("brightnes"));
        }

        Console.WriteLine();
        return failures;
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {what}");
        return ok ? 0 : 1;
    }

    private static string Indent(string text) =>
        "    " + text.TrimEnd().ReplaceLineEndings(Environment.NewLine + "    ");
}

/// <summary>
/// A minimal Home Assistant that answers the four endpoints the tools use, with the
/// same JSON shapes the real one produces.
/// </summary>
internal sealed class StubHomeAssistant : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;

    public StubHomeAssistant()
    {
        // A fixed high port rather than port 0: HttpListener does not report the bound
        // port, so it has to be chosen up front.
        _port = 58731;
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>Body of the most recent POST, so a call can be checked on the wire.</summary>
    public string? LastBody { get; private set; }

    /// <summary>Forces the next response to fail, for the error-path checks.</summary>
    public HttpStatusCode? NextStatus { get; set; }

    public string? NextBody { get; set; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // Listener stopped.
            }

            try
            {
                await RespondAsync(context).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A stub that throws while shutting down would look like a test failure.
            }
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? string.Empty;

        if (context.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            LastBody = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (NextStatus is { } forced)
        {
            NextStatus = null;
            string body = NextBody ?? "forced failure";
            NextBody = null;

            context.Response.StatusCode = (int)forced;
            await WriteAsync(context, body).ConfigureAwait(false);
            return;
        }

        string json = path switch
        {
            "/api/states" => States,
            "/api/services" => Services,
            _ when path.StartsWith("/api/states/") => SingleState(path["/api/states/".Length..]),
            _ when path.StartsWith("/api/services/") => CallResult(path),
            _ => "null",
        };

        if (json == "404")
        {
            context.Response.StatusCode = 404;
            await WriteAsync(context, "{\"message\":\"Entity not found.\"}").ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "application/json";
        await WriteAsync(context, json).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpListenerContext context, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static string SingleState(string escapedId)
    {
        string id = Uri.UnescapeDataString(escapedId);

        using JsonDocument document = JsonDocument.Parse(States);

        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.GetProperty("entity_id").GetString() == id)
                return element.GetRawText();
        }

        return "404";
    }

    /// <summary>
    /// Answer a service call the way Home Assistant does: the changed entities, or an
    /// empty list for a service that exists nowhere.
    /// </summary>
    private static string CallResult(string path)
    {
        if (path.EndsWith("/no_such_service"))
            return "[]";

        bool off = path.EndsWith("/turn_off");

        string state = off ? "off" : "on";
        int brightness = off ? 0 : 102;

        // Concatenated rather than interpolated: JSON's own braces collide with the
        // interpolation delimiters of a raw string literal often enough that the
        // escaping becomes the least readable part of the file.
        return "[{\"entity_id\":\"light.office\",\"state\":\"" + state + "\","
            + "\"attributes\":{\"friendly_name\":\"Office Lamp\",\"brightness\":"
            + brightness + "}}]";
    }

    private const string States = """
    [
      {"entity_id":"light.office","state":"on",
       "attributes":{"friendly_name":"Office Lamp","brightness":180,
                     "supported_color_modes":["a","b","c","d","e","f","g","h","i"]}},
      {"entity_id":"light.kitchen","state":"off",
       "attributes":{"friendly_name":"Kitchen Ceiling"}},
      {"entity_id":"sensor.living_temperature","state":"21.4",
       "attributes":{"friendly_name":"Living Room Temperature","unit_of_measurement":"°C"}},
      {"entity_id":"climate.hallway","state":"heat",
       "attributes":{"friendly_name":"Hallway Thermostat","temperature":21}},
      {"entity_id":"sensor.power","state":"430",
       "attributes":{"friendly_name":"Power","unit_of_measurement":"W"}}
    ]
    """;

    private const string Services = """
    [
      {"domain":"light","services":{
        "turn_on":{"name":"Turn on","description":"Turn a light on, optionally dimmed.",
                   "fields":{"brightness_pct":{},"color_name":{},"transition":{}}},
        "turn_off":{"name":"Turn off","description":"Turn a light off.",
                    "fields":{"transition":{}}},
        "toggle":{"name":"Toggle","description":"Toggle a light.","fields":{}}}},
      {"domain":"climate","services":{
        "set_temperature":{"name":"Set target temperature","description":"Set the target.",
                           "fields":{"temperature":{},"hvac_mode":{}}}}}
    ]
    """;

    public void Dispose()
    {
        if (_listener.IsListening)
            _listener.Stop();

        _listener.Close();
    }
}
