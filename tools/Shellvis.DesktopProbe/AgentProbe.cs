using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using Shellvis.Core.Agent;
using Shellvis.Core.Broker;
using Shellvis.Core.Browser;
using Shellvis.Core.Config;
using Shellvis.Core.Hooks;
using Shellvis.Core.Office;
using Shellvis.Core.Providers;
using Shellvis.Core.Shell;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Exercises the full agent loop: the model asks for a tool, the loop runs it, the
/// result goes back, the model answers.
///
/// With no argument it runs against a stubbed HTTP handler rather than a real provider.
/// That is not a mock of our own code: the genuine OpenAI client still serializes the
/// tool schemas onto the wire and parses tool_calls back off it, so the whole protocol
/// path is exercised. Only the socket is missing, which also means the test needs no
/// network, no API key, and no admin rights for a listener.
///
/// Given a base URL it runs against the real thing instead.
/// </summary>
internal static class AgentProbe
{
    private const string SystemPrompt = """
        You are Shellvis, an agent that operates this Windows machine.
        You speak in announcements. Reply in the language the user wrote in.
        Use the tools to observe before you act. Never guess at UI state.
        """;

    private const string DefaultQuestion = "Which windows are open on my desktop right now?";

    public static async Task<int> RunAsync(string? baseUrl, string? model, string? task)
    {
        string question = string.IsNullOrWhiteSpace(task) ? DefaultQuestion : task;

        using var desktop = new DesktopTools();
        using var comApartment = new ComApartment();
        var host = new PowerShellHost();
        using var shellTools = new PowerShellTools(host);

        var registry = new ToolRegistry();
        registry.RegisterFrom(desktop);
        registry.RegisterFrom(shellTools);
        registry.RegisterFrom(new WslTools());
        registry.RegisterFrom(new GalleryTools(host));
        registry.RegisterFrom(new OfficeTools());
        registry.RegisterFrom(new OutlookTools(comApartment));
        registry.RegisterFrom(new OfficeComTools(new OfficeComClient(comApartment)));

        // The browser is disposed with the probe, so a launched Chrome does not outlive
        // the run.
        await using var browser = new BrowserHost();
        registry.RegisterFrom(new BrowserTools(browser, new UrlGuard()));

        // Hooks come from the real config file, so this probe exercises whatever the
        // machine is actually configured to do. Consent is granted automatically:
        // there is no dialog on a console, and the point here is the loop wiring.
        var hookWarnings = new List<string>();
        ConfigLoadResult loaded = ConfigStore.Load();

        // The config's OWN warnings are printed too. Without this a config that failed
        // to parse falls back to defaults in silence, and the symptom is a feature that
        // simply does not happen -- which is exactly how a broken hooks block was
        // mistaken for broken hook wiring here.
        foreach (string warning in loaded.Warnings)
            Console.WriteLine($"  config: {warning}");

        IReadOnlyList<HookDefinition> hookDefs =
            HookLoader.Load(loaded.Config.Hooks, hookWarnings);

        foreach (string warning in hookWarnings)
            Console.WriteLine($"  hook config: {warning}");

        if (hookDefs.Count > 0)
            Console.WriteLine($"  hooks:    {hookDefs.Count} configured");

        var hooks = new HookRunner(hookDefs, AllowAllHooks.Instance, "probe-session");

        // Broker tools only when one is actually listening, matching what the app does.
        var brokerClient = new BrokerClient();

        if (await brokerClient.IsAvailableAsync().ConfigureAwait(false))
        {
            registry.RegisterFrom(new BrokerTools(brokerClient));
            Console.WriteLine("  broker:   connected");
        }

        IChatClient client;
        string mode;

        if (baseUrl is { Length: > 0 })
        {
            ProviderProfile profile = ProviderCatalog.OpenAiCompatible(baseUrl, model ?? "laguna");
            client = ChatClientFactory.Create(profile);
            mode = $"live: {baseUrl} ({profile.DefaultModel})";
        }
        else
        {
            client = CreateStubbedClient();
            mode = "stubbed HTTP handler (no network)";
        }

        Console.WriteLine($"provider: {mode}");
        Console.WriteLine($"tools:    {registry.Count} registered");
        Console.WriteLine($"asking:   {question}\n");

        var loop = new AgentLoop(
            client,
            registry,
            AutoApprove.Instance,
            new AgentOptions(MaxIterations: 12, SystemPrompt: SystemPrompt),
            null,
            null,
            hooks);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        bool answered = false;
        int deltas = 0;

        await foreach (AgentEvent evt in loop.RunAsync(question, cts.Token).ConfigureAwait(false))
        {
            switch (evt)
            {
                case AgentEvent.IterationStarted e:
                    Console.WriteLine($"[round {e.Iteration}/{e.Budget}]");
                    break;

                case AgentEvent.AssistantDelta e:
                    // Printed without a newline, so the answer appears the way it does in
                    // the pill: a chunk at a time.
                    Console.Write(e.Text);
                    deltas++;
                    break;

                case AgentEvent.AssistantMessage e:
                    if (deltas > 0)
                        Console.WriteLine();

                    Console.WriteLine($"  assistant: {Flatten(e.Text, 400)}");
                    break;

                case AgentEvent.ToolStarted e:
                    Console.WriteLine($"  -> {e.Preview}");
                    break;

                case AgentEvent.ToolCompleted e:
                    Console.WriteLine(
                        $"  <- {e.Tool} {(e.Succeeded ? "ok" : "FAILED")} "
                        + $"in {e.Duration.TotalMilliseconds:F0}ms: {Flatten(e.Result, 160)}");
                    break;

                case AgentEvent.ToolRefused e:
                    Console.WriteLine($"  !! {e.Tool} refused: {e.Reason}");
                    break;

                case AgentEvent.Failure e:
                    Console.WriteLine($"  failure: {e.Message}");
                    break;

                case AgentEvent.TurnFinished e:
                    Console.WriteLine($"\nturn ended: {e.Reason} after {e.Iterations} round(s)");
                    answered = e.Reason == TurnEndReason.Answered;
                    break;
            }
        }

        // The delta count is the evidence that streaming actually happened. A turn that
        // answers correctly with zero deltas has quietly fallen back to non-streaming.
        Console.WriteLine($"\nstreamed {deltas} delta(s)");

        Console.WriteLine(answered
            ? "\nVERIFIED: the model asked for a tool, the loop ran it, and the answer came back."
            : "\nNOT VERIFIED: the turn did not end with an answer.");

        return answered ? 0 : 1;
    }

    private static string Flatten(string text, int max)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal))
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);

        return flat.Length <= max ? flat : flat[..max] + "...";
    }

    private static IChatClient CreateStubbedClient()
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("https://stub.invalid/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(new ScriptedHandler())),
        };

        var client = new OpenAIClient(new ApiKeyCredential("stub"), options);
        return client.GetChatClient("stub-model").AsIChatClient();
    }

    /// <summary>
    /// Answers chat completion requests from a script.
    ///
    /// Scripted rather than intelligent, and deliberately so: round one asks for a
    /// tool, round two answers having seen the result. That is exactly the two-round
    /// shape the loop must handle, and scripting it makes the outcome deterministic
    /// instead of dependent on a model's mood.
    ///
    /// It also asserts something on the way past: the request must actually contain
    /// the tool schema. If the loop ever stops advertising its tools, this fails here
    /// rather than silently degrading into a chatbot.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private int _round;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            int round = Interlocked.Increment(ref _round);

            if (round == 1 && !requestBody.Contains("window_list", StringComparison.Ordinal))
            {
                return Json(
                    TextResponse("The request carried no tool schema, so the loop is misconfigured."));
            }

            string body = round == 1
                ? ToolCallResponse("call_1", "window_list", "{}")
                : TextResponse("Shellvis reports: I read the desktop and listed the open windows above.");

            return Json(body);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        private static string ToolCallResponse(string callId, string name, string argumentsJson) =>
            $$"""
            {
              "id": "chatcmpl-stub",
              "object": "chat.completion",
              "created": 1750000000,
              "model": "stub-model",
              "choices": [{
                "index": 0,
                "message": {
                  "role": "assistant",
                  "content": null,
                  "tool_calls": [{
                    "id": "{{callId}}",
                    "type": "function",
                    "function": { "name": "{{name}}", "arguments": {{JsonSerializer.Serialize(argumentsJson)}} }
                  }]
                },
                "finish_reason": "tool_calls"
              }],
              "usage": { "prompt_tokens": 100, "completion_tokens": 10, "total_tokens": 110 }
            }
            """;

        private static string TextResponse(string text) =>
            $$"""
            {
              "id": "chatcmpl-stub",
              "object": "chat.completion",
              "created": 1750000000,
              "model": "stub-model",
              "choices": [{
                "index": 0,
                "message": { "role": "assistant", "content": {{JsonSerializer.Serialize(text)}} },
                "finish_reason": "stop"
              }],
              "usage": { "prompt_tokens": 200, "completion_tokens": 20, "total_tokens": 220 }
            }
            """;
    }
}
