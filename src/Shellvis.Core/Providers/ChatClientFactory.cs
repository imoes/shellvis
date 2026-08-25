using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Shellvis.Core.Providers;

/// <summary>
/// Builds an <see cref="IChatClient"/> for a provider profile.
///
/// Everything above this line in the stack sees one interface, so switching models
/// mid-session is swapping an implementation rather than branching the agent loop.
/// That is the piece Hermes never factored out: it dispatches on an api_mode string
/// from inside a 12,000-line class, which is why adding a provider there means
/// touching the loop.
/// </summary>
public static class ChatClientFactory
{
    /// <summary>
    /// Create a client for a profile.
    /// </summary>
    /// <param name="profile">Which provider to talk to.</param>
    /// <param name="model">Model override, or null for the profile default.</param>
    /// <param name="apiKey">
    /// Key override. Normally left null so the key is read from the profile's
    /// environment variable, keeping secrets out of call sites and logs.
    /// </param>
    public static IChatClient Create(
        ProviderProfile profile,
        string? model = null,
        string? apiKey = null,
        int requestTimeoutSeconds = 300)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Transport switch
        {
            ChatTransport.OpenAiChatCompletions =>
                CreateOpenAiCompatible(profile, model, apiKey, requestTimeoutSeconds),

            ChatTransport.OpenAiResponses =>
                CreateResponses(profile, model, apiKey, requestTimeoutSeconds),

            // Anthropic and Gemini are reachable today through their OpenAI-compatible
            // endpoints, which is what the catalog entries use. These two enum values
            // are reserved for the NATIVE protocols, wanted for prompt caching and
            // thinking blocks, and nothing selects them yet. The message names the
            // working route rather than leaving the reader to guess.
            ChatTransport.AnthropicMessages =>
                throw new NotSupportedException(
                    "The native Anthropic Messages transport is not implemented. Use "
                    + "provider 'anthropic', which reaches the same models over the "
                    + "OpenAI-compatible endpoint (without prompt caching)."),

            ChatTransport.GeminiNative =>
                throw new NotSupportedException(
                    "The native Gemini transport is not implemented. Use provider "
                    + "'gemini', which reaches the same models over Google's "
                    + "OpenAI-compatible endpoint (without thinking budget control)."),

            _ => throw new NotSupportedException($"Unknown transport {profile.Transport}."),
        };
    }

    private static IChatClient CreateOpenAiCompatible(
        ProviderProfile profile, string? model, string? apiKey, int requestTimeoutSeconds)
    {
        string key = apiKey ?? ResolveKey(profile);

        var options = new OpenAIClientOptions();

        // Set explicitly, because the library's own default is about a hundred seconds
        // and that is not enough for a local model. Leaving it unset cut long answers off
        // mid-generation with a cancellation that looked like nothing in particular.
        options.NetworkTimeout = TimeSpan.FromSeconds(Math.Clamp(requestTimeoutSeconds, 10, 3600));

        if (profile.BaseUrl is { Length: > 0 } baseUrl)
            options.Endpoint = new Uri(baseUrl);

        foreach ((string name, string value) in profile.Quirks.ExtraHeaders ?? new Dictionary<string, string>())
        {
            // Per-request headers are how a provider gets its attribution or routing
            // hints without the agent loop knowing anything about it.
            options.AddPolicy(
                new StaticHeaderPolicy(name, value),
                PipelinePosition.PerCall);
        }

        var client = new OpenAIClient(new ApiKeyCredential(key), options);
        return client.GetChatClient(model ?? profile.DefaultModel).AsIChatClient();
    }

    /// <summary>
    /// The /responses transport, as Codex uses it.
    ///
    /// A different protocol from /chat/completions, not a variant of it: /responses
    /// keeps reasoning state on the server between turns, which is the reason to reach
    /// for it. The OpenAI package already referenced here speaks it, so this costs no
    /// new dependency -- and it lands behind the same IChatClient, so the agent loop
    /// cannot tell the difference.
    /// </summary>
    private static IChatClient CreateResponses(
        ProviderProfile profile, string? model, string? apiKey, int requestTimeoutSeconds)
    {
        string key = apiKey ?? ResolveKey(profile);

        var options = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(Math.Clamp(requestTimeoutSeconds, 10, 3600)),
        };

        if (profile.BaseUrl is { Length: > 0 } baseUrl)
            options.Endpoint = new Uri(baseUrl);

        foreach ((string name, string value) in profile.Quirks.ExtraHeaders ?? new Dictionary<string, string>())
            options.AddPolicy(new StaticHeaderPolicy(name, value), PipelinePosition.PerCall);

        var client = new OpenAIClient(new ApiKeyCredential(key), options);

        // OPENAI001: the Responses surface is marked experimental in the SDK, so using
        // it is an error unless suppressed. Suppressed here and nowhere else, because
        // the scope of the risk is exactly this method: if the API changes, the /responses
        // transport stops compiling and every other provider is untouched. Suppressing
        // it project-wide would hide the same warning on unrelated experimental APIs.
#pragma warning disable OPENAI001
        // Unlike the chat client, the responses client is not bound to a model at
        // construction -- the model travels with each request, so it is passed to the
        // adapter instead.
        return client.GetResponsesClient().AsIChatClient(model ?? profile.DefaultModel);
#pragma warning restore OPENAI001
    }

    /// <summary>
    /// Resolve the API key for a profile.
    ///
    /// Local servers get a placeholder rather than an empty string: the OpenAI client
    /// rejects an empty credential outright, while a llama.cpp or Ollama server
    /// ignores whatever is sent. A placeholder therefore turns a confusing client-side
    /// argument exception into a working request.
    /// </summary>
    private static string ResolveKey(ProviderProfile profile)
    {
        // The environment wins. Someone who has exported OPENAI_API_KEY for their whole
        // shell expects that to be the key in use, and a stored one silently overriding it
        // would be a setting they cannot see from where they set it.
        if (profile.ApiKeyEnvVar is { Length: > 0 } variable)
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        // Then the key entered in the provider dialog, encrypted to this Windows account.
        if (Config.SecretStore.Get(Config.SecretStore.NameForProvider(profile.Id))
            is { Length: > 0 } stored)
        {
            return stored;
        }

        if (profile.RequiresKey)
        {
            throw new InvalidOperationException(
                profile.ApiKeyEnvVar is { Length: > 0 } named
                    ? $"Provider '{profile.Id}' needs an API key. Set {named}, or enter one "
                      + "in the provider settings."
                    : $"Provider '{profile.Id}' needs an API key. Enter one in the provider "
                      + "settings.");
        }

        return "not-required";
    }

    /// <summary>Adds one fixed header to every request on the pipeline.</summary>
    private sealed class StaticHeaderPolicy(string name, string value) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
        {
            message.Request?.Headers.Set(name, value);
            ProcessNext(message, pipeline, index);
        }

        public override ValueTask ProcessAsync(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
        {
            message.Request?.Headers.Set(name, value);
            return ProcessNextAsync(message, pipeline, index);
        }
    }
}
