using System.Net.Http.Headers;
using System.Text.Json;

namespace Shellvis.Core.Providers;

/// <summary>What a provider says it can serve.</summary>
/// <param name="Models">Model ids, alphabetical. Empty when the endpoint would not say.</param>
/// <param name="Note">Why the list is empty or short, in plain words. Null when it is complete.</param>
public sealed record ModelListing(IReadOnlyList<string> Models, string? Note);

/// <summary>
/// Asks a provider which models it serves.
///
/// Worth doing rather than shipping a hardcoded list per provider: model names change
/// weekly, a stale list is a picker that offers models the endpoint rejects, and a local
/// llama.cpp or Ollama serves whatever the user happens to have pulled -- which no
/// hardcoded list could ever know.
///
/// Every OpenAI-compatible endpoint answers GET {baseUrl}/models with
/// <c>{"data":[{"id":...}]}</c>. Providers that do not are not treated as an error: the
/// caller falls back to the profile's default model and to free text, because a picker
/// that refuses to open because a listing failed is worse than one that offers less.
/// </summary>
public static class ModelDirectory
{
    /// <summary>
    /// Short, because this runs while a menu is waiting to open. A provider that needs
    /// longer than this to list its own models can be typed in by hand.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How many names to hand back.
    ///
    /// An aggregator lists several hundred; a flyout with several hundred items is not a
    /// chooser, it is a wall. The cap is reported rather than applied silently -- a
    /// truncated list that looks complete would have the user concluding a model is
    /// unavailable when it was merely cut off.
    /// </summary>
    private const int Cap = 30;

    public static async Task<ModelListing> ListAsync(
        ProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.BaseUrl is not { Length: > 0 } baseUrl)
            return new ModelListing([], "this provider has no base URL to ask.");

        try
        {
            using var http = new HttpClient { Timeout = Timeout };

            // The same variable the chat client uses, read the same way. A provider whose
            // key is unset still gets asked: local endpoints need none, and a 401 is a
            // better answer than a guess.
            if (profile.ApiKeyEnvVar is { Length: > 0 } variable
                && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } key)
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            using HttpResponseMessage response = await http
                .GetAsync($"{baseUrl.TrimEnd('/')}/models", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ModelListing(
                    [],
                    $"the endpoint answered {(int)response.StatusCode} when asked for its models.");
            }

            await using Stream body = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using JsonDocument document = await JsonDocument
                .ParseAsync(body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return new ModelListing([], "the endpoint's answer had no model list in it.");
            }

            List<string> names = [];

            foreach (JsonElement entry in data.EnumerateArray())
            {
                if (entry.TryGetProperty("id", out JsonElement id)
                    && id.GetString() is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);

            if (names.Count <= Cap)
                return new ModelListing(names, null);

            return new ModelListing(
                names.Take(Cap).ToArray(),
                $"showing {Cap} of {names.Count}; type a name for any of the rest.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Unreachable, unauthorised, slow or speaking something else. None of it is
            // worth throwing over: the caller has a default model and a text field.
            return new ModelListing([], $"could not ask this provider: {ex.Message}");
        }
    }
}
