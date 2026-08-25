namespace Shellvis.Core.Providers;

/// <summary>Which wire protocol a provider speaks.</summary>
public enum ChatTransport
{
    /// <summary>
    /// OpenAI /chat/completions. Covers the overwhelming majority: OpenRouter, local
    /// llama.cpp and Ollama and vLLM and LM Studio, Groq, xAI, DeepSeek, Mistral,
    /// Kimi, GLM, NVIDIA NIM, Together, Fireworks. They differ in base URL, key
    /// variable and a handful of quirks, not in protocol.
    /// </summary>
    OpenAiChatCompletions,

    /// <summary>OpenAI /responses, the Codex-style API.</summary>
    OpenAiResponses,

    /// <summary>Anthropic Messages, natively. Needed for prompt caching and thinking blocks.</summary>
    AnthropicMessages,

    /// <summary>Gemini generateContent, natively. Needed for thinking budget and safety settings.</summary>
    GeminiNative,
}

/// <summary>
/// The per-provider deviations from plain OpenAI semantics.
///
/// This exists as data rather than as code on purpose. Hermes handles the same problem
/// with a long if-else cascade inside its request builder, which is where its provider
/// support became hard to extend. Here a new provider is a table row.
/// </summary>
/// <param name="MaxTokensParameter">
/// Whether the token cap is called max_tokens or max_completion_tokens. Newer OpenAI
/// models reject the old name outright.
/// </param>
/// <param name="OmitTemperature">
/// Some reasoning models reject temperature entirely rather than ignoring it.
/// </param>
/// <param name="DefaultMaxTokens">Cap to send when the caller does not specify one.</param>
/// <param name="ExtraHeaders">
/// Headers the provider requires. OpenRouter wants HTTP-Referer and X-Title for
/// attribution and will rate-limit harder without them.
/// </param>
public sealed record ProviderQuirks(
    string MaxTokensParameter = "max_tokens",
    bool OmitTemperature = false,
    int? DefaultMaxTokens = null,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null)
{
    public static readonly ProviderQuirks Default = new();
}

/// <summary>
/// One addressable model provider.
/// </summary>
/// <param name="Id">Short key used in config and by the /model command.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Transport">Which protocol to speak.</param>
/// <param name="BaseUrl">API root. Null means the transport's own default.</param>
/// <param name="ApiKeyEnvVar">Environment variable holding the key, or null when none is needed.</param>
/// <param name="DefaultModel">Model to use when the caller does not name one.</param>
/// <param name="Quirks">Deviations from plain OpenAI semantics.</param>
/// <param name="RequiresKey">
/// False for local servers. Kept explicit because a local endpoint that is handed a
/// bogus key usually still works, while a cloud one fails in a confusing way.
/// </param>
public sealed record ProviderProfile(
    string Id,
    string DisplayName,
    ChatTransport Transport,
    string? BaseUrl,
    string? ApiKeyEnvVar,
    string DefaultModel,
    ProviderQuirks Quirks,
    bool RequiresKey = true);

/// <summary>
/// The built-in provider catalog.
///
/// Deliberately small and additive: this is a starting set, not a claim of
/// completeness. Anything OpenAI-compatible that is not listed works through
/// <see cref="OpenAiCompatible"/> with a base URL, which is the whole point of
/// treating providers as data.
/// </summary>
public static class ProviderCatalog
{
    /// <summary>
    /// A private llama.cpp endpoint, addressed by name rather than by URL.
    ///
    /// The URL is a placeholder and is meant to be overridden, because the real one is
    /// somebody's internal infrastructure and does not belong in a public catalogue. Point
    /// it at the actual host in config.yaml, which the resolver merges field by field:
    ///
    ///     providers:
    ///       laguna:
    ///         baseUrl: https://your-host/llama/v1
    ///
    /// Kept as a named entry rather than deleted because it is what
    /// <c>SHELLVIS_BASE_URL</c> and every existing config refer to, and because a keyless
    /// self-hosted endpoint is a distinct enough case to deserve a name.
    /// </summary>
    public static readonly ProviderProfile Laguna = new(
        Id: "laguna",
        DisplayName: "laguna (private llama.cpp)",
        Transport: ChatTransport.OpenAiChatCompletions,
        BaseUrl: "http://localhost:8080/v1",
        ApiKeyEnvVar: null,
        DefaultModel: "laguna",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 4096 },
        RequiresKey: false);

    public static readonly ProviderProfile OpenRouter = new(
        Id: "openrouter",
        DisplayName: "OpenRouter",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://openrouter.ai/api/v1",
        ApiKeyEnvVar: "OPENROUTER_API_KEY",
        DefaultModel: "anthropic/claude-sonnet-4.5",
        Quirks: ProviderQuirks.Default with
        {
            DefaultMaxTokens = 8192,
            ExtraHeaders = new Dictionary<string, string>
            {
                // Attribution. OpenRouter rate-limits unattributed traffic harder.
                ["HTTP-Referer"] = "https://github.com/shellvis",
                ["X-Title"] = "Shellvis",
            },
        });

    public static readonly ProviderProfile Ollama = new(
        Id: "ollama",
        DisplayName: "Ollama (local)",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "http://localhost:11434/v1",
        ApiKeyEnvVar: null,
        DefaultModel: "llama3.1",
        Quirks: ProviderQuirks.Default,
        RequiresKey: false);

    public static readonly ProviderProfile LmStudio = new(
        Id: "lmstudio",
        DisplayName: "LM Studio (local)",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "http://localhost:1234/v1",
        ApiKeyEnvVar: null,
        DefaultModel: "local-model",
        Quirks: ProviderQuirks.Default,
        RequiresKey: false);

    public static readonly ProviderProfile LlamaCpp = new(
        Id: "llamacpp",
        DisplayName: "llama.cpp (local)",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "http://localhost:8080/v1",
        ApiKeyEnvVar: null,
        DefaultModel: "local-model",
        Quirks: ProviderQuirks.Default,
        RequiresKey: false);

    public static readonly ProviderProfile OpenAi = new(
        Id: "openai",
        DisplayName: "OpenAI",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.openai.com/v1",
        ApiKeyEnvVar: "OPENAI_API_KEY",
        DefaultModel: "gpt-5",
        // Newer OpenAI models reject max_tokens outright rather than ignoring it, and
        // the reasoning models reject temperature the same way.
        Quirks: ProviderQuirks.Default with
        {
            MaxTokensParameter = "max_completion_tokens",
            OmitTemperature = true,
            DefaultMaxTokens = 16384,
        });

    /// <summary>
    /// OpenAI's /responses API, the shape Codex uses.
    ///
    /// A separate entry rather than a flag on the one above, because it is a different
    /// wire protocol -- and the reason to want it is that /responses carries reasoning
    /// state between turns, which /chat/completions discards.
    /// </summary>
    public static readonly ProviderProfile Codex = new(
        Id: "codex",
        DisplayName: "OpenAI Codex (/responses)",
        ChatTransport.OpenAiResponses,
        BaseUrl: "https://api.openai.com/v1",
        ApiKeyEnvVar: "OPENAI_API_KEY",
        DefaultModel: "gpt-5-codex",
        Quirks: ProviderQuirks.Default with { OmitTemperature = true, DefaultMaxTokens = 16384 });

    /// <summary>
    /// Anthropic through its OpenAI-compatible endpoint.
    ///
    /// Honest about what this is: the compatibility layer, not the native Messages API.
    /// It costs prompt caching and thinking blocks, and it buys working tool calling
    /// today with no extra dependency. The native transport stays on the list.
    /// </summary>
    public static readonly ProviderProfile Anthropic = new(
        Id: "anthropic",
        DisplayName: "Anthropic (OpenAI-compatible)",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.anthropic.com/v1",
        ApiKeyEnvVar: "ANTHROPIC_API_KEY",
        DefaultModel: "claude-sonnet-4-5",
        // Anthropic requires an explicit cap; omitting it is a 400, not a default.
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    /// <summary>Gemini through Google's OpenAI-compatible surface.</summary>
    public static readonly ProviderProfile Gemini = new(
        Id: "gemini",
        DisplayName: "Google Gemini (OpenAI-compatible)",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://generativelanguage.googleapis.com/v1beta/openai",
        ApiKeyEnvVar: "GEMINI_API_KEY",
        DefaultModel: "gemini-2.5-pro",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    public static readonly ProviderProfile XAi = new(
        Id: "xai",
        DisplayName: "xAI Grok",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.x.ai/v1",
        ApiKeyEnvVar: "XAI_API_KEY",
        DefaultModel: "grok-4",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    public static readonly ProviderProfile Groq = new(
        Id: "groq",
        DisplayName: "Groq",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.groq.com/openai/v1",
        ApiKeyEnvVar: "GROQ_API_KEY",
        DefaultModel: "llama-3.3-70b-versatile",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    public static readonly ProviderProfile DeepSeek = new(
        Id: "deepseek",
        DisplayName: "DeepSeek",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.deepseek.com/v1",
        ApiKeyEnvVar: "DEEPSEEK_API_KEY",
        DefaultModel: "deepseek-chat",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    public static readonly ProviderProfile Mistral = new(
        Id: "mistral",
        DisplayName: "Mistral",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.mistral.ai/v1",
        ApiKeyEnvVar: "MISTRAL_API_KEY",
        DefaultModel: "mistral-large-latest",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    public static readonly ProviderProfile Moonshot = new(
        Id: "kimi",
        DisplayName: "Moonshot Kimi",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.moonshot.ai/v1",
        ApiKeyEnvVar: "MOONSHOT_API_KEY",
        DefaultModel: "kimi-k2-0905-preview",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    public static readonly ProviderProfile ZAi = new(
        Id: "zai",
        DisplayName: "Z.ai GLM",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.z.ai/api/paas/v4",
        ApiKeyEnvVar: "ZAI_API_KEY",
        DefaultModel: "glm-4.6",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    public static readonly ProviderProfile Together = new(
        Id: "together",
        DisplayName: "Together AI",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.together.xyz/v1",
        ApiKeyEnvVar: "TOGETHER_API_KEY",
        DefaultModel: "meta-llama/Llama-3.3-70B-Instruct-Turbo",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    public static readonly ProviderProfile Fireworks = new(
        Id: "fireworks",
        DisplayName: "Fireworks AI",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.fireworks.ai/inference/v1",
        ApiKeyEnvVar: "FIREWORKS_API_KEY",
        DefaultModel: "accounts/fireworks/models/llama-v3p3-70b-instruct",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    public static readonly ProviderProfile Cerebras = new(
        Id: "cerebras",
        DisplayName: "Cerebras",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://api.cerebras.ai/v1",
        ApiKeyEnvVar: "CEREBRAS_API_KEY",
        DefaultModel: "llama-3.3-70b",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    public static readonly ProviderProfile Nvidia = new(
        Id: "nvidia",
        DisplayName: "NVIDIA NIM",
        ChatTransport.OpenAiChatCompletions,
        BaseUrl: "https://integrate.api.nvidia.com/v1",
        ApiKeyEnvVar: "NVIDIA_API_KEY",
        DefaultModel: "meta/llama-3.3-70b-instruct",
        Quirks: ProviderQuirks.Default with { DefaultMaxTokens = 8192 });

    private static readonly ProviderProfile[] All =
    [
        Laguna, OpenRouter, Ollama, LmStudio, LlamaCpp,
        OpenAi, Codex, Anthropic, Gemini, XAi, Groq, DeepSeek, Mistral,
        Moonshot, ZAi, Together, Fireworks, Cerebras, Nvidia,
    ];

    /// <summary>
    /// Alternative names people actually type.
    ///
    /// Taken from Hermes' provider aliases. Worth having because the name a product
    /// markets itself under and the name its API host uses are often different -- GLM
    /// is sold as GLM and served by Z.ai -- and being told "unknown provider 'glm'"
    /// when the entry exists is a needless dead end.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["glm"] = "zai",
            ["z.ai"] = "zai",
            ["zhipu"] = "zai",
            ["moonshot"] = "kimi",
            ["grok"] = "xai",
            ["x.ai"] = "xai",
            ["claude"] = "anthropic",
            ["google"] = "gemini",
            ["googleai"] = "gemini",
            ["gpt"] = "openai",
            ["oai"] = "openai",
            ["local"] = "llamacpp",
            ["lm-studio"] = "lmstudio",
            ["openrouter.ai"] = "openrouter",
        };

    /// <summary>Look up a profile by id or alias, case-insensitively.</summary>
    public static ProviderProfile? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        string key = id.Trim();

        if (Aliases.TryGetValue(key, out string? canonical))
            key = canonical;

        return All.FirstOrDefault(p => p.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every id and alias, for an error message that helps.</summary>
    public static IReadOnlyList<string> KnownNames =>
        [.. All.Select(p => p.Id).Concat(Aliases.Keys).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    public static IReadOnlyList<ProviderProfile> Known => All;

    /// <summary>
    /// Build a profile for an arbitrary OpenAI-compatible endpoint.
    ///
    /// This is the escape hatch that makes the catalog optional rather than a
    /// gatekeeper: any self-hosted server is one call away without a code change.
    /// </summary>
    public static ProviderProfile OpenAiCompatible(
        string baseUrl,
        string model,
        string? apiKeyEnvVar = null,
        string? id = null) =>
        new(
            Id: id ?? "custom",
            DisplayName: $"custom ({baseUrl})",
            ChatTransport.OpenAiChatCompletions,
            BaseUrl: baseUrl,
            ApiKeyEnvVar: apiKeyEnvVar,
            DefaultModel: model,
            Quirks: ProviderQuirks.Default,
            RequiresKey: apiKeyEnvVar is not null);
}
