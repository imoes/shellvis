using Microsoft.Extensions.AI;
using Shellvis.Core.Providers;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Checks the provider catalogue: that every entry resolves, that every entry can build
/// a client, and that the failure modes say something useful.
///
/// No network. The point is not whether a given cloud is up -- that needs keys nobody
/// should put in a test -- but whether the catalogue is internally consistent. A typo in
/// a base url or a missing key variable is a startup crash for whoever selects that
/// provider, and it would otherwise be found by that user rather than here.
/// </summary>
internal static class ProviderProbe
{
    public static int Run()
    {
        int failures = 0;

        Console.WriteLine("=== Providers ===");
        Console.WriteLine();

        failures += Catalogue();
        failures += Construction();
        failures += Failures();
        failures += Quirks();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: every catalogue entry resolves and builds a client."
            : $"{failures} provider check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static int Catalogue()
    {
        Console.WriteLine($"-- {ProviderCatalog.Known.Count} entries --");
        int failures = 0;

        foreach (ProviderProfile profile in ProviderCatalog.Known)
        {
            string key = profile.ApiKeyEnvVar ?? "(none)";

            Console.WriteLine(
                $"  {profile.Id,-11} {profile.Transport,-22} {key,-20} {profile.DefaultModel}");
        }

        Console.WriteLine();

        // An id that does not round-trip through Find is unreachable from config, which
        // makes the entry decoration rather than a feature.
        foreach (ProviderProfile profile in ProviderCatalog.Known)
        {
            if (ProviderCatalog.Find(profile.Id)?.Id != profile.Id)
                failures += Check($"'{profile.Id}' is findable by its own id", false);
        }

        failures += Check("every id is findable by its own id", failures == 0);

        failures += Check(
            "ids are unique",
            ProviderCatalog.Known.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                == ProviderCatalog.Known.Count);

        // A base url without /v1 (or the provider's equivalent) produces a 404 on the
        // first request, which reads like an outage rather than a config mistake.
        foreach (ProviderProfile profile in ProviderCatalog.Known)
        {
            bool ok = profile.BaseUrl is null
                || Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out Uri? uri)
                    && uri.Scheme is "http" or "https"
                    && uri.AbsolutePath.Length > 1;

            if (!ok)
                failures += Check($"'{profile.Id}' has a usable base url ({profile.BaseUrl})", false);
        }

        failures += Check("every base url is absolute and has a path", failures == 0);

        // A cloud entry with no key variable cannot be authenticated and would fail with
        // a client-side argument error instead of a helpful message.
        foreach (ProviderProfile profile in ProviderCatalog.Known.Where(p => p.RequiresKey))
        {
            if (string.IsNullOrWhiteSpace(profile.ApiKeyEnvVar))
                failures += Check($"'{profile.Id}' requires a key and names a variable", false);
        }

        failures += Check("every key-requiring entry names its variable", failures == 0);

        failures += Check(
            "the local entries need no key",
            new[] { "laguna", "ollama", "lmstudio", "llamacpp" }
                .All(id => ProviderCatalog.Find(id)?.RequiresKey == false));

        Console.WriteLine();
        Console.WriteLine("-- aliases --");

        (string Alias, string Expected)[] aliases =
        [
            ("glm", "zai"), ("z.ai", "zai"), ("zhipu", "zai"),
            ("moonshot", "kimi"), ("grok", "xai"), ("claude", "anthropic"),
            ("google", "gemini"), ("gpt", "openai"), ("local", "llamacpp"),
        ];

        foreach ((string alias, string expected) in aliases)
        {
            failures += Check(
                $"'{alias}' resolves to {expected}",
                ProviderCatalog.Find(alias)?.Id == expected);
        }

        // Case and whitespace come from a hand-edited YAML file, so both have to work.
        failures += Check("lookup ignores case", ProviderCatalog.Find("OpenRouter")?.Id == "openrouter");
        failures += Check("lookup trims whitespace", ProviderCatalog.Find("  gemini ")?.Id == "gemini");
        failures += Check("an unknown name returns null", ProviderCatalog.Find("gemini2") is null);
        failures += Check("an empty name returns null", ProviderCatalog.Find("") is null);

        failures += Check(
            "KnownNames lists ids and aliases, for a useful error message",
            ProviderCatalog.KnownNames.Contains("gemini")
                && ProviderCatalog.KnownNames.Contains("glm"));

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// Every entry must actually build a client.
    ///
    /// This is the check that catches a transport wired into the catalogue but not into
    /// the factory -- exactly the state /responses was in until now.
    /// </summary>
    private static int Construction()
    {
        Console.WriteLine("-- client construction --");
        int failures = 0;

        foreach (ProviderProfile profile in ProviderCatalog.Known)
        {
            try
            {
                // A stand-in key, so the check is about construction and not about
                // whether this machine happens to hold a credential.
                IChatClient client = ChatClientFactory.Create(profile, apiKey: "probe-key");

                failures += Check($"{profile.Id} builds a client", client is not null);

                (client as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
                failures += Check($"{profile.Id} builds a client", false);
            }
        }

        // The one entry on a different protocol. Worth naming explicitly: it is the
        // reason the loop can reach Codex at all.
        failures += Check(
            "codex uses the /responses transport",
            ProviderCatalog.Find("codex")?.Transport == ChatTransport.OpenAiResponses);

        Console.WriteLine();
        return failures;
    }

    private static int Failures()
    {
        Console.WriteLine("-- failure reporting --");
        int failures = 0;

        // A missing key must be a sentence, not an ArgumentException from three frames
        // inside the OpenAI client.
        ProviderProfile cloud = ProviderCatalog.Find("openai")!;

        string? saved = Environment.GetEnvironmentVariable(cloud.ApiKeyEnvVar!);
        Environment.SetEnvironmentVariable(cloud.ApiKeyEnvVar!, null);

        try
        {
            ChatClientFactory.Create(cloud);
            failures += Check("a missing key is reported", false);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine("    " + ex.Message);

            failures += Check(
                "a missing key names the variable to set",
                ex.Message.Contains(cloud.ApiKeyEnvVar!));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
            failures += Check("a missing key is reported as a clear message", false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(cloud.ApiKeyEnvVar!, saved);
        }

        // The reserved native transports must point at the route that works rather than
        // simply refusing.
        var nativeAnthropic = ProviderCatalog.Find("anthropic")! with
        {
            Transport = ChatTransport.AnthropicMessages,
        };

        try
        {
            ChatClientFactory.Create(nativeAnthropic, apiKey: "probe-key");
            failures += Check("the native Anthropic transport refuses", false);
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine("    " + ex.Message);

            failures += Check(
                "and names the working alternative",
                ex.Message.Contains("'anthropic'"));
        }

        var nativeGemini = ProviderCatalog.Find("gemini")! with
        {
            Transport = ChatTransport.GeminiNative,
        };

        try
        {
            ChatClientFactory.Create(nativeGemini, apiKey: "probe-key");
            failures += Check("the native Gemini transport refuses", false);
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine("    " + ex.Message);
            failures += Check("and names the working alternative", ex.Message.Contains("'gemini'"));
        }

        // A local endpoint handed no key must still work: the OpenAI client rejects an
        // empty credential outright, while llama.cpp ignores whatever is sent.
        try
        {
            IChatClient local = ChatClientFactory.Create(ProviderCatalog.Find("llamacpp")!);
            failures += Check("a local endpoint needs no key", local is not null);
            (local as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
            failures += Check("a local endpoint needs no key", false);
        }

        Console.WriteLine();
        return failures;
    }

    private static int Quirks()
    {
        Console.WriteLine("-- quirks --");
        int failures = 0;

        // OpenRouter rate-limits unattributed traffic harder, so the headers are not
        // decoration.
        ProviderProfile openRouter = ProviderCatalog.Find("openrouter")!;

        failures += Check(
            "OpenRouter carries its attribution headers",
            openRouter.Quirks.ExtraHeaders?.ContainsKey("HTTP-Referer") == true
                && openRouter.Quirks.ExtraHeaders?.ContainsKey("X-Title") == true);

        // Newer OpenAI models reject max_tokens outright rather than ignoring it.
        failures += Check(
            "OpenAI uses max_completion_tokens",
            ProviderCatalog.Find("openai")!.Quirks.MaxTokensParameter == "max_completion_tokens");

        failures += Check(
            "and omits temperature, which its reasoning models reject",
            ProviderCatalog.Find("openai")!.Quirks.OmitTemperature);

        // Anthropic treats an absent cap as a 400, not as a default.
        failures += Check(
            "Anthropic declares a default token cap",
            ProviderCatalog.Find("anthropic")!.Quirks.DefaultMaxTokens is > 0);

        failures += Check(
            "every cloud entry declares a token cap",
            ProviderCatalog.Known
                .Where(p => p.RequiresKey)
                .All(p => p.Quirks.DefaultMaxTokens is > 0));

        Console.WriteLine();
        return failures;
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        return ok ? 0 : 1;
    }
}
