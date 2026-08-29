using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.UI.Dispatching;
using Shellvis.Core.Agent;
using Shellvis.Core.Broker;
using Shellvis.Core.Browser;
using Shellvis.Core.Config;
using Shellvis.Core.HomeAssistant;
using Shellvis.Core.Hooks;
using Shellvis.Core.Mail;
using Shellvis.Core.Mcp;
using Shellvis.Core.Memory;
using Shellvis.Core.Notes;
using Shellvis.Core.Office;
using Shellvis.Core.Permissions;
using Shellvis.Core.Providers;
using Shellvis.Core.Shell;
using Shellvis.Core.Sessions;
using Shellvis.Core.Skills;
using Shellvis.Core.Tools;

namespace Shellvis.Shell.Agent;

/// <summary>
/// Owns the agent for the lifetime of the window and marshals its events onto the UI
/// thread.
///
/// The threading arrangement is the substance here. Tool calls block for real
/// durations: a PowerShell pipeline takes seconds, a UI Automation snapshot takes
/// hundreds of milliseconds, a model round trip takes longer than either. Running any
/// of that on the UI thread would freeze the pill mid-animation. So the loop runs on a
/// background thread and every event is hopped back through the DispatcherQueue, which
/// is the only thread allowed to touch XAML.
///
/// One agent, one tool set, one PowerShell runspace per window. That is what makes the
/// session stateful in the way a shell has to be: a variable set in one turn is still
/// there in the next.
/// </summary>
internal sealed partial class AgentSession : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DesktopTools _desktop;
    private readonly PowerShellTools _shell;
    private readonly ToolRegistry _registry;
    private readonly AgentLoop _loop;

    /// <summary>
    /// Serialises turns. One AgentLoop holds one conversation history and one
    /// PowerShell runspace, neither of which tolerates two turns at once.
    /// </summary>
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    private CancellationTokenSource? _current;

    private AgentSession(
        DispatcherQueue dispatcher,
        DesktopTools desktop,
        PowerShellTools shell,
        ToolRegistry registry,
        AgentLoop loop,
        string providerLabel)
    {
        _dispatcher = dispatcher;
        _desktop = desktop;
        _shell = shell;
        _registry = registry;
        _loop = loop;
        ProviderLabel = providerLabel;
    }

    /// <summary>Human-readable description of the model in use, for the status line.</summary>
    public string ProviderLabel { get; private set; }

    /// <summary>How many tools the model can see.</summary>
    public int ToolCount => _registry.Count;

    /// <summary>Whether a turn is currently running.</summary>
    public bool IsBusy => _current is not null;

    /// <summary>
    /// Build a session.
    ///
    /// The endpoint is resolved from the environment rather than hard-coded so the
    /// same build can point at the internal llama.cpp host, a local Ollama, or
    /// OpenRouter without a rebuild. Falling back to the internal host keeps the
    /// out-of-the-box experience working on this machine.
    /// </summary>
    public static AgentSession Create(DispatcherQueue dispatcher, IApprovalGate approvals)
    {
        ConfigLoadResult configuration = ConfigStore.Load();
        ShellvisConfig settings = configuration.Config;

        // Environment variables still win over the file. That is what makes a
        // one-off "point at this endpoint instead" possible without editing config.
        string? baseUrl = Environment.GetEnvironmentVariable("SHELLVIS_BASE_URL")
            ?? settings.Model.BaseUrl;
        string? model = Environment.GetEnvironmentVariable("SHELLVIS_MODEL")
            ?? settings.Model.Model;

        List<string> warnings = [.. configuration.Warnings];

        ProviderProfile profile = ResolveProfile(settings, baseUrl, model, warnings);
        IChatClient client = ChatClientFactory.Create(
            profile, model, apiKey: null, settings.Agent.RequestTimeoutSeconds);

        var desktop = new DesktopTools();

        var comApartment = new ComApartment();
        var host = new PowerShellHost();
        var shellTools = new PowerShellTools(host);

        var registry = new ToolRegistry();
        registry.RegisterFrom(desktop);
        registry.RegisterFrom(shellTools);
        registry.RegisterFrom(new WslTools());

        // Remoting shares the runspace, which is what lets a PSSession survive between
        // turns: a variable set on the remote host is still there in the next call.
        registry.RegisterFrom(new RemoteTools(host));
        // The gallery shares the runspace, so an installed module is importable in the
        // same session it was fetched into.
        registry.RegisterFrom(new GalleryTools(host));
        registry.RegisterFrom(new OfficeTools());
        // The notes an assistant keeps about people and dates. A store of its own rather
        // than more memory: memory is capped and injected into every prompt, and what goes
        // in here is private material about third parties that must surface when it is
        // relevant and not travel with every unrelated question.
        //
        // Opened here rather than beside the memory store because the Outlook tools take it:
        // a mail listing carries what is already known about the people who sent it, so the
        // model does not have to remember to go and look.
        var notes = new NoteStore();

        registry.RegisterFrom(new OutlookTools(comApartment, notes));

        // Teams through the deep links it registers with Windows. Registered
        // unconditionally: without Teams the launcher refuses the scheme with a sentence
        // rather than raising the "choose an app" dialog, so an absent client is an answer
        // and not a hang.
        registry.RegisterFrom(new TeamsTools(comApartment));

        // Asking the user is a capability, so it is a tool. The flag behind it is what
        // keeps a scheduled run from opening a dialog at three in the morning: the
        // registry is shared with cron, so the choice cannot be made when it is
        // registered, only per call.
        var unattended = new System.Runtime.CompilerServices.StrongBox<bool>(false);

        registry.RegisterFrom(new ClarifyTools(
            new UnattendedClarifier(
                approvals as IClarifier ?? new NobodyHome(),
                () => unattended.Value)));

        // The live-Office tools share the STA apartment with Outlook. Registered
        // unconditionally: unlike Home Assistant there is nothing to configure, and
        // office_open_documents answering "nothing is open" is a useful answer rather
        // than a failed capability.
        registry.RegisterFrom(new OfficeComTools(new OfficeComClient(comApartment)));

        // Home Assistant is offered only when it can actually be reached. Advertising
        // ha_* tools without a token would mean the model plans around a capability
        // that fails on first use, which costs a round and reads as a broken agent
        // rather than as an absent integration.
        HomeAssistantClient? homeAssistant = HomeAssistantClient.TryCreate(
            settings.HomeAssistant.BaseUrl, settings.HomeAssistant.TokenEnvVar);

        if (homeAssistant is not null)
            registry.RegisterFrom(new HomeAssistantTools(homeAssistant));

        // The browser tools are always offered: unlike Home Assistant there is nothing
        // to configure before they work, and a browser that is not yet running is
        // something the model can fix itself with browser_launch.
        var browser = new BrowserHost();

        registry.RegisterFrom(new BrowserTools(
            browser,
            new UrlGuard
            {
                Blocklist = settings.Browser.Blocklist,
                AllowPrivate = settings.Browser.AllowPrivateUrls,
            }));

        // Skills are discovered before the system prompt is built, because the prompt
        // carries their index -- names and one-line descriptions only.
        var skills = new SkillIndex(
            new[] { ShellvisPaths.SkillsDirectory, ShellvisPaths.BundledSkillsDirectory }.Concat(settings.SkillDirectories));
        registry.RegisterFrom(new SkillTools(skills));

        // Read before the prompt is built, because the prompt carries what is remembered.
        var memory = new MemoryStore();
        registry.RegisterFrom(new MemoryTools(memory));

        registry.RegisterFrom(new NoteTools(notes));

        IReadOnlyList<HookDefinition> hookDefinitions = HookLoader.Load(settings.Hooks, warnings);

        // Consent is asked through the approval gate when it can do so, which keeps a
        // hook prompt and a tool prompt behind the same single-dialog lock.
        var hooks = new HookRunner(
            hookDefinitions,
            approvals as IHookConsent);

        // The configured mode, honoured for the first time. The field has existed since
        // the config was written and nothing ever read it, so a machine set to 'ask' or
        // 'yolo' silently behaved as 'auto-read'.
        var permissions = new PermissionPolicy();

        if (PermissionPolicy.TryParse(settings.Approvals.Mode, out PermissionMode configured))
        {
            permissions.Mode = configured;
        }
        else
        {
            warnings.Add(
                $"approvals.mode '{settings.Approvals.Mode}' is not a mode I know, so I am "
                + "using auto-read. Known: ask, auto-read, yolo.");
        }

        var loop = new AgentLoop(
            client,
            registry,
            approvals,
            new AgentOptions(
                MaxIterations: settings.Agent.MaxIterations,
                Stream: settings.Agent.Stream,
                StallTimeoutSeconds: settings.Agent.StallTimeoutSeconds,
                SystemPrompt: BuildSystemPrompt(
                    settings.Agent.ExtraInstructions,
                    skills.BuildPromptSection(
                        registry.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal)),
                    memory.PromptSection())),
            // Downgrades a provably read-only PowerShell query to silent, and escalates
            // a dangerous one to always-ask. Without this, every Get-CimInstance would
            // raise a prompt and the user would learn to click Allow without reading.
            new PowerShellRiskAssessor(),
            // The same client summarises. A separate cheaper model for this is the
            // eventual refinement; one endpoint is what the config offers today.
            new ContextCompactor(client),
            hooks,
            permissions);

        var session = new AgentSession(
            dispatcher, desktop, shellTools, registry, loop,
            $"{profile.DisplayName} / {model ?? profile.DefaultModel}")
        {
            Warnings = warnings,
            Permissions = permissions,
            Provider = profile,
            ModelName = model ?? profile.DefaultModel,
            _requestTimeoutSeconds = settings.Agent.RequestTimeoutSeconds,
            _skills = skills,
            _memory = memory,
            _notes = notes,
            _learnFromTurns = settings.Agent.LearnFromTurns,
            McpServers = ToMcpConfigs(settings),
            Registry = registry,
            HomeAssistant = homeAssistant,
            Browser = browser,
            Hooks = hooks,
            _unattended = unattended,
        };

        // Held separately from the loop so a scheduled run is not affected by an
        // interactive model switch mid-flight.
        session._cronClient = client;

        // Persistence is best-effort: a locked or unwritable database must not stop
        // the agent from working, it just means this conversation is not recorded. The
        // store is opened now but the session ROW is not created until something is
        // actually said -- see Record.
        try
        {
            session._store = new SessionStore();
        }
        catch (Exception)
        {
            session._store = null;
        }

        return session;
    }

    /// <summary>Configuration problems worth showing in the transcript at startup.</summary>
    public IReadOnlyList<string> Warnings { get; private init; } = [];

    /// <summary>
    /// The live permission mode. The same instance the loop reads, so a change here
    /// applies to the next tool call rather than the next session.
    /// </summary>
    public PermissionPolicy Permissions { get; private init; } = new();

    /// <summary>
    /// The network timeout to give a replacement client, so switching model does not
    /// silently drop back to the library's own hundred-second default.
    /// </summary>
    private int _requestTimeoutSeconds = 300;

    /// <summary>The skills index, shared with the tools and the reflection.</summary>
    private SkillIndex? _skills;

    /// <summary>The memory store, shared with the tool and the reflection.</summary>
    private Shellvis.Core.Memory.MemoryStore? _memory;

    /// <summary>The note database, shared with the tools and the reflection.</summary>
    private Shellvis.Core.Notes.NoteStore? _notes;

    /// <summary>Whether to run the post-turn reflection at all.</summary>
    private bool _learnFromTurns = true;

    /// <summary>
    /// True while a scheduled job is running, read by the clarify tool.
    ///
    /// A box rather than a plain field because the tool is registered before this
    /// session exists, so the two have to share one cell rather than a value.
    /// Serialised by the turn gate, so it cannot be true and false at once.
    /// </summary>
    private System.Runtime.CompilerServices.StrongBox<bool> _unattended = new(false);

    /// <summary>
    /// Ask, after a finished turn, whether it taught anything worth keeping.
    ///
    /// Deliberately after the answer is on screen and not before: the user has what they
    /// asked for, and this is bookkeeping. Awaited inside the turn gate rather than
    /// detached, because it shares the endpoint -- a local model with one slot would
    /// otherwise have the reflection and the next prompt queued against each other, and the
    /// user would be waiting on a call they never asked for without knowing why.
    /// </summary>
    private async Task ReflectAsync(TurnDigest digest, Action<AgentEvent> onEvent)
    {
        if (!_learnFromTurns || _skills is null || !digest.WorthReflecting)
            return;

        var reflector = new SkillReflector(_loop.Client, _skills, _memory, _notes);

        string? note = await reflector
            .ReflectAsync(digest, CancellationToken.None)
            .ConfigureAwait(false);

        // Null is the common and expected answer: most turns teach nothing. Only an actual
        // write gets a line, so the transcript does not fill with "learned nothing".
        if (note is { Length: > 0 })
            Post(onEvent, new AgentEvent.Announcement($"noted for later: {note}"));
    }

    /// <summary>
    /// Every provider that can be picked: the built-ins with config overrides applied,
    /// plus any defined only in the config file. Read fresh so an edit to config.yaml, or
    /// a provider just added through the dialog, shows up without a restart.
    /// </summary>
    public static IReadOnlyList<ProviderProfile> AvailableProviders() =>
        ProviderResolver.All(ConfigStore.Load().Config);

    /// <summary>
    /// Write a provider's settings and, if it is the one in use, apply them now.
    ///
    /// The key does NOT go into the config file. It goes to the DPAPI store, because this
    /// project already decided that config.yaml must never hold a resolved secret -- the
    /// same rule that keeps <c>${VAR}</c> literal on save. Passing null leaves whatever key
    /// is already stored, so editing a base URL does not silently clear a key.
    /// </summary>
    /// <returns>What to put in the transcript.</returns>
    public string ConfigureProvider(
        string id,
        string? name,
        string? baseUrl,
        string? defaultModel,
        string? apiKeyEnvVar,
        string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "a provider needs an id.";

        try
        {
            ShellvisConfig settings = ConfigStore.Load().Config;

            if (!settings.Providers.TryGetValue(id, out ProviderSection? section))
            {
                section = new ProviderSection();
                settings.Providers[id] = section;
            }

            // Only what was actually filled in is recorded. A blank field means "leave the
            // built-in alone", not "set it to empty" -- otherwise opening the dialog and
            // pressing save would wipe every default the entry inherits.
            if (!string.IsNullOrWhiteSpace(name))
                section.Name = name.Trim();

            if (!string.IsNullOrWhiteSpace(baseUrl))
                section.BaseUrl = baseUrl.Trim();

            if (!string.IsNullOrWhiteSpace(defaultModel))
                section.DefaultModel = defaultModel.Trim();

            if (!string.IsNullOrWhiteSpace(apiKeyEnvVar))
                section.ApiKeyEnvVar = apiKeyEnvVar.Trim();

            ConfigStore.Save(settings);

            if (apiKey is not null)
                SecretStore.Set(SecretStore.NameForProvider(id), apiKey);

            // Re-resolved from what was just written, so the answer describes the file
            // rather than the intent.
            if (ProviderResolver.Find(id, ConfigStore.Load().Config) is not { } resolved)
            {
                return $"saved settings for '{id}', but it still resolves to nothing. A "
                    + "provider that is not built in needs a base URL before it can be used.";
            }

            // Always switch to what was just configured, and to the model that was typed.
            //
            // Two defects lived in the line this replaces. It only applied anything when the
            // edited provider was already the current one, so configuring a different
            // provider wrote the file and changed nothing -- which is exactly what "I still
            // cannot configure the model" describes. And when it did apply, it passed the
            // CURRENT model rather than the one in the dialog, so typing a model name saved
            // it and then ignored it.
            //
            // Configuring a provider is a statement of intent to use it. Saving settings for
            // an endpoint and then continuing to talk to a different one is not a defensible
            // reading of that gesture.
            string wanted = string.IsNullOrWhiteSpace(defaultModel)
                ? resolved.DefaultModel
                : defaultModel.Trim();

            return $"saved settings for {resolved.DisplayName}. " + SetModel(resolved, wanted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or CryptographicException or ArgumentException)
        {
            return $"could not save settings for '{id}': {ex.Message}";
        }
    }

    /// <summary>Whether a key has been stored for this provider, for the dialog to show.</summary>
    public static bool HasStoredKey(string id) =>
        SecretStore.Has(SecretStore.NameForProvider(id));

    /// <summary>The provider this session currently talks to.</summary>
    public ProviderProfile Provider { get; private set; } = ProviderCatalog.Laguna;

    /// <summary>The model name in force, which may differ from the provider's default.</summary>
    public string ModelName { get; private set; } = ProviderCatalog.Laguna.DefaultModel;

    /// <summary>
    /// Point this session at a different model, keeping everything else.
    ///
    /// Only the transport is replaced. The conversation, the tool registry, the
    /// PowerShell runspace, the MCP connections and the permission mode all belong to the
    /// session rather than to the provider, and a user switching model in the middle of a
    /// task wants to keep exactly those. Rebuilding the session would be simpler to write
    /// and would throw the conversation away.
    ///
    /// The system prompt is deliberately NOT rebuilt: it is frozen per session so the
    /// provider's prefix cache keeps hitting, and the new provider has no cache to lose
    /// anyway. What the prompt says about persona, rules and skills is unchanged by a
    /// change of model.
    /// </summary>
    /// <returns>What to put in the transcript.</returns>
    public string SetModel(ProviderProfile profile, string? model)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string wanted = string.IsNullOrWhiteSpace(model) ? profile.DefaultModel : model.Trim();

        try
        {
            // Built before anything is committed: a missing API key throws here, and it
            // must leave the session talking to the model it was already using rather
            // than to nothing at all.
            IChatClient replacement = ChatClientFactory.Create(
                profile, wanted, apiKey: null, _requestTimeoutSeconds);

            _loop.Client = replacement;
            _cronClient = replacement;
            Provider = profile;
            ModelName = wanted;
            ProviderLabel = $"{profile.DisplayName} / {wanted}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // The two honest failures: no key for a provider that needs one, and a
            // transport that exists in the catalog but not in the factory. Both are
            // configuration, not defects, so they are reported and nothing changes.
            return $"stayed on {ProviderLabel}: {ex.Message}";
        }

        try
        {
            ShellvisConfig settings = ConfigStore.Load().Config;
            settings.Model.Provider = profile.Id;
            settings.Model.Model = wanted;
            ConfigStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"now talking to {ProviderLabel} (this session only: the config file "
                + "could not be written.)";
        }

        return $"now talking to {ProviderLabel}.";
    }

    /// <summary>
    /// Switch mode and remember it.
    ///
    /// Written back to config.yaml, because a mode is a standing decision about how much
    /// the user wants to be asked, and having to set it again at every start would train
    /// them to leave it wherever it happens to land. Persistence is best-effort: an
    /// unwritable config must not stop the mode from applying to this session.
    /// </summary>
    /// <returns>What to put in the transcript, or null if nothing changed.</returns>
    public string? SetPermissionMode(PermissionMode mode)
    {
        if (Permissions.Mode == mode)
            return null;

        Permissions.Mode = mode;

        string note = mode == PermissionMode.Yolo
            ? "permission mode: yolo. Nothing will ask, except installs, privileged "
              + "operations and browser script, which always confirm."
            : $"permission mode: {PermissionPolicy.Label(mode)}. {PermissionPolicy.Describe(mode)}";

        try
        {
            ShellvisConfig settings = ConfigStore.Load().Config;
            settings.Approvals.Mode = PermissionPolicy.ToConfigValue(mode);
            ConfigStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return note + " (this session only: the config file could not be written.)";
        }

        return note;
    }

    /// <summary>MCP servers to connect once the window is up.</summary>
    private IReadOnlyList<McpServerConfig> McpServers { get; init; } = [];

    /// <summary>
    /// The tool catalog, reachable from the shell.
    ///
    /// Internal rather than private because a clicked link runs a tool: the front door for
    /// the model and the front door for a click are deliberately the same one, so a mail
    /// opened from an answer and a mail opened by the model behave identically.
    /// </summary>
    internal ToolRegistry? Registry { get; init; }

    /// <summary>The Home Assistant connection, when one was configured.</summary>
    private HomeAssistantClient? HomeAssistant { get; init; }

    /// <summary>The browser connection. Idle until a browser tool is used.</summary>
    private BrowserHost? Browser { get; init; }

    /// <summary>Configured hooks. Empty unless the config declares some.</summary>
    private HookRunner? Hooks { get; init; }

    private McpHost? _mcp;

    /// <summary>Where the conversation is recorded, and the row it belongs to.</summary>
    private SessionStore? _store;
    private string? _sessionId;

    /// <summary>
    /// Register the broker tools if a broker is actually listening.
    ///
    /// The alternative -- always offering them -- would mean the model plans an elevated
    /// step, spends a round on it and is told the service is absent. On the great majority
    /// of machines (user-mode install) that would happen every time elevation looked
    /// useful. Reporting the absence once, in the transcript, is more honest and cheaper.
    /// </summary>
    private async Task ProbeBrokerAsync(CancellationToken cancellationToken)
    {
        if (Registry is null || _brokerProbed)
            return;

        _brokerProbed = true;

        var client = new BrokerClient();

        if (!await client.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            BrokerAvailability = "no privileged broker; elevated operations are unavailable "
                + "(install with Shellvis.Setup.exe --mode service).";

            return;
        }

        Registry.RegisterFrom(new BrokerTools(client));
        BrokerAvailability = "privileged broker connected; elevated operations are available.";
    }

    private bool _brokerProbed;

    /// <summary>What the broker probe concluded, for the transcript.</summary>
    public string? BrokerAvailability { get; private set; }

    /// <summary>
    /// Register the mail tools if the Thunderbird bridge answers.
    ///
    /// Only reported when it IS there. Outlook is already covered by its own tools, so on
    /// the usual machine a line saying Thunderbird is absent would be noise about
    /// something the user never asked for -- unlike the broker, whose absence changes what
    /// the agent can be asked to do.
    /// </summary>
    private async Task ProbeThunderbirdAsync(CancellationToken cancellationToken)
    {
        if (Registry is null || _thunderbirdProbed)
            return;

        _thunderbirdProbed = true;

        var provider = new ThunderbirdProvider();

        if (!await provider.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            return;

        Registry.RegisterFrom(new MailTools(provider));
        ThunderbirdAvailability = "Thunderbird bridge connected; mail_* tools are available.";
    }

    private bool _thunderbirdProbed;

    /// <summary>Set only when a bridge was found.</summary>
    public string? ThunderbirdAvailability { get; private set; }

    /// <summary>
    /// Fire a session-lifecycle hook, swallowing anything it does wrong.
    ///
    /// A lifecycle hook cannot block: there is nothing to veto at session start, and
    /// refusing to close a window because a script failed would be absurd. So the
    /// outcome is ignored and only the notes are of interest.
    /// </summary>
    private async Task<IReadOnlyList<string>> FireHookAsync(
        HookEvent value, CancellationToken cancellationToken = default)
    {
        if (Hooks is null || !Hooks.Has(value))
            return [];

        Hooks.SessionId = _sessionId;

        try
        {
            await Hooks.FireAsync(value, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing a lifecycle hook does justifies breaking the lifecycle.
        }

        return Hooks.DrainNotes();
    }

    /// <summary>
    /// Connect the configured MCP servers.
    ///
    /// Separate from construction and deliberately so: each connect launches a process
    /// or opens an HTTP session and can take seconds, and none of it should stand
    /// between the user and a usable window.
    /// </summary>
    public async Task<IReadOnlyList<string>> ConnectMcpAsync(CancellationToken cancellationToken = default)
    {
        // Fired here rather than in the constructor because this is the first point at
        // which the window exists: a hook denied consent needs a dialog to be denied in,
        // and there is no XamlRoot during construction.
        await FireHookAsync(HookEvent.OnSessionStart, cancellationToken).ConfigureAwait(false);

        // The broker is probed here rather than during construction for the same reason
        // Home Assistant is: the tools are only offered if they can work. The probe costs
        // up to three seconds against a machine with no service, which is why it does not
        // sit between the user and a usable window.
        await ProbeBrokerAsync(cancellationToken).ConfigureAwait(false);
        await ProbeThunderbirdAsync(cancellationToken).ConfigureAwait(false);

        if (McpServers.Count == 0 || Registry is null)
            return [];

        _mcp = new McpHost(Registry);

        IReadOnlyList<McpServerStatus> results = await _mcp
            .ConnectAllAsync(McpServers, cancellationToken)
            .ConfigureAwait(false);

        return results.Select(r => r.ToString()).ToList();
    }

    /// <summary>Translate the config file's server sections into client configurations.</summary>
    private static IReadOnlyList<McpServerConfig> ToMcpConfigs(ShellvisConfig settings)
    {
        var configs = new List<McpServerConfig>();

        foreach ((string name, McpServerSection section) in settings.McpServers)
        {
            configs.Add(new McpServerConfig(
                Name: name,
                Transport: section.Transport.Equals("http", StringComparison.OrdinalIgnoreCase)
                    ? McpTransport.Http
                    : McpTransport.Stdio,
                Command: section.Command,
                Arguments: section.Args,
                Url: section.Url,
                Environment: section.Env,
                Headers: section.Headers,
                ConnectTimeoutSeconds: section.ConnectTimeoutSeconds,
                Include: section.Include,
                Exclude: section.Exclude,
                TrustReadOnly: section.TrustReadOnly));
        }

        return configs;
    }

    /// <summary>
    /// Turn the configured provider into a profile.
    ///
    /// An explicit base url always wins, whatever the named provider was: someone who
    /// wrote a url meant it. Otherwise the catalog is consulted, and an unknown name
    /// falls back to the house endpoint rather than failing to start.
    /// </summary>
    private static ProviderProfile ResolveProfile(
        ShellvisConfig settings, string? baseUrl, string? model, List<string> warnings)
    {
        if (baseUrl is { Length: > 0 })
        {
            return ProviderCatalog.OpenAiCompatible(
                baseUrl,
                model ?? "laguna",
                settings.Model.ApiKeyEnvVar,
                settings.Model.Provider);
        }

        // Through the resolver, so a provider the config OVERRIDES or DEFINES is found
        // here. Going straight to the catalog meant a config-only provider could be
        // written down, appear nowhere, and fall back to laguna with a warning claiming it
        // was not in the catalog -- which was true and unhelpful.
        ProviderProfile? found = ProviderResolver.Find(settings.Model.Provider, settings);

        if (found is not null)
            return found;

        // A misspelt provider name used to fall back to the default in silence, which
        // is the worst outcome: the setting looks honoured, the answers come from
        // somewhere else, and nothing says so. Same reasoning as an unset ${VAR} being
        // kept literal rather than becoming an empty string.
        warnings.Add(
            $"provider '{settings.Model.Provider}' is neither in the catalog nor in the "
            + $"providers section of the config, falling back to {ProviderCatalog.Laguna.Id}. "
            + "Known: " + string.Join(", ", ProviderCatalog.KnownNames));

        return ProviderCatalog.Laguna;
    }

    /// <summary>
    /// Run one turn, invoking <paramref name="onEvent"/> on the UI thread for each
    /// event. Returns when the turn is over.
    /// </summary>
    public async Task RunTurnAsync(string prompt, Action<AgentEvent> onEvent)
    {
        // Only one turn at a time, and the previous one must have FINISHED before the
        // next begins -- not merely been asked to stop.
        //
        // An earlier revision only signalled cancellation and started immediately. Two
        // turns then overlapped on one AgentLoop, appending concurrently to the same
        // non-thread-safe history list, and the outgoing turn's cleanup nulled the
        // incoming turn's cancellation source so it could no longer be interrupted at
        // all. It surfaced the moment a human typed into the pill while an automated
        // turn was still running: both turns ended interrupted and neither answered.
        await _turnGate.WaitAsync().ConfigureAwait(false);

        try
        {
            await RunTurnCoreAsync(prompt, onEvent).ConfigureAwait(false);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private async Task RunTurnCoreAsync(string prompt, Action<AgentEvent> onEvent)
    {
        var cts = new CancellationTokenSource();
        _current = cts;

        // Accumulated as the turn runs, because afterwards the events are gone. Only the
        // tool names with a short preview and the final answer: enough for a reflection to
        // judge what was learned, small enough that the extra call stays cheap.
        List<string> steps = [];
        string answer = string.Empty;
        bool answered = false;

        try
        {
            // ConfigureAwait(false) throughout: this deliberately leaves the UI thread
            // so a blocking tool call cannot freeze the window. Everything that
            // touches XAML goes back through the dispatcher instead.
            Record("user", prompt);

            await Task.Run(async () =>
            {
                await foreach (AgentEvent evt in _loop.RunAsync(prompt, cts.Token).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case AgentEvent.ToolCompleted tool:
                            steps.Add($"{tool.Tool} -> {tool.Result}");
                            break;

                        case AgentEvent.AssistantMessage message:
                            answer = message.Text;
                            break;

                        // Only a turn that ended by answering is reflected on. An
                        // interrupted or failed one taught nothing reliable: the model
                        // stopped mid-thought, and half a procedure written down as a
                        // skill is worse than none.
                        case AgentEvent.TurnFinished { Reason: TurnEndReason.Answered }:
                            answered = true;
                            break;
                    }

                    // Recorded as the turn runs rather than at the end, so an
                    // interrupted or failed turn still leaves a trace of how far it got.
                    RecordEvent(evt);
                    Post(onEvent, evt);
                }
            }, cts.Token).ConfigureAwait(false);

            if (answered)
                await ReflectAsync(new TurnDigest(prompt, steps, answer), onEvent).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Post(onEvent, new AgentEvent.TurnFinished(TurnEndReason.Interrupted, 0, null));
        }
        catch (Exception ex)
        {
            // The window must survive anything a provider or tool can throw; a dead
            // pill with no explanation is the worst possible outcome.
            Post(onEvent, new AgentEvent.Failure(ex.Message));
            Post(onEvent, new AgentEvent.TurnFinished(TurnEndReason.Failed, 0, null));
        }
        finally
        {
            // Only clear it if it is still ours. Interlocked rather than a plain
            // assignment because a queued turn may already have replaced it.
            Interlocked.CompareExchange(ref _current, null, cts);
            cts.Dispose();
        }
    }

    // ------------------------------------------------------------ session manager

    /// <summary>One row for the session list.</summary>
    /// <param name="Info">The stored session.</param>
    /// <param name="Depth">
    /// How deep in a compaction chain it sits, so the list can indent a continuation
    /// under the conversation it came from. Without it a compacted conversation looks
    /// like several unrelated near-duplicates.
    /// </param>
    /// <param name="IsCurrent">Whether this is the conversation on screen.</param>
    public sealed record SessionRow(SessionInfo Info, int Depth, bool IsCurrent);

    /// <summary>The stored sessions, ordered so children follow their parents.</summary>
    public IReadOnlyList<SessionRow> ListSessions(string? search = null)
    {
        if (_store is null)
            return [];

        try
        {
            IReadOnlyList<SessionInfo> all = _store.ListSessions(200);

            if (search is { Length: > 0 })
            {
                // Search matches on message content, so the result is the set of
                // sessions that mentioned it -- which is the question someone typing
                // into a history search is actually asking.
                var matched = _store.Search(search, 50)
                    .Select(h => h.Session.Id)
                    .ToHashSet(StringComparer.Ordinal);

                all = all.Where(s =>
                        matched.Contains(s.Id)
                        || s.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return BuildTree(all);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Order sessions so a continuation follows what it continued, indented.
    ///
    /// Roots stay newest-first; children are attached to their parent wherever it is.
    /// A child whose parent was deleted or filtered out is promoted to a root rather
    /// than disappearing.
    /// </summary>
    private IReadOnlyList<SessionRow> BuildTree(IReadOnlyList<SessionInfo> sessions)
    {
        var byParent = sessions
            .Where(s => s.ParentId is not null)
            .GroupBy(s => s.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var present = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var rows = new List<SessionRow>();

        foreach (SessionInfo root in sessions.Where(
            s => s.ParentId is null || !present.Contains(s.ParentId)))
        {
            Append(root, 0);
        }

        return rows;

        void Append(SessionInfo session, int depth)
        {
            rows.Add(new SessionRow(session, depth, session.Id == _sessionId));

            if (!byParent.TryGetValue(session.Id, out List<SessionInfo>? children))
                return;

            // Depth is capped: a long compaction chain would otherwise indent itself
            // off the right edge of a 460 DIP window.
            foreach (SessionInfo child in children.OrderBy(c => c.StartedAt))
                Append(child, Math.Min(depth + 1, 3));
        }
    }

    /// <summary>
    /// Resume a stored conversation.
    ///
    /// The tool state is deliberately NOT restored: the PowerShell runspace, the UI
    /// snapshots and the MCP connections belong to this process and this moment. A
    /// resumed conversation gets its words back, not a claim that the machine is still
    /// in the state it was in yesterday.
    /// </summary>
    public IReadOnlyList<StoredMessage> ResumeSession(string sessionId)
    {
        if (_store is null)
            return [];

        Interrupt();

        IReadOnlyList<StoredMessage> messages = _store.GetMessages(sessionId);

        _loop.ReplaceHistory(messages
            .Where(m => m.Role is "user" or "assistant")
            .Select(m => new ChatMessage(
                m.Role == "user" ? ChatRole.User : ChatRole.Assistant,
                m.Content)));

        _sessionId = sessionId;
        _needsTitle = false;

        return messages;
    }

    /// <summary>Start a fresh conversation, leaving the old one stored.</summary>
    public void StartNewSession()
    {
        Interrupt();

        // Fired before the history is dropped, so a hook that wants to archive the
        // conversation still has a session id pointing at it.
        _ = FireHookAsync(HookEvent.OnSessionReset);

        _loop.ReplaceHistory([]);

        if (_store is null)
            return;

        try
        {
            if (_sessionId is not null)
                _store.EndSession(_sessionId, "user started a new session");

            // Left null on purpose: the new row appears when the user says something,
            // so pressing "new conversation" and then changing your mind leaves nothing
            // behind. Same reasoning as at startup.
            _sessionId = null;
            _needsTitle = true;
            _firstPrompt = null;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Delete a stored conversation. Refuses to delete the one in progress.</summary>
    public string DeleteSession(string sessionId)
    {
        if (_store is null)
            return "history is not available.";

        // Deleting the live conversation would leave the agent writing into a row that
        // no longer exists, and the user with a transcript that has no home.
        if (sessionId == _sessionId)
            return "that is the conversation you are in. Start a new one first.";

        try
        {
            return _store.DeleteSession(sessionId)
                ? "deleted."
                : "that session no longer exists.";
        }
        catch (Exception ex)
        {
            return $"could not delete it: {ex.Message}";
        }
    }

    /// <summary>Cancel the running turn, if any.</summary>
    public void Interrupt()
    {
        CancellationTokenSource? running = _current;
        if (running is null)
            return;

        try
        {
            running.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Finished between the null check and the cancel.
        }
    }

    private void Post(Action<AgentEvent> onEvent, AgentEvent evt) =>
        _dispatcher.TryEnqueue(() => onEvent(evt));

    /// <summary>Record the parts of an event worth keeping.</summary>
    private void RecordEvent(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentEvent.AssistantMessage e:
                Record("assistant", e.Text);
                break;

            case AgentEvent.ToolCompleted e:
                // Truncated: a tool result can be a hundred kilobytes, and storing that
                // verbatim for every call turns the history file into a liability
                // without making it more useful to read.
                Record("tool", Clip(e.Result, 4000), e.Tool);
                break;

            case AgentEvent.ToolRefused e:
                Record("tool", $"declined by the user: {e.Reason}", e.Tool);
                break;

            case AgentEvent.Failure e:
                Record("system", $"failure: {e.Message}");
                break;

            case AgentEvent.Compacted e:
                RotateSession(e);
                break;

            // The first user message becomes the title, so a session list is readable
            // without opening anything.
            case AgentEvent.TurnFinished when _needsTitle:
                _needsTitle = false;
                TrySetTitle();
                break;
        }
    }

    private bool _needsTitle = true;
    private string? _firstPrompt;

    private void Record(string role, string content, string? toolName = null)
    {
        if (_store is null)
            return;

        // The row is created on the first recorded message, not at startup. Creating it
        // eagerly meant every launch left an empty "Untitled, 0 msg" entry behind, and
        // after a dozen restarts the session manager was mostly noise -- which defeats
        // the feature it is part of. A conversation that never happened is not history.
        if (_sessionId is null)
        {
            try
            {
                _sessionId = _store.CreateSession(ProviderLabel, "Untitled");
            }
            catch (Exception)
            {
                return;
            }
        }

        _firstPrompt ??= role == "user" ? content : null;

        try
        {
            _store.AddMessage(_sessionId, role, content, toolName);
        }
        catch (Exception)
        {
            // A failed write must never interrupt the conversation it is recording.
        }
    }

    /// <summary>
    /// Close the current session row and open a successor pointing back at it.
    ///
    /// Compaction is lossy, so the verbatim history has to stay somewhere. Rotating
    /// leaves it in the closed session and puts the summary at the head of the new
    /// one, which is what makes a compaction chain readable afterwards instead of
    /// being a summary with nothing behind it.
    /// </summary>
    private void RotateSession(AgentEvent.Compacted compaction)
    {
        if (_store is null || _sessionId is null)
            return;

        try
        {
            string previous = _sessionId;
            _store.EndSession(previous, "compaction");

            _sessionId = _store.CreateSession(
                ProviderLabel, Clip(_firstPrompt ?? "Continued", 60), parentId: previous);

            if (compaction.Summary is { Length: > 0 } summary)
                _store.AddMessage(_sessionId, "system", summary);
        }
        catch (Exception)
        {
            // Losing the rotation costs lineage in the history file, not the turn.
        }
    }

    private void TrySetTitle()
    {
        if (_store is null || _sessionId is null || _firstPrompt is null)
            return;

        try
        {
            _store.SetTitle(_sessionId, Clip(_firstPrompt.ReplaceLineEndings(" "), 70));
        }
        catch (Exception)
        {
        }
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string BuildSystemPrompt(string? extra, string skillIndex, string memory)
    {
        var sb = new System.Text.StringBuilder(BasePrompt);

        // Today's date, which was missing and cost a wrong answer.
        //
        // Asked "what is on this week", the model answered with appointments dated August
        // 2024 -- two years out, because a model's only sense of the date is its training
        // data and nothing here corrected it. Any question with "today", "this week" or
        // "tomorrow" in it is unanswerable without this line, and the failure is invisible:
        // the answer looks like an answer.
        //
        // The week's boundaries are given rather than left to be derived. With only today's
        // date the model resolved "this week" to the right RANGE and then mislabelled which
        // weekday each date was, off by one -- date arithmetic across a week boundary is a
        // thing models get wrong, and there is no reason to make it do any.
        DateTime now = DateTime.Now;
        int sinceMonday = ((int)now.DayOfWeek + 6) % 7;
        DateTime monday = now.Date.AddDays(-sinceMonday);

        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        sb.AppendLine().AppendLine()
          .Append("Today is ").Append(now.ToString("dddd, d MMMM yyyy", invariant))
          .Append(", local time ").Append(now.ToString("HH:mm", invariant)).AppendLine(".")
          .Append("This week runs Monday ").Append(monday.ToString("yyyy-MM-dd", invariant))
          .Append(" to Sunday ").Append(monday.AddDays(6).ToString("yyyy-MM-dd", invariant))
          .Append(". Yesterday was ").Append(now.AddDays(-1).ToString("yyyy-MM-dd", invariant))
          .Append(" and tomorrow is ").Append(now.AddDays(1).ToString("yyyy-MM-dd", invariant))
          .AppendLine(".")
          .Append("Never guess a date and never work one out if it is written above. When a "
              + "tool takes a date range, pass it explicitly rather than relying on its "
              + "default.");

        // What is remembered comes BEFORE the skill index, and both come after the rules.
        // Facts first because they are short and always relevant, where the index is a
        // menu the model consults only when it needs one.
        if (memory.Length > 0)
            sb.AppendLine().AppendLine().Append(memory);

        // The skill index goes AFTER the working rules, so the rules frame how to use
        // it rather than being buried under a list.
        if (skillIndex.Length > 0)
            sb.AppendLine().AppendLine().Append(skillIndex);

        if (!string.IsNullOrWhiteSpace(extra))
            sb.AppendLine().AppendLine("## From the user configuration").AppendLine().Append(extra.Trim());

        return sb.ToString();
    }

    private const string BasePrompt =
        """
        You are Shellvis, an agent that operates this Windows machine on the user's behalf.

        Voice: you speak in announcements, in the style of a stage announcer. Keep them
        short. Never let the persona get in the way of being precise about what you did.

        Language: reply in the language the user wrote in.

        Formatting: every answer is Markdown, always, whether or not anyone asked for it.
        The console renders it. What it renders is headings, bullet and numbered lists,
        **bold**, *italic*, `inline code`, ```fenced code blocks```, [links](target) and
        GitHub-style tables. Use a table when the answer really is a grid, and a list
        otherwise. Images and block quotes are NOT rendered and would reach the user as raw
        punctuation.

        Link back to what you are talking about. When you name a mail, write it as
        [subject](shellvis:mail/<the id from mail_list>) so the user can open it in Outlook
        from your answer. A claim about a message the user cannot get to is a claim they
        have to take on trust.

        You drive the graphical desktop, not only the shell. desktop_analyze returns the
        full element tree of any window with a reference for every item in it; ui_click,
        ui_set_text, ui_send_keys and ui_read_text act on those references, and
        screen_capture takes a picture. Never say you cannot see a window, cannot read what
        is on screen or cannot send input. You can, and saying otherwise misreports your own
        tools to the user.

        When the user names an application, work in that application. Computing the same
        answer another way answers a different question: "open the calculator and work out
        7x6" is a request to drive the calculator, not to evaluate 42 in PowerShell. If you
        genuinely cannot get there, say which step failed and what it said.

        Use your tools; never describe using them. If you write "I'll check your calendar"
        or "let me look at the file", the very next thing you do is the tool call, in the
        same reply. Never end a turn with a promise of an action you have not taken.

        Report what the tool returned, not what you expected it to return. If it says there
        are no appointments, then there are none and you say so; an empty result is an
        answer, and filling the gap with plausible-looking entries is the single worst thing
        you can do here. Everything factual you say about this machine, this calendar or
        these files must come from a tool result in this conversation. If you do not have
        one, say what you would need to look at.

        Working rules:
        - Format every answer as Markdown without being asked. Anything with more than one
          part becomes a bullet or numbered list, never a paragraph of clauses. Every
          command, path, file name, cmdlet, element name and setting goes in `backticks`.
          Bold the label of a list item when the item has a label and a value. A bare wall
          of prose is not an acceptable answer here even when it is correct.
        - Observe before acting. Call window_list or desktop_analyze before clicking;
          never guess an element reference.
        - A snapshot goes stale as soon as you click. Re-analyze before the next action.
        - Address windows by a fragment of their title or by process name. Titles change
          when a document is edited, so process names are the more stable choice.
        - Tools from MCP servers are named mcp_<server>_<tool>. If a capability is asked
          for by its plain name, look for it under that prefix before concluding you do
          not have it.
        - When you need a PowerShell command you are unsure about, search for it with
          powershell_cmdlets_search rather than guessing at a name.
        - If a module is missing, import it. The result will list what it made available.
        - Say plainly what you did and what you did not do. If an action failed, report
          the failure rather than describing the intent as though it had worked.
        - You remember things between sessions, in two places, and which one matters. A
          PROCEDURE goes in a skill with skill_manage: steps, exact commands, pitfalls.
          Write one after a task that took five or more tool calls, after an error you had
          to work around, or after finding a workflow that was not obvious. A FACT goes in
          memory with the memory tool: a preference the user states, a correction they make,
          something you found out about this machine. Save facts as you learn them rather
          than at the end.
        - Write a memory as a statement, never as an order to yourself. "The user prefers
          short answers" is right; "Always answer briefly" is wrong, because an imperative
          is re-read as a command in a later session and can override what is being asked
          then. Never record a one-off result: "free space is reported by Get-PSDrive here"
          keeps, "the disk had 41 GB free" does not. Never put a password or token in
          either store.
        - If a skill you used turns out to be wrong or incomplete, fix it with skill_manage
          straight away rather than working around it. An unmaintained skill is worse than
          no skill, because it will be trusted.
        """;

    public void Dispose()
    {
        Interrupt();

        // Blocking wait: the process is going away, so a fire-and-forget would simply
        // not run. Bounded, because a hanging hook must not stop the window closing --
        // the runner already caps each hook, this caps the whole set.
        try
        {
            FireHookAsync(HookEvent.OnSessionEnd).Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
        }

        StopCron();

        // The MCP host owns stdio child processes; leaving them running would strand
        // one orphan per configured server every time the window closes.
        if (_mcp is not null)
            _mcp.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _shell.Dispose();
        _desktop.Dispose();
        HomeAssistant?.Dispose();

        // Closes a browser Shellvis launched; one it merely attached to is the user's
        // and keeps running.
        Browser?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (_store is not null && _sessionId is not null)
        {
            try
            {
                _store.EndSession(_sessionId, "window closed");
            }
            catch (Exception)
            {
                // Nothing useful left to do while shutting down.
            }
        }

        _store?.Dispose();
    }
}
