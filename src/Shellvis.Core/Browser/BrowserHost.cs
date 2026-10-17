using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Shellvis.Core.Browser;

/// <summary>Where a page came from, for the status line.</summary>
public enum BrowserOrigin
{
    /// <summary>Shellvis started this browser.</summary>
    Launched,

    /// <summary>It was already listening and Shellvis attached to it.</summary>
    Attached,
}

/// <summary>One tab.</summary>
public sealed record BrowserTab(string TargetId, string Title, string Url, bool IsCurrent)
{
    public override string ToString()
    {
        string mark = IsCurrent ? "* " : "  ";
        return $"{mark}{TargetId[..8]}  \"{Title}\"  {Url}";
    }
}

/// <summary>
/// Owns the browser connection and the page currently being driven.
///
/// Deliberately one page at a time. A model that can address several tabs at once has
/// to carry which tab each reference belongs to, and a stale reference then fails
/// against the wrong page instead of failing loudly. Switching tabs is an explicit act.
/// </summary>
public sealed class BrowserHost : IAsyncDisposable
{
    /// <summary>Default debugging port. 9222 is the convention every CDP tool uses.</summary>
    public const int DefaultPort = 9222;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly List<string> _console = [];
    private readonly Lock _consoleLock = new();

    private CdpConnection? _connection;
    private Process? _launched;

    /// <summary>
    /// Whether the process in _launched is ours to close.
    ///
    /// Separate from Origin because Origin is cleared on disconnect, and teardown runs
    /// after that: reading Origin in DisposeAsync left every launched browser running as
    /// an orphan. The probe only revealed it because Chrome kept logging after the test
    /// had finished.
    /// </summary>
    private bool _ownsProcess;
    private string? _sessionId;
    private string? _targetId;

    /// <summary>How the current browser was obtained.</summary>
    public BrowserOrigin? Origin { get; private set; }

    /// <summary>Browser build string, once connected.</summary>
    public string? BrowserVersion { get; private set; }

    public int Port { get; private set; } = DefaultPort;

    public bool IsConnected => _connection?.IsOpen == true && _sessionId is not null;

    /// <summary>
    /// The profile directory Shellvis launches browsers with.
    ///
    /// A dedicated directory is not a preference. Since Chrome 136 (and the matching
    /// Edge build) a browser started with --remote-debugging-port and the DEFAULT
    /// profile does not open the port at all -- silently, with no error. The
    /// restriction exists because that exact path was being used to lift cookies out of
    /// logged-in profiles. So the profile is separate and persistent: the user signs in
    /// once and those logins survive, which recovers most of the value of driving an
    /// already-authenticated browser.
    /// </summary>
    public static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shellvis",
        "BrowserProfile");

    /// <summary>Attach to a browser that is already listening, or explain why not.</summary>
    public async Task<string> ConnectAsync(
        int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        Port = port;

        JsonNode? version;

        try
        {
            version = await _http
                .GetFromJsonAsync<JsonNode>(
                    $"http://127.0.0.1:{port}/json/version", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"Nothing is listening for DevTools on 127.0.0.1:{port}. "
                + "Start a browser with browser_launch, or start one yourself with "
                + $"--remote-debugging-port={port} AND a --user-data-dir that is not the "
                + "default profile -- Chrome and Edge refuse to open the port on the "
                + "default profile since version 136.";
        }

        string? url = version?["webSocketDebuggerUrl"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(url))
            return $"127.0.0.1:{port} answered but offered no WebSocket endpoint.";

        BrowserVersion = version?["Browser"]?.GetValue<string>();

        _connection = await CdpConnection
            .ConnectAsync(new Uri(url), cancellationToken)
            .ConfigureAwait(false);

        _connection.EventReceived += OnEvent;
        Origin = BrowserOrigin.Attached;

        string attached = await AttachToFirstPageAsync(cancellationToken).ConfigureAwait(false);

        return $"Attached to {BrowserVersion} on port {port}. {attached}";
    }

    /// <summary>
    /// Start a browser with debugging enabled and attach to it.
    /// </summary>
    public async Task<string> LaunchAsync(
        string? executable = null,
        int port = DefaultPort,
        bool headless = false,
        CancellationToken cancellationToken = default)
    {
        string? path = executable ?? FindBrowser();

        if (path is null)
            return "Found neither Chrome nor Edge. Pass the path to a Chromium-based browser.";

        Directory.CreateDirectory(ProfileDirectory);

        var arguments = new List<string>
        {
            $"--remote-debugging-port={port}",
            $"--user-data-dir={ProfileDirectory}",
            "--no-first-run",
            "--no-default-browser-check",
            // Without this, a launch while the user already has the browser open can be
            // handed to the existing process, which then does not open the port.
            "--new-window",
            "about:blank",
        };

        if (headless)
            arguments.Insert(0, "--headless=new");

        var startInfo = new ProcessStartInfo(path) { UseShellExecute = false };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        _launched = Process.Start(startInfo);

        // The port appears a moment after the process does, so this polls rather than
        // sleeping a fixed amount: a cold profile takes seconds, a warm one milliseconds.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string last = "the browser did not open the debugging port.";

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);

            last = await ConnectAsync(port, cancellationToken).ConfigureAwait(false);

            if (IsConnected)
            {
                Origin = BrowserOrigin.Launched;
                _ownsProcess = true;
                return $"Launched {Path.GetFileName(path)}"
                    + (headless ? " headless" : string.Empty)
                    + $" with the Shellvis profile. {last}";
            }
        }

        return $"Started {Path.GetFileName(path)} but could not attach: {last}";
    }

    /// <summary>Chrome first, then Edge: both are Chromium and both speak CDP.</summary>
    public static string? FindBrowser()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] candidates =
        [
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Every ordinary tab. Extension and worker targets are not pages a user sees.</summary>
    public async Task<IReadOnlyList<BrowserTab>> ListTabsAsync(
        CancellationToken cancellationToken = default)
    {
        CdpConnection connection = Require();

        JsonNode? result = await connection
            .SendAsync("Target.getTargets", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var tabs = new List<BrowserTab>();

        if (result?["targetInfos"] is not JsonArray infos)
            return tabs;

        foreach (JsonNode? info in infos)
        {
            if (info is not JsonObject obj)
                continue;

            if (obj["type"]?.GetValue<string>() != "page")
                continue;

            string url = obj["url"]?.GetValue<string>() ?? string.Empty;

            // A background page of an installed extension is a "page" to the protocol
            // but is not a tab, and listing a dozen of them buries the real ones.
            if (url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string id = obj["targetId"]?.GetValue<string>() ?? string.Empty;

            tabs.Add(new BrowserTab(
                id,
                obj["title"]?.GetValue<string>() ?? string.Empty,
                url,
                id == _targetId));
        }

        return tabs;
    }

    /// <summary>Drive a different tab from now on.</summary>
    public async Task<string> SwitchTabAsync(
        string targetIdPrefix, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BrowserTab> tabs = await ListTabsAsync(cancellationToken).ConfigureAwait(false);

        List<BrowserTab> matches = [.. tabs.Where(t =>
            t.TargetId.StartsWith(targetIdPrefix, StringComparison.OrdinalIgnoreCase)
            || t.Title.Contains(targetIdPrefix, StringComparison.OrdinalIgnoreCase))];

        if (matches.Count == 0)
            return $"No tab matches '{targetIdPrefix}'. browser_tabs lists them.";

        // Ambiguity returns the candidates rather than picking one, the same convention
        // the window tools use: a guess that acts on the wrong tab is worse than a
        // question.
        if (matches.Count > 1)
        {
            var sb = new StringBuilder($"'{targetIdPrefix}' matches {matches.Count} tabs:");
            sb.AppendLine();

            foreach (BrowserTab tab in matches)
                sb.AppendLine(tab.ToString());

            return sb.ToString();
        }

        await AttachAsync(matches[0].TargetId, cancellationToken).ConfigureAwait(false);

        return $"Now driving \"{matches[0].Title}\" ({matches[0].Url}).";
    }

    private async Task<string> AttachToFirstPageAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<BrowserTab> tabs = await ListTabsAsync(cancellationToken).ConfigureAwait(false);

        if (tabs.Count == 0)
            return "The browser has no ordinary tab open; open one and connect again.";

        await AttachAsync(tabs[0].TargetId, cancellationToken).ConfigureAwait(false);

        return $"Driving \"{tabs[0].Title}\" ({tabs[0].Url}); {tabs.Count} tab(s) open.";
    }

    private async Task AttachAsync(string targetId, CancellationToken cancellationToken)
    {
        CdpConnection connection = Require();

        JsonNode? result = await connection.SendAsync(
            "Target.attachToTarget",
            new JsonObject { ["targetId"] = targetId, ["flatten"] = true },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _sessionId = result?["sessionId"]?.GetValue<string>()
            ?? throw new CdpException("The browser attached to the tab but returned no session.");

        _targetId = targetId;

        // Domains are opt-in per session. Runtime is what evaluates the snapshot script,
        // Page reports navigation, Log and Runtime together carry what the console shows.
        foreach (string domain in (string[])["Page.enable", "Runtime.enable", "Log.enable", "DOM.enable"])
            await SendAsync(domain, cancellationToken: cancellationToken).ConfigureAwait(false);

        lock (_consoleLock)
            _console.Clear();
    }

    /// <summary>Send a command to the page currently being driven.</summary>
    internal async Task<JsonNode?> SendAsync(
        string method,
        JsonObject? parameters = null,
        CancellationToken cancellationToken = default)
    {
        CdpConnection connection = Require();

        if (_sessionId is null)
            throw new InvalidOperationException("No tab is attached. Use browser_connect first.");

        return await connection
            .SendAsync(method, parameters, _sessionId, cancellationToken)
            .ConfigureAwait(false);
    }

    private CdpConnection Require() =>
        _connection ?? throw new InvalidOperationException(
            "Not connected to a browser. Use browser_launch to start one, or "
            + "browser_connect to attach to one that is already listening.");

    /// <summary>
    /// Collect console output as it happens.
    ///
    /// Polled after the fact rather than streamed, because the useful question is "what
    /// did the page complain about while I was doing that", and an error that scrolled
    /// past before the model asked would otherwise be gone.
    /// </summary>
    private void OnEvent(string method, string? sessionId, JsonNode? parameters)
    {
        if (sessionId is not null && sessionId != _sessionId)
            return;

        string? line = method switch
        {
            "Runtime.consoleAPICalled" => FormatConsoleCall(parameters),
            "Log.entryAdded" => FormatLogEntry(parameters),
            "Runtime.exceptionThrown" => FormatException(parameters),
            _ => null,
        };

        if (line is null)
            return;

        lock (_consoleLock)
        {
            _console.Add(line);

            // A page in a render loop can log thousands of lines a second; only the
            // recent ones are diagnostic and the rest would be an unbounded leak.
            if (_console.Count > 300)
                _console.RemoveRange(0, _console.Count - 300);
        }
    }

    /// <summary>
    /// Render an uncaught exception usefully.
    ///
    /// The obvious field, exceptionDetails.text, is almost always the bare word
    /// "Uncaught" -- the actual message lives on the thrown object's description. Using
    /// the obvious one produces the line "uncaught: Uncaught", which tells the reader
    /// nothing and is worse than no console tool at all, because it looks like an answer.
    /// </summary>
    private static string FormatException(JsonNode? parameters)
    {
        JsonNode? details = parameters?["exceptionDetails"];

        string message = details?["exception"]?["description"]?.GetValue<string>()
            ?? details?["exception"]?["value"]?.ToJsonString()
            ?? details?["text"]?.GetValue<string>()
            ?? "an exception with no description";

        // A stack trace's first frame is the only part worth the context; the rest is
        // framework noise on most real pages.
        string? where = details?["url"]?.GetValue<string>();
        int? line = details?["lineNumber"]?.GetValue<int>();

        string location = where is { Length: > 0 }
            ? $"  ({where}{(line is not null ? ":" + (line + 1) : string.Empty)})"
            : string.Empty;

        // Descriptions carry the whole stack; the first line is the message.
        int newline = message.IndexOf('\n');

        if (newline > 0)
            message = message[..newline];

        return $"uncaught: {message}{location}";
    }

    private static string? FormatConsoleCall(JsonNode? parameters)
    {
        string type = parameters?["type"]?.GetValue<string>() ?? "log";

        if (parameters?["args"] is not JsonArray args)
            return $"[{type}]";

        var parts = new List<string>();

        foreach (JsonNode? argument in args)
        {
            string? value = argument?["value"]?.ToJsonString()
                ?? argument?["description"]?.GetValue<string>()
                ?? argument?["className"]?.GetValue<string>();

            parts.Add(value ?? "?");
        }

        return $"[{type}] {string.Join(' ', parts)}";
    }

    private static string? FormatLogEntry(JsonNode? parameters)
    {
        JsonNode? entry = parameters?["entry"];

        if (entry is null)
            return null;

        string level = entry["level"]?.GetValue<string>() ?? "info";
        string text = entry["text"]?.GetValue<string>() ?? string.Empty;
        string? url = entry["url"]?.GetValue<string>();

        return $"[{level}] {text}" + (url is { Length: > 0 } ? $"  ({url})" : string.Empty);
    }

    /// <summary>What the page has logged since the tab was attached.</summary>
    public IReadOnlyList<string> DrainConsole(bool clear)
    {
        lock (_consoleLock)
        {
            List<string> copy = [.. _console];

            if (clear)
                _console.Clear();

            return copy;
        }
    }

    /// <summary>Where the driven tab currently is.</summary>
    public async Task<string> GetUrlAsync(CancellationToken cancellationToken = default)
    {
        JsonNode? result = await SendAsync(
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] = "location.href + ' | ' + document.title",
                ["returnByValue"] = true,
            },
            cancellationToken).ConfigureAwait(false);

        return result?["result"]?["value"]?.GetValue<string>() ?? "unknown";
    }

    public string Describe()
    {
        if (!IsConnected)
        {
            return "Not connected to a browser. browser_launch starts one with the "
                + "Shellvis profile; browser_connect attaches to one already listening.";
        }

        string origin = Origin == BrowserOrigin.Launched ? "launched by Shellvis" : "attached";

        return $"{BrowserVersion} on port {Port.ToString(CultureInfo.InvariantCulture)}, "
            + $"{origin}, driving target {_targetId?[..8]}.";
    }

    /// <summary>Drop the connection, leaving an attached browser running.</summary>
    public async Task DisconnectAsync()
    {
        if (_connection is not null)
        {
            _connection.EventReceived -= OnEvent;
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _sessionId = null;
        _targetId = null;
        Origin = null;
        BrowserVersion = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);

        // A browser Shellvis started is Shellvis' to clean up; one it merely attached to
        // belongs to the user and must survive. Killing the user's browser on window
        // close would be a memorable kind of bug.
        if (_ownsProcess && _launched is { HasExited: false })
        {
            try
            {
                _launched.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already gone, or not ours to kill any more.
            }
        }

        _launched?.Dispose();
        _http.Dispose();
    }
}
