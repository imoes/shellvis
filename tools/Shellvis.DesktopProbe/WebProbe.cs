using System.Globalization;

using Microsoft.Extensions.AI;

using Shellvis.Core.Agent;
using Shellvis.Core.Browser;
using Shellvis.Core.Config;
using Shellvis.Core.Providers;
using Shellvis.Core.Tools;
using Shellvis.Core.Web;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Two things the network never sees: HTML reduced to text, and one call's cost as a line.
///
/// <b>Why a harness rather than trying it out.</b> Both of these fail by producing something
/// that looks right. A stripper that decodes entities before it removes tags deletes text the
/// page actually showed -- and the result still reads as prose, so nobody notices the missing
/// sentence. A cost line that divides by a guessed window shows a percentage that is simply
/// wrong, on a display whose whole job is to say how close to the edge the conversation is.
/// Neither shows up in use.
///
/// No sockets are opened here. Everything below is a string going into a pure function.
/// </summary>
internal static class WebProbe
{
    public static int Run(bool live = false)
    {
        Console.WriteLine("web: pages reduced to text, and what a call cost\n");

        int failures = 0;

        failures += Stripping();
        failures += Sniffing();
        failures += Guarding();
        failures += Costs();

        // Off unless asked for. A harness that reaches the internet is one that fails in
        // the CI, on a train, and behind a proxy -- for reasons that have nothing to do
        // with the code it is meant to be checking.
        if (live)
            failures += Fetching().GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: script and style go before tags, tags before entities, so a page's own\n"
                + "escaped markup survives as text; the guard still refuses what it refused to the\n"
                + "browser; and a cost line shows no percentage it cannot stand behind."
            : $"{failures} check(s) failed.");

        Console.WriteLine();
        Console.WriteLine("NOT covered here: whether a real site answers, which needs the network,");
        Console.WriteLine("and whether a provider reports usage at all, which needs a provider.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Fetch one url and print exactly what the tool hands the model.
    /// </summary>
    /// <remarks>
    /// Worth a command of its own. "The assistant says it has no information about that
    /// page" has three possible causes -- the guard refused the url, the fetch came back
    /// with a login wall or a script shell, or the model never called the tool -- and the
    /// first two are answered in one second by looking at the result. Guessing between them
    /// from the transcript is how an afternoon goes.
    /// </remarks>
    public static async Task<int> FetchAsync(string url)
    {
        var tools = new WebTools(new UrlGuard());

        Console.WriteLine($"web_fetch {url}\n");
        Console.WriteLine(await tools.Fetch(url).ConfigureAwait(false));

        return 0;
    }

    private static int Stripping()
    {
        Console.WriteLine("-- html down to the words on the page --");

        int failures = 0;

        const string page = """
            <!doctype html>
            <html><head>
              <title>  Der   FTP-Zugang </title>
              <style>body { color: red }</style>
            </head>
            <body>
              <script>var secret = "do not print me";</script>
              <h1>Zugang</h1>
              <p>Host&nbsp;ist ftp.example.com &amp; der Port 21.</p>
              <p>Ein Beispiel: &lt;script&gt;alert(1)&lt;/script&gt;</p>
              <ul><li>Erstens</li><li>Zweitens</li></ul>
            </body></html>
            """;

        string text = PageText.Of(page);

        failures += Check("the heading survives", text.Contains("Zugang", StringComparison.Ordinal));

        failures += Check(
            "script bodies do not",
            !text.Contains("do not print me", StringComparison.Ordinal));

        failures += Check(
            "style bodies do not either",
            !text.Contains("color: red", StringComparison.Ordinal));

        failures += Check(
            "entities are decoded, so & reads as &",
            text.Contains("ftp.example.com & der Port 21", StringComparison.Ordinal));

        // The ordering check, and the reason the order is written down in the class doc.
        // Decoding first would turn this line into markup and then delete it -- removing
        // text the page displayed, with nothing to show that anything went missing.
        failures += Check(
            "a page's own escaped markup stays text",
            text.Contains("<script>alert(1)</script>", StringComparison.Ordinal));

        failures += Check(
            "list items end up on lines of their own",
            text.Contains("Erstens", StringComparison.Ordinal)
            && text.Contains("Zweitens", StringComparison.Ordinal)
            && !text.Contains("ErstensZweitens", StringComparison.Ordinal));

        failures += Check(
            "no tag survives anywhere",
            !text.Contains("<p>", StringComparison.Ordinal)
            && !text.Contains("<li>", StringComparison.Ordinal));

        failures += Check(
            "the title is read from head, which is otherwise thrown away",
            PageText.TitleOf(page) == "Der FTP-Zugang");

        failures += Check(
            "a page without a title says so rather than inventing one",
            PageText.TitleOf("<html><body>nichts</body></html>") is null);

        // Malformed markup is the normal case, not the exception.
        failures += Check(
            "an unclosed tag does not swallow the document",
            PageText.Of("<p>eins<p>zwei<div>drei").Contains("drei", StringComparison.Ordinal));

        failures += Check("empty in, empty out", PageText.Of("   ").Length == 0);

        return failures;
    }

    private static int Sniffing()
    {
        Console.WriteLine("\n-- when a body is markup and when it is not --");

        int failures = 0;

        failures += Check(
            "text/html is html",
            PageText.LooksLikeHtml("text/html", "<html><body>x</body></html>"));

        // The one that matters: running the strip over JSON eats every value with an angle
        // bracket in it, and the result still parses as text so nothing complains.
        failures += Check(
            "application/json is not, whatever it contains",
            !PageText.LooksLikeHtml("application/json", """{"a":"<b>"}"""));

        failures += Check(
            "text/plain is not",
            !PageText.LooksLikeHtml("text/plain", "<html> in a plain text file"));

        failures += Check(
            "xml is left alone",
            !PageText.LooksLikeHtml("application/xml", "<rss><item/></rss>"));

        failures += Check(
            "a server that asserts nothing gets sniffed",
            PageText.LooksLikeHtml(null, "<!doctype html><html>"));

        failures += Check(
            "and a sniff of prose finds no markup",
            !PageText.LooksLikeHtml(null, "Just a sentence."));

        Console.WriteLine("\n-- a login wall, which answers 200 like anything else --");

        // The shape a Jira Service Desk portal returns: a title, and configuration JSON
        // carrying a loginUrl. Readable text: two words.
        const string wall = """
            <html><head><title>Service Management</title></head><body>
            <script>window.config = {"xsrfToken":"AAAA","login":{"loginUrl":
            "/servicedesk/customer/portal/8/user/login","canReset":true}}</script>
            </body></html>
            """;

        failures += Check(
            "a portal login page is recognised as one",
            PageText.LooksLikeSignIn(wall, PageText.Of(wall)));

        // The important half. Most of the web has a sign-in link somewhere, and reporting
        // every such page as a wall would make the tool useless for reading anything.
        failures += Check(
            "a real page with a sign-in link in its navigation is not",
            !PageText.LooksLikeSignIn(
                "<html><body><a href='/signin'>Sign in</a><p>" + new string('x', 1200) + "</p></body></html>",
                new string('x', 1200)));

        // A weak marker on its own must not trip it. "signin" is in the navigation of half
        // the web, and an earlier version counted it.
        failures += Check(
            "a page whose only marker is the word signin is not a wall",
            !PageText.LooksLikeSignIn(
                "<html><body><a href='/signin'>Sign in</a><p>Der Artikel.</p></body></html>",
                "Sign in Der Artikel."));

        // And a page that really is a form must be, even when it is tiny.
        failures += Check(
            "a bare login form is a wall",
            PageText.LooksLikeSignIn(
                "<html><body><form><input type=\"password\"></form></body></html>",
                ""));

        Console.WriteLine("\n-- a page whose only text is its own configuration --");

        failures += Check(
            "a configuration blob is not mistaken for prose",
            PageText.LooksLikeScriptShell(
                """{"reasonKey":"com.atlassian.pocketknife.AnError","xsrfToken":"AAAA"}"""));

        failures += Check(
            "an article that merely opens with a brace is",
            !PageText.LooksLikeScriptShell(
                "{ so he said } and then the meeting went on for another hour, "
                + "which nobody had expected, least of all the chair."));

        failures += Check(
            "and prose is left alone",
            !PageText.LooksLikeScriptShell("Example Domain. This domain is for use in examples."));

        return failures;
    }

    private static int Guarding()
    {
        Console.WriteLine("\n-- the same guard the browser gets --");

        int failures = 0;

        var guard = new UrlGuard { Blocklist = ["example.com"] };

        failures += Check(
            "a public url is allowed",
            guard.Refuse("https://learn.microsoft.com/") is null);

        failures += Check(
            "a blocked host is refused",
            guard.Refuse("https://docs.example.com/x") is not null);

        failures += Check(
            "loopback is refused unless it was allowed on purpose",
            guard.Refuse("http://127.0.0.1:8080/") is not null);

        failures += Check(
            "an RFC1918 address too",
            guard.Refuse("http://10.1.2.3/admin") is not null);

        failures += Check(
            "file: is not fetchable",
            guard.Refuse("file:///C:/Windows/win.ini") is not null);

        // about: passes the guard because a browser can navigate to it. web_fetch has to
        // refuse it separately, which is why that check is in the tool and not here.
        failures += Check(
            "about: passes the guard, so the tool must reject it itself",
            guard.Refuse("about:blank") is null);

        return failures;
    }

    private static int Costs()
    {
        Console.WriteLine("\n-- what one call cost, as a line --");

        int failures = 0;

        failures += Check(
            "nothing measured says nothing",
            !TurnCost.Unknown.Measured && TurnCost.Unknown.Line().Length == 0);

        var noWindow = new TurnCost(12_345, 412, TimeSpan.FromSeconds(22.4));

        failures += Check(
            "with no window size there is no percentage",
            !noWindow.Line().Contains('%') && noWindow.Share is null);

        failures += Check(
            "the input count is there regardless",
            noWindow.Line().Contains("12k", StringComparison.Ordinal));

        var known = new TurnCost(12_288, 412, TimeSpan.FromSeconds(22.4), ContextTokens: 32_768);

        failures += Check(
            "with a known window the share is shown",
            known.Line().Contains("38%", StringComparison.Ordinal));

        failures += Check(
            "and it is a share of the window, not of anything else",
            known.Share is > 0.37 and < 0.38);

        // A context that has overflowed reports more input than the window; showing 137%
        // would be arithmetic rather than information.
        failures += Check(
            "an overflowing context caps at 100%",
            new TurnCost(40_000, 10, TimeSpan.FromSeconds(1), 32_768).Share == 1.0);

        failures += Check(
            "output tokens per second are wall clock",
            known.PerSecond is > 18.3 and < 18.5);

        failures += Check(
            "a call too fast to time reports no rate",
            new TurnCost(100, 5, TimeSpan.FromMilliseconds(10)).PerSecond is null);

        failures += Check(
            "no output means no rate to report",
            new TurnCost(100, 0, TimeSpan.FromSeconds(3)).PerSecond is null);

        // The abbreviation thresholds, because a header is where they show.
        failures += Check(
            "under a thousand is written out",
            new TurnCost(940, 0, TimeSpan.Zero).Line().Contains("in 940", StringComparison.Ordinal));

        // Built with the running culture rather than written as "9.4k". The line is read by
        // whoever is at the machine, so a German desktop should say 9,4k -- and hard-coding
        // the point would have made this check demand that the display be wrong for them.
        string oneDecimal = string.Create(CultureInfo.CurrentCulture, $"{9.4:F1}k");

        failures += Check(
            $"under ten thousand keeps one decimal ({oneDecimal})",
            new TurnCost(9_430, 0, TimeSpan.Zero).Line().Contains(oneDecimal, StringComparison.Ordinal));

        failures += Check(
            "above that it does not",
            new TurnCost(31_200, 0, TimeSpan.Zero).Line().Contains("31k", StringComparison.Ordinal));

        // Zero is a measurement; the absence of one is not. A provider that reports 0/0 and
        // a provider that reports nothing must not look the same on screen.
        failures += Check(
            "zero in and zero out counts as unmeasured",
            !new TurnCost(0, 0, TimeSpan.FromSeconds(4)).Measured);

        Console.WriteLine($"     · {known.Line()}");
        Console.WriteLine($"     · {noWindow.Line()}");

        return failures;
    }

    /// <summary>
    /// The two things that need a network: a page, and the model server's window size.
    /// </summary>
    private static async Task<int> Fetching()
    {
        Console.WriteLine("\n-- live, over the wire --");

        int failures = 0;

        var tools = new WebTools(new UrlGuard());

        string page = await tools.Fetch("https://example.com").ConfigureAwait(false);

        failures += Check(
            "example.com comes back as text",
            page.Contains("Example Domain", StringComparison.OrdinalIgnoreCase));

        failures += Check(
            "with the status in front of it",
            page.StartsWith("200 OK", StringComparison.Ordinal));

        failures += Check(
            "and no markup in it",
            !page.Contains("<body", StringComparison.OrdinalIgnoreCase));

        failures += Check(
            "raw=true returns the markup instead",
            (await tools.Fetch("https://example.com", raw: true).ConfigureAwait(false))
                .Contains("<body", StringComparison.OrdinalIgnoreCase));

        // The refusal has to happen before the request, not after it.
        failures += Check(
            "about:blank is refused rather than fetched",
            (await tools.Fetch("about:blank").ConfigureAwait(false))
                .Contains("not an http", StringComparison.OrdinalIgnoreCase));

        string missing = await tools
            .Fetch("https://example.com/definitely-not-here-9f2c")
            .ConfigureAwait(false);

        failures += Check(
            "a 404 is reported as a 404",
            missing.Contains("404", StringComparison.Ordinal));

        // A page that is genuinely behind a login, reached over the real network, because
        // the detection is about a response shape and a synthetic one proves less. Any
        // Atlassian service desk portal will do; this is Atlassian's own.
        string portal = await tools
            .Fetch("https://jira.atlassian.com/servicedesk/customer/portal/1")
            .ConfigureAwait(false);

        // What must NOT happen is the configuration blob coming back as "what the page
        // says". Which of the three explanations applies depends on what the portal
        // answers today -- a login wall, a script shell, or an error payload -- so the
        // check is on the outcome that would mislead a reader, not on the wording.
        failures += Check(
            "a live portal does not hand over its configuration as page prose",
            !portal.Contains("What the page says", StringComparison.Ordinal)
            || !portal.Contains("\":", StringComparison.Ordinal),
            portal.Length > 240 ? portal[..240] : portal);

        failures += await MeasuringAsync().ConfigureAwait(false);

        return failures;
    }

    /// <summary>
    /// Whether the model server actually reports what a call cost -- and the window size
    /// the percentage is divided by.
    /// </summary>
    /// <remarks>
    /// <b>This is the check the token display stands on.</b> An OpenAI-compatible server
    /// sends no usage at all on a streamed call unless the request asks for it with
    /// <c>stream_options.include_usage</c>, and llama.cpp is one of those: measured against
    /// this estate's endpoint, the same request returns a usage object with the flag and
    /// none without it. A display that shows nothing is indistinguishable from a display
    /// that is not wired up, which is exactly how it was reported.
    /// </remarks>
    private static async Task<int> MeasuringAsync()
    {
        int failures = 0;

        var loaded = ConfigStore.Load();
        ShellvisConfig settings = loaded.Config;

        // Through the resolver, not out of the config dictionary: that is what the running
        // application does, so a profile it can build and this cannot would be a difference
        // between the harness and the thing being measured.
        if (ProviderResolver.Find(settings.Model.Provider, settings) is not { } profile
            || profile.BaseUrl is not { Length: > 0 } baseUrl)
        {
            Console.WriteLine("     · no provider configured; skipping the live measurement");
            return 0;
        }

        int? window = await ContextWindow.LearnAsync(baseUrl).ConfigureAwait(false);

        Console.WriteLine(window is { } size
            ? $"     · {ContextWindow.Root(baseUrl)}/props reports n_ctx {size}"
            : $"     · {ContextWindow.Root(baseUrl)}/props said nothing; "
                + "the header will show tokens without a share");

        failures += Check("the endpoint says how big its window is", window is > 0);

        try
        {
            IChatClient client = ChatClientFactory.Create(
                profile, settings.Model.Model, requestTimeoutSeconds: 60);

            var asked = new List<ChatMessage>
            {
                new(ChatRole.User, "Antworte mit einem Wort: hallo"),
            };

            var options = new ChatOptions { MaxOutputTokens = 8 };

            var chunks = new List<ChatResponseUpdate>();

            await foreach (ChatResponseUpdate update in client
                .GetStreamingResponseAsync(asked, options)
                .ConfigureAwait(false))
            {
                chunks.Add(update);
            }

            ChatResponse response = chunks.ToChatResponse();

            failures += Check(
                "a STREAMED call reports what it cost  (needs stream_options.include_usage)",
                response.Usage is { InputTokenCount: > 0 });

            Console.WriteLine(response.Usage is { } usage
                ? $"     · streamed: in {usage.InputTokenCount}, out {usage.OutputTokenCount}"
                : "     · streamed: the provider reported no usage at all");

            // The same question both ways, and NEITHER capped.
            //
            // This is the link that cannot be verified by reading the code:
            // chat_template_kwargs is not part of the OpenAI schema, so it travels as a
            // rewritten request body, and a rewrite that silently failed would look
            // identical from here -- the call still works, it just thinks.
            //
            // The cap matters. Comparing against a call limited to 8 output tokens compares
            // the limit with the switch and would pass whatever the switch did.
            const string OneLine = "Antworte in genau einer Zeile: 1 | INFORMATION | Testzeile.";

            long? thoughtful = (await client
                .GetResponseAsync([new ChatMessage(ChatRole.User, OneLine)])
                .ConfigureAwait(false))
                .Usage?.OutputTokenCount;

            IChatClient quick = ChatClientFactory.Create(
                profile, settings.Model.Model, requestTimeoutSeconds: 120, thinking: false);

            long? terse = (await quick
                .GetResponseAsync([new ChatMessage(ChatRole.User, OneLine)])
                .ConfigureAwait(false))
                .Usage?.OutputTokenCount;

            Console.WriteLine(
                $"     · one line of output: {thoughtful} tokens thinking, {terse} not");

            failures += Check(
                "switching thinking off reaches the server",
                terse is > 0 && thoughtful is > 0 && terse < thoughtful / 4);
        }
        catch (Exception failure)
        {
            Console.WriteLine($"  ??   could not reach the model: {failure.Message}");
        }

        return failures;
    }

    private static int Check(string what, bool condition, string? detail = null)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");

        // Only on failure. A detail line beside a passing check is noise that trains people
        // to skim the output, which is how a FAIL goes unread.
        if (!condition && detail is { Length: > 0 })
            Console.WriteLine($"       got: {detail.ReplaceLineEndings(" ")}");

        return condition ? 0 : 1;
    }
}
