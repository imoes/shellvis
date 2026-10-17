using System.Text;
using Shellvis.Core.Browser;

namespace Shellvis.Core.Tools;

/// <summary>
/// The browser as tools.
///
/// The read/write split follows what an action can cost. Navigating, snapshotting and
/// screenshotting observe; clicking and typing act, and a click on a page that is not
/// the user's own can place an order, send a message or delete something. So the reads
/// run silently in the conditional auto mode and the writes ask -- the same polarity as
/// everywhere else, applied to a surface where the consequences leave the machine.
/// </summary>
public sealed class BrowserTools(BrowserHost host, UrlGuard guard)
{
    private readonly BrowserHost _host = host;
    private readonly PageDriver _page = new(host);
    private readonly UrlGuard _guard = guard;

    [ShellvisTool(
        "browser_launch",
        SideEffect.Mutating,
        Description =
            "Start Chrome or Edge under Shellvis' control and attach to it. Uses a "
            + "dedicated Shellvis browser profile that keeps its logins between "
            + "sessions -- sign in there once and it stays signed in. Call this before "
            + "any other browser tool unless a browser is already listening.",
        Glyph = "globe")]
    public async Task<string> Launch(
        bool headless = false,
        int port = BrowserHost.DefaultPort,
        string? executablePath = null,
        CancellationToken cancellationToken = default)
    {
        if (_host.IsConnected)
            return "A browser is already connected: " + _host.Describe();

        return await _host
            .LaunchAsync(executablePath, port, headless, cancellationToken)
            .ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_connect",
        SideEffect.Mutating,
        Description =
            "Attach to a Chromium browser that is already running with remote debugging "
            + "enabled. Note that Chrome and Edge refuse to open the debugging port when "
            + "using the default profile, so this only reaches a browser started with a "
            + "separate --user-data-dir. Pass no arguments for the status of the current "
            + "connection.",
        Glyph = "globe")]
    public async Task<string> Connect(
        int port = BrowserHost.DefaultPort,
        bool status = false,
        CancellationToken cancellationToken = default)
    {
        if (status)
            return _host.Describe();

        return await _host.ConnectAsync(port, cancellationToken).ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_tabs",
        SideEffect.ReadOnly,
        Description =
            "List the browser's tabs, marking the one being driven, or switch to "
            + "another by id prefix or title. Only one tab is driven at a time, so "
            + "element references always belong to that tab.",
        PreviewParameter = "switchTo",
        Glyph = "globe")]
    public async Task<string> Tabs(
        string? switchTo = null, CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        if (switchTo is { Length: > 0 })
            return await _host.SwitchTabAsync(switchTo, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<BrowserTab> tabs = await _host
            .ListTabsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (tabs.Count == 0)
            return "The browser has no ordinary tabs open.";

        var sb = new StringBuilder();
        sb.Append(tabs.Count).AppendLine(" tab(s), * marks the one being driven:");

        foreach (BrowserTab tab in tabs)
            sb.AppendLine(tab.ToString());

        return sb.ToString();
    }

    [ShellvisTool(
        "browser_navigate",
        SideEffect.ReadOnly,
        Description =
            "Go to a url in the tab being driven and wait for it to load. Reading a "
            + "page is not a change, so this runs without asking -- but private and "
            + "internal addresses are refused unless config.yaml allows them.",
        PreviewParameter = "url",
        Glyph = "globe")]
    public async Task<string> Navigate(
        string url,
        int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        // Checked here rather than inside the driver so that the refusal never becomes a
        // protocol call at all: a blocked request must not leave the machine.
        if (_guard.Refuse(url) is { } refusal)
            return "Refused: " + refusal;

        return await _page.NavigateAsync(url, timeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_snapshot",
        SideEffect.ReadOnly,
        Description =
            "Read the current page as a compact tree of its visible interactive "
            + "elements, each with a reference like @e12. Every click and every typed "
            + "field is addressed by one of these references, so take a snapshot before "
            + "acting and again after anything changes.",
        Glyph = "globe")]
    public async Task<string> Snapshot(
        int maxNodes = 300, CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        return await _page.SnapshotAsync(maxNodes, cancellationToken).ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_read_text",
        SideEffect.ReadOnly,
        Description =
            "Read the visible text of the page, or of one referenced element. Use this "
            + "for the content of an article or a result; use browser_snapshot to find "
            + "things to act on.",
        PreviewParameter = "reference",
        Glyph = "globe")]
    public async Task<string> ReadText(
        string? reference = null,
        int maxChars = 4000,
        CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        return await _page.ReadTextAsync(reference, maxChars, cancellationToken).ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_click",
        SideEffect.Mutating,
        Description =
            "Click an element by its snapshot reference, for example @e12. Refuses if "
            + "something is covering the element rather than clicking whatever is on "
            + "top of it.",
        PreviewParameter = "reference",
        Glyph = "globe")]
    public async Task<string> Click(
        string reference,
        string button = "left",
        int clickCount = 1,
        CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        return await _page
            .ClickAsync(reference, button, clickCount, cancellationToken)
            .ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_type",
        SideEffect.Mutating,
        Description =
            "Type text into a referenced field. Replaces what is there by default. Set "
            + "pressEnter to submit a search or a form in the same call.",
        PreviewParameter = "text",
        Glyph = "globe")]
    public async Task<string> Type(
        string reference,
        string text,
        bool replace = true,
        bool pressEnter = false,
        CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        return await _page
            .TypeAsync(reference, text, replace, pressEnter, cancellationToken)
            .ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_press",
        SideEffect.Mutating,
        Description =
            "Send a named key to whatever has focus: enter, tab, escape, backspace, "
            + "delete, up, down, left, right, home, end, pageup, pagedown, space.",
        PreviewParameter = "key",
        Glyph = "globe")]
    public async Task<string> Press(
        string key, CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        return await _page.PressAsync(key, cancellationToken).ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_scroll",
        SideEffect.ReadOnly,
        Description =
            "Scroll the page by whole viewports, or bring a referenced element into "
            + "view. Scrolling reveals content without changing anything, so it runs "
            + "without asking.",
        Glyph = "globe")]
    public async Task<string> Scroll(
        string? reference = null,
        int pages = 1,
        CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        return await _page.ScrollAsync(reference, pages, cancellationToken).ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_back",
        SideEffect.ReadOnly,
        Description = "Go back one step in the driven tab's history.",
        Glyph = "globe")]
    public async Task<string> Back(CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        return await _page.BackAsync(cancellationToken).ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_screenshot",
        SideEffect.ReadOnly,
        Description =
            "Save a PNG of the page to disk and report the path. Use it when the layout "
            + "itself matters; the snapshot is cheaper for finding things to click.",
        Glyph = "globe")]
    public async Task<string> Screenshot(
        string? path = null,
        bool fullPage = false,
        CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        return await _page.ScreenshotAsync(path, fullPage, cancellationToken).ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_console",
        SideEffect.ReadOnly,
        Description =
            "Show what the page has logged to its console since the tab was attached, "
            + "including uncaught errors and failed requests. Read this when a page "
            + "behaves as though a click did nothing.",
        Glyph = "globe")]
    public string Console(bool clear = false)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        IReadOnlyList<string> lines = _host.DrainConsole(clear);

        if (lines.Count == 0)
            return "The page has logged nothing since it was attached.";

        var sb = new StringBuilder();
        sb.Append(lines.Count).AppendLine(" console line(s):");

        foreach (string line in lines)
            sb.Append("  ").AppendLine(line);

        return sb.ToString();
    }

    [ShellvisTool(
        "browser_evaluate",
        SideEffect.AlwaysAsk,
        Description =
            "Run a JavaScript expression in the page and return its value. The escape "
            + "hatch for what the other tools cannot express. Prefer them: script that "
            + "clicks or fills fields bypasses the checks that stop an action landing on "
            + "the wrong element.",
        PreviewParameter = "expression",
        Glyph = "globe")]
    public async Task<string> Evaluate(
        string expression, CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return _host.Describe();

        // AlwaysAsk, not merely mutating: arbitrary script in a logged-in page can read
        // every token the page holds and act as the user, and it is invisible to the
        // read-only classifier. A "do not ask again" answer must never cover it.
        return await _page.EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
    }

    [ShellvisTool(
        "browser_disconnect",
        SideEffect.Mutating,
        Description =
            "Let go of the browser. One that Shellvis launched is closed; one it merely "
            + "attached to keeps running.",
        Glyph = "globe")]
    public async Task<string> Disconnect(CancellationToken cancellationToken = default)
    {
        if (!_host.IsConnected)
            return "No browser is connected.";

        BrowserOrigin? origin = _host.Origin;

        await _host.DisconnectAsync().ConfigureAwait(false);

        return origin == BrowserOrigin.Launched
            ? "Disconnected. The browser Shellvis launched stays open until Shellvis exits."
            : "Disconnected. Your browser is untouched.";
    }
}
