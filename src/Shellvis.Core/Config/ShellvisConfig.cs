namespace Shellvis.Core.Config;

/// <summary>Which model to talk to.</summary>
/// <param name="Provider">
/// A catalog id (laguna, openrouter, ollama, lmstudio) or "custom" with a base url.
/// </param>
/// <param name="Model">Model name, or null for the provider default.</param>
/// <param name="BaseUrl">Endpoint, for a custom provider.</param>
/// <param name="ApiKeyEnvVar">Environment variable holding the key, if one is needed.</param>
public sealed class ModelSection
{
    public string Provider { get; set; } = "laguna";

    public string? Model { get; set; }

    public string? BaseUrl { get; set; }

    public string? ApiKeyEnvVar { get; set; }
}

/// <summary>Permission behaviour.</summary>
public sealed class ApprovalSection
{
    /// <summary>
    /// ask, auto-read, smart or yolo. Defaults to auto-read: provably read-only
    /// commands run silently and everything else prompts.
    /// </summary>
    public string Mode { get; set; } = "auto-read";

    /// <summary>Seconds a prompt waits before refusing on its own.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Commands allowed permanently, from earlier "always allow" answers.</summary>
    public List<string> CommandAllowlist { get; set; } = [];
}

/// <summary>Agent loop limits.</summary>
public sealed class AgentSection
{
    /// <summary>
    /// Model round trips a single user turn may consume.
    ///
    /// Raised from 20: a task driven by clicking costs two rounds per click when the
    /// tree has to be re-read, and "open the calculator and work out 7 x 6 by clicking"
    /// ran out of budget mid-calculation at twelve.
    /// </summary>
    public int MaxIterations { get; set; } = 30;

    /// <summary>Extra instructions appended to the built-in system prompt.</summary>
    public string? ExtraInstructions { get; set; }

    /// <summary>
    /// Whether to stream the answer. On by default; turn it off if a provider's
    /// streaming is broken.
    /// </summary>
    public bool Stream { get; set; } = true;

    /// <summary>
    /// Seconds a stream may go silent before it is abandoned.
    ///
    /// Raised from 90. A local llama.cpp serving a long answer pauses noticeably between
    /// chunks under load, and 90 seconds cut answers off mid-sentence. The watchdog exists
    /// to catch a stream that has genuinely died, not to hurry a slow one.
    /// </summary>
    public int StallTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Seconds a single request to the provider may take in total.
    ///
    /// This is the network timeout, and it was previously not set at all -- which meant
    /// the client library's own default of about 100 seconds applied, silently. A
    /// non-streaming call to a slow local model takes longer than that, and the request
    /// was cut off with nothing to say why.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Whether to ask, after each finished turn, if anything was learned worth keeping as a
    /// skill.
    ///
    /// On by default, because "get smarter over time" was asked for and the prompt-only
    /// version of it demonstrably never fired. It costs one extra, tool-less model call per
    /// turn that used a tool -- worth turning off on a metered provider, or when the same
    /// tasks repeat and there is nothing new to learn.
    /// </summary>
    public bool LearnFromTurns { get; set; } = true;
}

/// <summary>
/// A provider as the config file may describe it.
///
/// Two jobs in one shape, following Hermes' <c>providers:</c> section. An entry whose key
/// matches a built-in overrides just the fields it sets, so pointing "openai" at a company
/// gateway is two lines and does not fork the catalog. An entry whose key matches nothing
/// DEFINES a provider, which is what makes a self-hosted endpoint reachable without a
/// build -- the thing this project promised when it called providers "data, not code" and
/// then only shipped the built-in table.
///
/// Every field is nullable on purpose. Null means "keep whatever the built-in says", and
/// that distinction is the whole mechanism: an empty string is a value, and treating it as
/// absent would make it impossible to clear a base URL on purpose.
/// </summary>
public sealed class ProviderSection
{
    /// <summary>Display name. Defaults to the key for a new provider.</summary>
    public string? Name { get; set; }

    /// <summary>API root, including the version path (most endpoints need /v1).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Environment variable holding the key. A key entered in the UI is stored separately.</summary>
    public string? ApiKeyEnvVar { get; set; }

    /// <summary>Model to use when none is named.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>openai, responses, anthropic or gemini. Defaults to openai.</summary>
    public string? Transport { get; set; }

    /// <summary>Whether a key is required at all. Local endpoints need none.</summary>
    public bool? RequiresKey { get; set; }

    /// <summary>max_tokens or max_completion_tokens.</summary>
    public string? MaxTokensParameter { get; set; }

    /// <summary>Leave temperature out entirely; some reasoning models reject it.</summary>
    public bool? OmitTemperature { get; set; }

    /// <summary>Token cap to send when the caller names none.</summary>
    public int? DefaultMaxTokens { get; set; }

    /// <summary>Headers the provider requires, such as OpenRouter's attribution pair.</summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = [];
}

/// <summary>One MCP server, as it appears in the config file.</summary>
public sealed class McpServerSection
{
    /// <summary>stdio or http.</summary>
    public string Transport { get; set; } = "stdio";

    public string? Command { get; set; }

    public List<string> Args { get; set; } = [];

    public string? Url { get; set; }

    public Dictionary<string, string> Env { get; set; } = [];

    public Dictionary<string, string> Headers { get; set; } = [];

    public int ConnectTimeoutSeconds { get; set; } = 60;

    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = [];

    /// <summary>
    /// Tools from this server that may run without asking.
    ///
    /// Empty by default and deliberately so. This is the only place a remote tool can
    /// be granted silent execution, and it lives in the LOCAL config precisely so that
    /// a server cannot grant it to itself.
    /// </summary>
    public List<string> TrustReadOnly { get; set; } = [];
}

/// <summary>
/// Home Assistant connection details.
///
/// The token is referenced by variable name and never held here. That is not just
/// hygiene: this file is rewritten whenever a setting changes, so a token stored in it
/// would be copied around by ordinary use.
/// </summary>
public sealed class HomeAssistantSection
{
    /// <summary>
    /// Base url of the Home Assistant instance, without the /api suffix. Falls back to
    /// the HASS_URL environment variable when unset.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Environment variable holding the long-lived access token.</summary>
    public string TokenEnvVar { get; set; } = "HASS_TOKEN";
}

/// <summary>One hook entry, as it appears under an event name in the config file.</summary>
public sealed class HookSection
{
    /// <summary>The command line to run. Required.</summary>
    public string? Command { get; set; }

    /// <summary>Regex on the tool name. Empty matches every tool.</summary>
    public string? Matcher { get; set; }

    /// <summary>How long the hook may take before it is abandoned. Capped at 300.</summary>
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Dictation settings.
/// </summary>
/// <summary>
/// Watching Outlook, and deciding when that is worth saying something about.
///
/// <b>Every number here exists to keep it quiet.</b> A watcher that speaks too often is not
/// a lesser version of a useful one, it is worse than none: an alert for a routine arrival
/// teaches you to dismiss the next one unread, and then the one that mattered goes with it.
/// So the interval decides how soon it can notice, and the floor decides how often it may
/// interrupt, and they are separate on purpose.
/// </summary>
public sealed class WatchSection
{
    /// <summary>Whether Outlook is watched at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minutes between looks. Cheap: a folder query and a calendar restriction, no model.
    /// </summary>
    public int EveryMinutes { get; set; } = 3;

    /// <summary>
    /// How far ahead an appointment counts as starting soon.
    ///
    /// Fifteen, because that is roughly the time it takes to notice, read what the meeting
    /// is about and find the room or the link. Outlook's own default reminder is fifteen for
    /// the same reason.
    /// </summary>
    public int LeadMinutes { get; set; } = 15;

    /// <summary>
    /// The least time between two questions to the model.
    ///
    /// Ten, and it is not about cost. A turn against a local model takes one to three
    /// minutes on this machine; a watcher asking every three minutes would keep the model
    /// permanently busy answering itself, and the user's own question would queue behind it.
    /// </summary>
    public int QuietMinutes { get; set; } = 10;
}

/// <summary>
/// The remembered desk: how far back it is kept, and how far back it is consulted.
///
/// <b>Two different numbers, and confusing them is the whole reason this section exists.</b>
/// The store keeps a quarter of a year because that is roughly how long a thing on a desk
/// stays relevant, and because forgetting is a feature: an enrichment about a ticket that
/// closed in June reads as current in September and is not.
///
/// How much of that quarter is brought to bear on a question is a separate choice, and it is
/// the user's. Somebody who wants "what has been going on" to mean this week and somebody who
/// wants it to mean the quarter are both right about their own desk, so it is a setting --
/// and, because it is the kind of setting nobody edits a file for, a slider on the page.
/// </summary>
public sealed class DeskSection
{
    /// <summary>
    /// How far back the assistant looks when it is remembering, in days.
    ///
    /// Thirty by default: a month covers the mail somebody would still expect an answer
    /// about, and it is short enough that a search comes back with the thing rather than
    /// with everything. The slider on the reference page writes this.
    /// </summary>
    public int RememberDays { get; set; } = 30;

    /// <summary>
    /// How long anything is kept at all, in days.
    ///
    /// Ninety-two, which is a quarter. Configurable but not on the page: shortening it
    /// destroys what is already remembered, and that is not a thing to put behind a control
    /// somebody drags to see what it does.
    /// </summary>
    public int KeepDays { get; set; } = 92;
}

/// <summary>How the floating bar behaves towards other windows.</summary>
public sealed class WindowSection
{
    /// <summary>
    /// No longer used. Kept so that a <c>config.yaml</c> written by an older version still
    /// loads, and empty so that nothing reads as configured when it is not.
    ///
    /// <b>What it was.</b> A list of process names the bar would step behind while they were in
    /// the foreground, defaulted to the Microsoft remote desktop clients. It existed because the
    /// bar decided for itself when to get out of the way, and a windowed remote client has no
    /// measurable property that says "this one takes the keyboard" -- so it was named instead.
    ///
    /// <b>Why it is gone.</b> Naming windows was the weak part of that design and it was
    /// admitted as such in this very comment. The bar now registers as an application desktop
    /// toolbar and moves only when the shell says a full-screen application has the screen,
    /// which is the same signal that moves the taskbar. There is nothing left for a name list
    /// to correct. See <c>Shellvis.Core.Desktop.TaskbarBand</c>.
    /// </summary>
    public string[]? YieldTo { get; set; }
}

public sealed class VoiceSection
{
    /// <summary>
    /// Recording device index, or -1 for the Windows default.
    ///
    /// Needed because Windows speech can only open the DEFAULT device, and a machine
    /// with a headset, a webcam and a built-in array has three -- if the default is not
    /// the one being spoken into, dictation hears nothing and cannot say why.
    /// The transcript lists the devices with their indices at startup.
    /// </summary>
    public int DeviceIndex { get; set; } = -1;

    /// <summary>Recognition language, or empty for the machine's UI language.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Which recogniser to dictate with: whisper, sapi, or auto.
    ///
    /// <c>auto</c> means Whisper when its model is on disk and the Windows engine otherwise,
    /// which is what makes the first run work before anything is downloaded. <c>sapi</c> is
    /// kept reachable on purpose: the Windows engine is worse but instant, and on a machine
    /// where the model cannot be fetched it is the difference between poor dictation and
    /// none.
    /// </summary>
    public string Engine { get; set; } = "auto";

    /// <summary>
    /// Which Whisper model to use: tiny, base, small or medium.
    ///
    /// Chosen at install time and changeable here. Not downloaded automatically on a whim --
    /// the smallest is 74 MB and the largest 1.5 GB, and fetching that much without being
    /// asked is not something an agent should decide.
    /// </summary>
    /// <remarks>
    /// Null means "not stated here", which is not the same as the default: the installer
    /// records what the user picked during setup, and an unset value in the config has to
    /// defer to that rather than overrule it with a built-in guess.
    /// </remarks>
    public string? WhisperModel { get; set; }

    /// <summary>
    /// The Azure region a speech key belongs to, e.g. westeurope.
    ///
    /// Needed because Azure's speech endpoint is per-region with no global address, so the
    /// wrong region is a connection failure rather than an authentication one -- which reads
    /// like an outage instead of a typo. Ignored by every other provider.
    /// </summary>
    public string? AzureRegion { get; set; }
}

/// <summary>
/// Browser automation settings.
///
/// The blocklist and the private-address switch are here rather than being decided in
/// code because they are policy, and policy about which addresses an agent may reach
/// belongs to whoever owns the network.
/// </summary>
public sealed class BrowserSection
{
    /// <summary>Hosts the browser may never be pointed at, matched by suffix.</summary>
    public List<string> Blocklist { get; set; } = [];

    /// <summary>
    /// Whether loopback and internal addresses may be navigated to. Off by default so
    /// that a url arriving from a web page cannot aim the browser at the intranet.
    /// </summary>
    public bool AllowPrivateUrls { get; set; }

    /// <summary>DevTools port to use for launching and attaching.</summary>
    public int DebugPort { get; set; } = 9222;
}

/// <summary>
/// The whole configuration.
///
/// Deliberately plain mutable classes rather than records: YamlDotNet round-trips
/// properties, and a config file has to survive being written back out after a
/// programmatic change (an "always allow" answer, a model switch) without losing the
/// parts nothing touched.
/// </summary>
public sealed class ShellvisConfig
{
    /// <summary>
    /// Schema version, so a future change can migrate an old file rather than
    /// silently misreading it.
    /// </summary>
    public int ConfigVersion { get; set; } = 1;

    public ModelSection Model { get; set; } = new();

    public AgentSection Agent { get; set; } = new();

    public ApprovalSection Approvals { get; set; } = new();

    /// <summary>MCP servers, keyed by the short name used to namespace their tools.</summary>
    /// <summary>
    /// Provider overrides and additions, keyed by provider id. See <see cref="ProviderSection"/>.
    /// </summary>
    public Dictionary<string, ProviderSection> Providers { get; set; } = [];

    public Dictionary<string, McpServerSection> McpServers { get; set; } = [];

    /// <summary>Extra directories to search for skills, beyond the built-in one.</summary>
    public List<string> SkillDirectories { get; set; } = [];

    /// <summary>Home Assistant, if this machine's owner has one.</summary>
    public HomeAssistantSection HomeAssistant { get; set; } = new();

    /// <summary>Browser automation policy.</summary>
    public BrowserSection Browser { get; set; } = new();

    /// <summary>Dictation settings.</summary>
    public VoiceSection Voice { get; set; } = new();

    public WindowSection Window { get; set; } = new();

    public WatchSection Watch { get; set; } = new();

    /// <summary>How far back the desk is remembered, and how far back it is consulted.</summary>
    public DeskSection Desk { get; set; } = new();

    /// <summary>
    /// Hooks, keyed by event name, each holding a list of commands.
    ///
    /// Keyed by event rather than a flat list with an event field, because that is how
    /// someone reads it: "what happens before a tool runs" is the question, and the
    /// answer should be one place in the file.
    /// </summary>
    public Dictionary<string, List<HookSection>> Hooks { get; set; } = [];
}
