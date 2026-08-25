using Shellvis.Core.Config;

namespace Shellvis.Core.Providers;

/// <summary>
/// Merges the built-in catalog with what the config file says.
///
/// Hermes' resolution chain is the model here (<c>resolve_provider_full</c>): built-in,
/// then the user's <c>providers:</c> section, then anything defined only there. The shape
/// is worth copying because it separates two things this project had conflated -- the
/// catalog of providers that ship, and the description of the endpoint in front of the
/// user. A company gateway, a second llama.cpp on another port, a colleague's vLLM: all of
/// those are configuration, and none of them should need a build.
///
/// Overriding is field-by-field rather than whole-entry. Replacing the entry would mean
/// re-stating the transport, the token parameter and the header quirks in order to change a
/// base URL, and a half-restated entry is how a provider ends up silently missing the
/// header that keeps it from being rate-limited.
/// </summary>
public static class ProviderResolver
{
    /// <summary>
    /// Every provider the user can pick: the built-ins, with overrides applied, plus the
    /// ones defined only in config.
    /// </summary>
    public static IReadOnlyList<ProviderProfile> All(ShellvisConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var result = new List<ProviderProfile>(ProviderCatalog.Known.Count + config.Providers.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProviderProfile builtIn in ProviderCatalog.Known)
        {
            result.Add(Apply(builtIn, Section(config, builtIn.Id)));
            seen.Add(builtIn.Id);
        }

        // Config-only entries come last and keep their file order, so a list the user
        // wrote reads back in the order they wrote it.
        foreach ((string id, ProviderSection section) in config.Providers)
        {
            if (!seen.Add(id))
                continue;

            if (Define(id, section) is { } defined)
                result.Add(defined);
        }

        return result;
    }

    /// <summary>
    /// Resolve one provider by id or alias, or null if nothing matches.
    /// </summary>
    public static ProviderProfile? Find(string? id, ShellvisConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(id))
            return null;

        // Aliases are resolved by the catalog, which knows that "glm" means "zai" and that
        // "claude" means "anthropic". A config override for the canonical id therefore
        // applies however the user spelled it.
        if (ProviderCatalog.Find(id) is { } builtIn)
            return Apply(builtIn, Section(config, builtIn.Id));

        foreach ((string key, ProviderSection section) in config.Providers)
        {
            if (key.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
                return Define(key, section);
        }

        return null;
    }

    private static ProviderSection? Section(ShellvisConfig config, string id)
    {
        foreach ((string key, ProviderSection section) in config.Providers)
        {
            if (key.Equals(id, StringComparison.OrdinalIgnoreCase))
                return section;
        }

        return null;
    }

    /// <summary>Overlay the fields the config actually set onto a built-in.</summary>
    private static ProviderProfile Apply(ProviderProfile profile, ProviderSection? section)
    {
        if (section is null)
            return profile;

        ProviderQuirks quirks = profile.Quirks with
        {
            MaxTokensParameter = Or(section.MaxTokensParameter, profile.Quirks.MaxTokensParameter),
            OmitTemperature = section.OmitTemperature ?? profile.Quirks.OmitTemperature,
            DefaultMaxTokens = section.DefaultMaxTokens ?? profile.Quirks.DefaultMaxTokens,

            // Headers merge rather than replace: a user adding one header should not have
            // to restate the two OpenRouter needs for attribution.
            ExtraHeaders = Merge(profile.Quirks.ExtraHeaders, section.ExtraHeaders),
        };

        return profile with
        {
            DisplayName = Or(section.Name, profile.DisplayName),
            BaseUrl = Or(section.BaseUrl, profile.BaseUrl),
            ApiKeyEnvVar = Or(section.ApiKeyEnvVar, profile.ApiKeyEnvVar),
            DefaultModel = Or(section.DefaultModel, profile.DefaultModel),
            Transport = ParseTransport(section.Transport) ?? profile.Transport,
            RequiresKey = section.RequiresKey ?? profile.RequiresKey,
            Quirks = quirks,
        };
    }

    /// <summary>
    /// Build a provider that exists only in the config.
    ///
    /// A base URL is required and its absence is a refusal rather than a default: an entry
    /// with no endpoint would be offered in the picker and fail on first use, which reads
    /// as a broken agent rather than as an incomplete config line.
    /// </summary>
    private static ProviderProfile? Define(string id, ProviderSection section)
    {
        if (section.BaseUrl is not { Length: > 0 } baseUrl)
            return null;

        // Keyless by default. A self-hosted endpoint is the overwhelmingly common reason to
        // write one of these, and demanding a key would make the common case the awkward
        // one. Setting apiKeyEnvVar or requiresKey opts in.
        bool requiresKey = section.RequiresKey
            ?? section.ApiKeyEnvVar is { Length: > 0 };

        return new ProviderProfile(
            id,
            Or(section.Name, id)!,
            ParseTransport(section.Transport) ?? ChatTransport.OpenAiChatCompletions,
            baseUrl,
            section.ApiKeyEnvVar,
            Or(section.DefaultModel, "default")!,
            new ProviderQuirks(
                Or(section.MaxTokensParameter, "max_tokens")!,
                section.OmitTemperature ?? false,
                section.DefaultMaxTokens,
                section.ExtraHeaders.Count > 0 ? section.ExtraHeaders : null),
            requiresKey);
    }

    /// <summary>
    /// Parse a transport name, or null when it is absent OR unrecognised.
    ///
    /// An unrecognised name keeping the existing transport is deliberate: a typo should
    /// leave a working provider rather than throw during startup, and the picker will show
    /// what is actually in force.
    /// </summary>
    private static ChatTransport? ParseTransport(string? name) =>
        name?.Trim().ToLowerInvariant() switch
        {
            "openai" or "openai_chat" or "chat" or "chatcompletions" => ChatTransport.OpenAiChatCompletions,
            "responses" or "codex" or "openai_responses" => ChatTransport.OpenAiResponses,
            "anthropic" or "anthropic_messages" => ChatTransport.AnthropicMessages,
            "gemini" or "gemini_native" => ChatTransport.GeminiNative,
            _ => null,
        };

    private static string? Or(string? candidate, string? fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();

    private static IReadOnlyDictionary<string, string>? Merge(
        IReadOnlyDictionary<string, string>? builtIn,
        Dictionary<string, string> extra)
    {
        if (extra.Count == 0)
            return builtIn;

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (builtIn is not null)
        {
            foreach ((string name, string value) in builtIn)
                merged[name] = value;
        }

        foreach ((string name, string value) in extra)
            merged[name] = value;

        return merged;
    }
}
