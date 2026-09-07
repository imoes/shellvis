using System.Text.Json;

namespace Shellvis.Core.Providers;

/// <summary>
/// How large the model's context window is, asked rather than assumed.
///
/// <b>Why this is worth a network call.</b> The input token count on its own answers "how
/// much did that cost"; as a share of the window it answers "how much room is left", which is
/// the question somebody actually has when answers start slowing down. That share needs a
/// denominator, and the three ways to get one are not equal: configuration is reliable but
/// nobody sets it, a model card is a guess about a server somebody else configured, and the
/// server itself simply knows.
///
/// <b>llama.cpp says so if asked.</b> Its <c>/props</c> endpoint reports the context size the
/// server was started with -- which is the number that matters, not whatever the model was
/// trained for: the same GGUF served with <c>-c 8192</c> and <c>-c 131072</c> has two
/// different windows, and only the server knows which.
///
/// <b>And a failure here is not a failure.</b> Every other provider answers /props with a 404
/// and that is fine: the share is then not shown, which is the honest outcome of not knowing.
/// Nothing about the assistant depends on this.
/// </summary>
public static class ContextWindow
{
    /// <summary>
    /// Ask the endpoint how big its window is. Null when it does not say.
    /// </summary>
    /// <param name="baseUrl">
    /// The chat endpoint, typically ending in <c>/v1</c>. That suffix is stripped: llama.cpp
    /// serves the OpenAI-compatible routes under <c>/v1</c> and its own properties at the
    /// root beside them.
    /// </param>
    /// <remarks>
    /// Three seconds, once, at startup. A number for a header is not worth making anybody
    /// wait for, and a provider that is slow to answer a diagnostic route is a provider whose
    /// window size can go unshown.
    /// </remarks>
    public static async Task<int?> LearnAsync(
        string? baseUrl,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(Root(baseUrl) + "/props", UriKind.Absolute, out Uri? props))
        {
            return null;
        }

        try
        {
            using var client = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(3) };

            using HttpResponseMessage answer = await client
                .GetAsync(props, cancellationToken)
                .ConfigureAwait(false);

            if (!answer.IsSuccessStatusCode)
                return null;

            await using Stream body = await answer.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            using JsonDocument json = await JsonDocument
                .ParseAsync(body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Read(json.RootElement);
        }
        catch (Exception)
        {
            // Deliberately broad, and deliberately silent. A 404 from a provider that is not
            // llama.cpp, a proxy that rejects the route, a DNS failure on a machine that is
            // offline: none of them is worth a warning about a number in a header.
            return null;
        }
    }

    /// <summary>
    /// Find the context size in whatever shape the server reports it.
    /// </summary>
    /// <remarks>
    /// Two shapes are known: <c>default_generation_settings.n_ctx</c> on current builds, and
    /// a bare <c>n_ctx</c> on older ones. Both are checked rather than picking one and
    /// breaking on the other server this estate happens to run.
    /// </remarks>
    private static int? Read(JsonElement root)
    {
        if (root.TryGetProperty("default_generation_settings", out JsonElement settings)
            && settings.TryGetProperty("n_ctx", out JsonElement nested)
            && nested.TryGetInt32(out int fromSettings)
            && fromSettings > 0)
        {
            return fromSettings;
        }

        if (root.TryGetProperty("n_ctx", out JsonElement bare)
            && bare.TryGetInt32(out int direct)
            && direct > 0)
        {
            return direct;
        }

        return null;
    }

    /// <summary>The server root: the chat base URL without its OpenAI-compatible suffix.</summary>
    public static string Root(string baseUrl)
    {
        string trimmed = baseUrl.TrimEnd('/');

        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3].TrimEnd('/')
            : trimmed;
    }
}
