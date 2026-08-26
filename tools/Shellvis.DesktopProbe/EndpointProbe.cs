using Shellvis.Core.Config;
using Shellvis.Core.Providers;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Base-URL normalisation: what a person types becoming what a client needs.
///
/// Worth its own harness because the failure it prevents is silent. Without the version
/// segment the first request returns 404, which reads like the endpoint being down rather
/// than like a text box filled in slightly wrong -- and the person who filled it in has no
/// way to tell those apart.
///
/// The other half is what must NOT be touched. A normaliser that helpfully rewrites a URL
/// that was already correct is worse than none, because it breaks the setups that worked.
/// </summary>
internal static class EndpointProbe
{
    public static int Run()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("-- what a person types --");

        foreach ((string input, string expected) in new[]
        {
            // The case this was built for.
            ("llamacpp03.example.com/laguna", "https://llamacpp03.example.com/laguna/v1"),
            ("api.openai.com", "https://api.openai.com/v1"),
            ("my-host.example.com/", "https://my-host.example.com/v1"),
            ("  spaces.example.com/llm  ", "https://spaces.example.com/llm/v1"),
        })
        {
            string? got = EndpointUrl.Normalise(input);
            failures += Expect(got == expected, $"'{input.Trim()}' -> {expected}", got);
        }

        Console.WriteLine();
        Console.WriteLine("-- a local server is http, not https --");

        // Defaulting everything to https would be tidier and would make the commonest case
        // -- a local inference server with no certificate -- the one that fails.
        foreach ((string input, string expected) in new[]
        {
            ("localhost:8080", "http://localhost:8080/v1"),
            ("127.0.0.1:11434", "http://127.0.0.1:11434/v1"),
            ("workstation.local:1234/llm", "http://workstation.local:1234/llm/v1"),
        })
        {
            string? got = EndpointUrl.Normalise(input);
            failures += Expect(got == expected, $"'{input}' -> {expected}", got);
        }

        Console.WriteLine();
        Console.WriteLine("-- already correct, so left alone --");

        foreach (string url in new[]
        {
            "https://api.openai.com/v1",
            "http://localhost:8080/v1",
            "https://generativelanguage.googleapis.com/v1beta/openai",
            "https://gateway.example.com/openai",
            "https://host.example.com/api",
            "https://host.example.com/v2",
        })
        {
            string? got = EndpointUrl.Normalise(url);
            failures += Expect(got == url, $"'{url}' is unchanged", got);
        }

        Console.WriteLine();
        Console.WriteLine("-- and a trailing slash is not a path --");

        failures += Expect(
            EndpointUrl.Normalise("https://api.openai.com/v1/") == "https://api.openai.com/v1",
            "a trailing slash is dropped rather than making /v1/ into /v1//v1",
            EndpointUrl.Normalise("https://api.openai.com/v1/"));

        Console.WriteLine();
        Console.WriteLine("-- what is not a URL at all --");

        // Null rather than a guess, so the caller refuses instead of building something
        // that cannot work and failing later somewhere less obvious.
        foreach (string junk in new[] { "", "   ", "file:///C:/x", "mailto:a@b.c", "://nohost", "http://" })
        {
            failures += Expect(
                EndpointUrl.Normalise(junk) is null,
                $"'{junk}' is refused",
                EndpointUrl.Normalise(junk) ?? "(null)");
        }

        Console.WriteLine();
        Console.WriteLine("-- and the resolver applies it --");

        // The point of normalising inside the resolver rather than only in the dialog: a
        // bare host written into config.yaml by hand has to work the same way.
        var config = new ShellvisConfig();
        config.Providers["house"] = new ProviderSection
        {
            BaseUrl = "llm.example.com/house",
            DefaultModel = "big",
        };

        ProviderProfile? defined = ProviderResolver.Find("house", config);

        failures += Expect(
            defined?.BaseUrl == "https://llm.example.com/house/v1",
            "a config-defined provider gets the same treatment",
            defined?.BaseUrl ?? "(not resolved)");

        config.Providers["openai"] = new ProviderSection { BaseUrl = "gateway.example.com" };

        ProviderProfile? overridden = ProviderResolver.Find("openai", config);

        failures += Expect(
            overridden?.BaseUrl == "https://gateway.example.com/v1",
            "and so does an override of a built-in",
            overridden?.BaseUrl ?? "(not resolved)");

        // An entry with no usable endpoint is skipped, not offered in the picker and then
        // failing on first use.
        config.Providers["broken"] = new ProviderSection { BaseUrl = "not a url at all" };

        failures += Expect(
            ProviderResolver.Find("broken", config) is null,
            "an unparseable endpoint means the provider is not offered",
            ProviderResolver.Find("broken", config)?.BaseUrl ?? "(skipped)");

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: a bare host is enough, a correct URL is untouched, and junk is refused."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    private static int Expect(bool condition, string what, string? got)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}"
            + (condition ? string.Empty : $"   got: {got ?? "(null)"}"));

        return condition ? 0 : 1;
    }
}
