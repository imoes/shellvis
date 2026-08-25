using Shellvis.Core.Browser;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Drives a real Chromium against a real page.
///
/// Deliberately not a mock. Everything interesting about browser automation is what the
/// browser actually does -- whether a click lands, whether a reference survives, whether
/// the debugging port opens at all -- and none of that can be asserted against a
/// pretend browser. The page itself is written to a local file so the checks do not
/// depend on some website keeping its markup stable.
/// </summary>
internal static class BrowserProbe
{
    public static async Task<int> RunAsync(bool headless)
    {
        int failures = 0;

        Console.WriteLine("=== Browser ===");
        Console.WriteLine();

        failures += Guarding();

        string page = WriteTestPage();
        Console.WriteLine($"  test page: {page}");
        Console.WriteLine();

        await using var host = new BrowserHost();

        // Private urls are allowed here because the test page IS a local file; the
        // guard's own refusals are checked separately above, without a browser.
        var tools = new BrowserTools(host, new UrlGuard { AllowPrivate = true });

        Console.WriteLine("-- launch --");

        string launched = await tools.Launch(headless: headless).ConfigureAwait(false);
        Console.WriteLine(Indent(launched));

        failures += Check("a browser was launched and attached", host.IsConnected);

        if (!host.IsConnected)
        {
            Console.WriteLine();
            Console.WriteLine("Cannot continue without a browser.");
            return 1;
        }

        failures += Check(
            "it uses the dedicated Shellvis profile, not the default one",
            Directory.Exists(BrowserHost.ProfileDirectory));

        Console.WriteLine();

        failures += await NavigateAndSnapshotAsync(tools, page).ConfigureAwait(false);
        failures += await ActingAsync(tools).ConfigureAwait(false);
        failures += await StaleReferencesAsync(tools, page).ConfigureAwait(false);
        failures += await ConsoleAndShotAsync(tools).ConfigureAwait(false);

        Console.WriteLine("-- teardown --");
        string gone = await tools.Disconnect().ConfigureAwait(false);
        Console.WriteLine(Indent(gone));
        failures += Check("disconnect releases the browser", !host.IsConnected);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "All browser checks passed."
            : $"{failures} browser check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The url guard, checked without a browser.
    ///
    /// This is the part that matters even when nobody is watching: a url can arrive from
    /// a web page or a tool description, so the guard is what stops text from outside
    /// aiming the browser at the intranet.
    /// </summary>
    private static int Guarding()
    {
        Console.WriteLine("-- url guard --");
        int failures = 0;

        var strict = new UrlGuard { Blocklist = ["example.com", "tracker.net"] };

        failures += Check("a public url passes", strict.Refuse("https://wikipedia.org") is null);
        failures += Check("loopback is refused", strict.Refuse("http://127.0.0.1:8080") is not null);
        failures += Check("localhost is refused", strict.Refuse("http://localhost/admin") is not null);
        failures += Check("an RFC1918 address is refused", strict.Refuse("http://192.168.1.1") is not null);
        failures += Check("a 10.x address is refused", strict.Refuse("http://10.1.2.3/") is not null);
        failures += Check("a 172.16-31 address is refused", strict.Refuse("http://172.20.0.5") is not null);
        failures += Check("a 172.32 address is NOT private", strict.Refuse("http://172.32.0.5") is null);
        failures += Check("link-local is refused", strict.Refuse("http://169.254.169.254/") is not null);
        failures += Check("carrier-grade NAT is refused", strict.Refuse("http://100.64.0.1") is not null);

        // On a domain-joined machine a bare name resolves through the search domain,
        // which means an internal host.
        failures += Check("a single-label host is refused", strict.Refuse("http://intranet/") is not null);
        failures += Check("a .local name is refused", strict.Refuse("http://nas.local/") is not null);

        failures += Check("a blocklisted host is refused", strict.Refuse("https://example.com") is not null);

        // Suffix matching, or any subdomain defeats the list.
        failures += Check(
            "a subdomain of a blocklisted host is refused",
            strict.Refuse("https://ads.tracker.net/pixel") is not null);

        failures += Check(
            "a host merely ENDING in the pattern is not refused",
            strict.Refuse("https://notexample.com") is null);

        // file: would turn the browser into a file reader that bypasses the path checks
        // the file tools apply; javascript: executes in whatever page is open.
        failures += Check("file: is refused", strict.Refuse("file:///C:/Windows/win.ini") is not null);
        failures += Check("javascript: is refused", strict.Refuse("javascript:alert(1)") is not null);
        failures += Check("about:blank is allowed", strict.Refuse("about:blank") is null);
        failures += Check("a relative url is refused", strict.Refuse("/admin") is not null);

        var permissive = new UrlGuard { AllowPrivate = true };

        failures += Check(
            "allowPrivateUrls lets local addresses through",
            permissive.Refuse("http://127.0.0.1:8080") is null);

        failures += Check(
            "but allowPrivateUrls does NOT lift the scheme rule",
            permissive.Refuse("file:///C:/Windows/win.ini") is not null);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> NavigateAndSnapshotAsync(BrowserTools tools, string page)
    {
        Console.WriteLine("-- navigate and snapshot --");
        int failures = 0;

        // A local file is served over http by nothing, so it is reached as a file url --
        // which the guard refuses. The probe therefore navigates via the data-free
        // route the browser itself allows: a file url through the driver, bypassing the
        // tool-level guard on purpose and only here.
        string loaded = await tools.Navigate(new Uri(page).AbsoluteUri).ConfigureAwait(false);
        Console.WriteLine(Indent(loaded));

        failures += Check(
            "the tool refuses a file url even in the probe",
            loaded.StartsWith("Refused:"));

        // So the real navigation happens over http, from a data url the browser accepts.
        string dataUrl = "about:blank";
        await tools.Navigate(dataUrl).ConfigureAwait(false);

        string injected = await tools
            .Evaluate("document.open(); document.write(" + Quote(File.ReadAllText(page)) + "); document.close(); document.title")
            .ConfigureAwait(false);

        Console.WriteLine(Indent("injected page title: " + injected));
        failures += Check("the test page is loaded", injected.Contains("Shellvis"));

        string snapshot = await tools.Snapshot().ConfigureAwait(false);
        Console.WriteLine(Indent(snapshot));

        failures += Check("the snapshot names the page", snapshot.Contains("Shellvis Browser Test"));

        // The reference is the whole addressing contract; without it nothing else works.
        failures += Check("elements carry @eN references", snapshot.Contains("@e1"));

        failures += Check("a button is described as one", snapshot.Contains("button \"Klick mich\""));
        failures += Check("a text input is typed", snapshot.Contains("input[text]"));
        failures += Check("a link shows its target", snapshot.Contains("-> "));
        failures += Check("a checkbox reports its state", snapshot.Contains("[checked]"));
        failures += Check("a disabled control says so", snapshot.Contains("[disabled]"));

        // A password's value must never reach the transcript: it would be recorded in
        // the session database and sent to the model on every later turn.
        failures += Check(
            "a password field's value is masked",
            snapshot.Contains("(set)") && !snapshot.Contains("hunter2"));

        // Invisible elements are noise at best; at worst the model tries to click one.
        failures += Check("a hidden element is not listed", !snapshot.Contains("Unsichtbar"));

        failures += Check("headings appear for orientation", snapshot.Contains("h1 \"Shellvis"));

        string truncated = await tools.Snapshot(maxNodes: 3).ConfigureAwait(false);
        failures += Check(
            "a truncated snapshot says so rather than looking complete",
            truncated.Contains("truncated at 3"));

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> ActingAsync(BrowserTools tools)
    {
        Console.WriteLine("-- clicking and typing --");
        int failures = 0;

        string snapshot = await tools.Snapshot().ConfigureAwait(false);

        string? buttonRef = FindReference(snapshot, "Klick mich");
        string? fieldRef = FindReference(snapshot, "input[text]");
        string? coveredRef = FindReference(snapshot, "Verdeckt");

        failures += Check("the button has a reference", buttonRef is not null);
        failures += Check("the field has a reference", fieldRef is not null);

        if (buttonRef is not null)
        {
            string clicked = await tools.Click(buttonRef).ConfigureAwait(false);
            Console.WriteLine(Indent(clicked));

            failures += Check("the click reports what it hit", clicked.Contains("Clicked"));

            // The page records the click itself, which is the only honest proof that a
            // real event was delivered rather than merely dispatched.
            string result = await tools.Evaluate("document.getElementById('log').textContent")
                .ConfigureAwait(false);

            Console.WriteLine(Indent("page log: " + result));
            failures += Check("the page received a real click event", result.Contains("clicked"));
            failures += Check("the event was trusted", result.Contains("trusted=true"));
        }

        if (fieldRef is not null)
        {
            string typed = await tools.Type(fieldRef, "Shellvis war hier").ConfigureAwait(false);
            Console.WriteLine(Indent(typed));

            string value = await tools
                .Evaluate("document.getElementById('name').value")
                .ConfigureAwait(false);

            failures += Check("the text arrived in the field", value == "Shellvis war hier");

            // Component frameworks listen for input, not for keydown; insertText has to
            // raise it or every React form would silently ignore what was typed.
            string events = await tools
                .Evaluate("document.getElementById('log').textContent")
                .ConfigureAwait(false);

            failures += Check("an input event was raised", events.Contains("input"));

            string replaced = await tools.Type(fieldRef, "zweiter Text").ConfigureAwait(false);
            string after = await tools
                .Evaluate("document.getElementById('name').value")
                .ConfigureAwait(false);

            failures += Check("typing replaces by default", after == "zweiter Text");

            string appended = await tools.Type(fieldRef, " plus", replace: false).ConfigureAwait(false);
            string combined = await tools
                .Evaluate("document.getElementById('name').value")
                .ConfigureAwait(false);

            failures += Check("replace:false appends", combined == "zweiter Text plus");
        }

        // The covered-element check is the one that stops browser automation lying about
        // what it did: a button under a consent banner is the classic case.
        if (coveredRef is not null)
        {
            string blocked = await tools.Click(coveredRef).ConfigureAwait(false);
            Console.WriteLine(Indent(blocked));

            failures += Check(
                "a covered element is refused rather than clicked through",
                blocked.Contains("covered by"));

            failures += Check(
                "and the refusal names what is on top",
                blocked.Contains("overlay"));
        }
        else
        {
            failures += Check("the covered element has a reference", false);
        }

        string keyed = await tools.Press("enter").ConfigureAwait(false);
        failures += Check("a named key is sent", keyed.Contains("Pressed Enter"));

        string unknown = await tools.Press("f13").ConfigureAwait(false);
        failures += Check(
            "an unknown key lists the known ones instead of failing silently",
            unknown.Contains("pagedown"));

        string scrolled = await tools.Scroll(pages: 1).ConfigureAwait(false);
        Console.WriteLine(Indent(scrolled));
        failures += Check("scrolling reports where it went", scrolled.Contains("->"));

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// What happens to references after the page changes underneath them.
    ///
    /// This is the property the whole reference design exists for: a stale reference must
    /// fail loudly rather than resolve to whatever now sits at that index.
    /// </summary>
    private static async Task<int> StaleReferencesAsync(BrowserTools tools, string page)
    {
        Console.WriteLine("-- stale references --");
        int failures = 0;

        string snapshot = await tools.Snapshot().ConfigureAwait(false);
        string? buttonRef = FindReference(snapshot, "Klick mich");

        // Removing the element leaves the reference resolvable but detached, which is a
        // different mistake from a reference that never existed.
        await tools.Evaluate("document.getElementById('btn').remove(); 'gone'").ConfigureAwait(false);

        if (buttonRef is not null)
        {
            string detached = await tools.Click(buttonRef).ConfigureAwait(false);
            Console.WriteLine(Indent(detached));

            failures += Check(
                "a removed element is reported as removed, not as missing",
                detached.Contains("removed from the page"));
        }

        string never = await tools.Click("@e9999").ConfigureAwait(false);
        Console.WriteLine(Indent(never));

        failures += Check(
            "a reference that was never in the snapshot says so",
            never.Contains("not in the current snapshot"));

        string nonsense = await tools.Click("the blue button").ConfigureAwait(false);
        Console.WriteLine(Indent(nonsense));

        failures += Check(
            "a non-reference explains the format instead of guessing",
            nonsense.Contains("@e12") || nonsense.Contains("not an element reference"));

        // A navigation destroys the page-side table entirely. Every reference from
        // before must now fail, and say why.
        await tools.Navigate("about:blank").ConfigureAwait(false);

        if (buttonRef is not null)
        {
            string stale = await tools.Click(buttonRef).ConfigureAwait(false);
            Console.WriteLine(Indent(stale));

            failures += Check(
                "after navigation every reference is reported as stale",
                stale.Contains("no reference table") || stale.Contains("navigated"));
        }

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> ConsoleAndShotAsync(BrowserTools tools)
    {
        Console.WriteLine("-- console and screenshot --");
        int failures = 0;

        await tools.Evaluate("console.warn('shellvis-probe-warning'); 1").ConfigureAwait(false);
        await tools.Evaluate("setTimeout(() => { throw new Error('shellvis-probe-throw'); }, 0); 1")
            .ConfigureAwait(false);

        await Task.Delay(400).ConfigureAwait(false);

        string log = tools.Console();
        Console.WriteLine(Indent(log));

        failures += Check("a console warning is captured", log.Contains("shellvis-probe-warning"));
        failures += Check("an uncaught error is captured", log.Contains("shellvis-probe-throw"));

        string cleared = tools.Console(clear: true);
        failures += Check("clearing works", !tools.Console().Contains("shellvis-probe-warning"));

        string shot = await tools.Screenshot().ConfigureAwait(false);
        Console.WriteLine(Indent(shot));

        // To disk, not into the result: a base64 PNG is megabytes of context spent in
        // one call.
        failures += Check("the screenshot went to a file", shot.Contains(".png"));
        failures += Check("and the result is not the image itself", shot.Length < 400);

        Console.WriteLine();
        return failures;
    }

    /// <summary>Pull the @eN reference off the snapshot line containing a marker.</summary>
    private static string? FindReference(string snapshot, string marker)
    {
        foreach (string line in snapshot.Split('\n'))
        {
            if (!line.Contains(marker, StringComparison.Ordinal))
                continue;

            string trimmed = line.TrimStart();

            if (!trimmed.StartsWith('@'))
                continue;

            int space = trimmed.IndexOf(' ');
            return space > 0 ? trimmed[..space] : trimmed;
        }

        return null;
    }

    /// <summary>A JavaScript string literal, so the page's own markup can be injected.</summary>
    private static string Quote(string text) =>
        System.Text.Json.JsonSerializer.Serialize(text);

    private static string WriteTestPage()
    {
        string path = Path.Combine(Path.GetTempPath(), "shellvis-browser-test.html");

        // Covers exactly the cases the snapshot and the click checks assert: a plain
        // button, a text field, a link, a checkbox, a disabled control, a password, a
        // hidden element, and a button under an overlay.
        File.WriteAllText(path, """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Shellvis Browser Test</title>
        <style>
          body { font-family: sans-serif; margin: 2rem; height: 3000px; }
          #overlay { position: absolute; z-index: 10; background: rgba(0,0,0,.5);
                     width: 300px; height: 80px; }
          #under { position: absolute; z-index: 1; margin-top: 20px; margin-left: 20px; }
        </style></head>
        <body>
          <h1>Shellvis Browser Test</h1>
          <button id="btn">Klick mich</button>
          <input id="name" type="text" placeholder="Name eingeben">
          <input id="pw" type="password" value="hunter2">
          <a href="https://example.org/ziel">Ein Link</a>
          <input id="agree" type="checkbox" checked>
          <button id="dead" disabled>Deaktiviert</button>
          <span style="display:none">Unsichtbar</span>
          <h2>Verdeckter Bereich</h2>
          <div id="overlay" class="overlay"></div>
          <button id="under">Verdeckt</button>
          <pre id="log"></pre>
          <script>
            const log = document.getElementById('log');
            const say = (t) => { log.textContent += t + '\n'; };
            document.getElementById('btn').addEventListener('click',
              (e) => say('clicked trusted=' + e.isTrusted));
            document.getElementById('name').addEventListener('input',
              () => say('input'));
            document.getElementById('under').addEventListener('click',
              () => say('THE COVERED BUTTON WAS CLICKED'));
          </script>
        </body></html>
        """);

        return path;
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {what}");
        return ok ? 0 : 1;
    }

    private static string Indent(string text) =>
        "    " + text.TrimEnd().ReplaceLineEndings(Environment.NewLine + "    ");
}
