using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Shellvis.Core.Config;

/// <summary>Where Shellvis keeps its state on disk.</summary>
public static class ShellvisPaths
{
    /// <summary>
    /// The home directory. A dotted folder under the user profile, matching the
    /// convention every comparable tool uses, so it is where someone would look.
    /// </summary>
    public static string Home => Environment.GetEnvironmentVariable("SHELLVIS_HOME")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".shellvis");

    public static string ConfigFile => Path.Combine(Home, "config.yaml");

    public static string SkillsDirectory => Path.Combine(Home, "skills");

    public static string SessionsDirectory => Path.Combine(Home, "sessions");

    public static string LogsDirectory => Path.Combine(Home, "logs");

    /// <summary>
    /// Modules installed from the PowerShell Gallery.
    ///
    /// Under LOCALAPPDATA rather than the home directory on purpose: the user's own
    /// PowerShell module path is redirected into OneDrive on this machine, and
    /// installing there means sync conflicts and locked files.
    /// </summary>
    public static string ModulesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shellvis", "Modules");

    /// <summary>Create the directories that must exist before anything runs.</summary>
    public static void EnsureCreated()
    {
        foreach (string path in new[] { Home, SkillsDirectory, SessionsDirectory, LogsDirectory })
            Directory.CreateDirectory(path);
    }
}

/// <summary>Result of loading a configuration, including anything that looked wrong.</summary>
/// <param name="Config">The loaded configuration, or defaults when the file was unusable.</param>
/// <param name="Path">Where it came from.</param>
/// <param name="Created">Whether a default file was just written.</param>
/// <param name="Warnings">Problems worth telling the user about, none of them fatal.</param>
public sealed record ConfigLoadResult(
    ShellvisConfig Config,
    string Path,
    bool Created,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads and writes config.yaml.
///
/// Two behaviours are load-bearing and easy to get wrong.
///
/// Environment variables are interpolated on READ, so a config can reference
/// ${OPENROUTER_API_KEY} without the key living in the file. But the literal
/// ${VAR} text is preserved on WRITE: an "always allow" answer or a model switch
/// rewrites the file, and a naive round trip would bake the resolved secret into it.
/// Hermes handles this the same way, and for the same reason.
///
/// A broken config never stops the agent from starting. It falls back to defaults and
/// reports what was wrong, because a syntax error in a YAML file should not leave
/// someone with an application that refuses to open and no explanation.
/// </summary>
public static class ConfigStore
{
    /// <summary>Matches ${NAME} references.</summary>
    private static readonly Regex Reference = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <summary>Load the configuration, creating a commented default file if none exists.</summary>
    public static ConfigLoadResult Load(string? path = null)
    {
        string file = path ?? ShellvisPaths.ConfigFile;
        var warnings = new List<string>();

        if (!File.Exists(file))
        {
            try
            {
                ShellvisPaths.EnsureCreated();
                File.WriteAllText(file, DefaultYaml, Encoding.UTF8);
                return new ConfigLoadResult(new ShellvisConfig(), file, true, warnings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"could not create {file}: {ex.Message}; using defaults");
                return new ConfigLoadResult(new ShellvisConfig(), file, false, warnings);
            }
        }

        string yaml;
        try
        {
            yaml = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"could not read {file}: {ex.Message}; using defaults");
            return new ConfigLoadResult(new ShellvisConfig(), file, false, warnings);
        }

        // Interpolation happens on the raw text before deserialization, so a reference
        // works in any string value without the model needing to know where.
        string expanded = Expand(yaml, warnings);

        try
        {
            ShellvisConfig? config = BuildDeserializer().Deserialize<ShellvisConfig>(expanded);

            if (config is null)
            {
                warnings.Add($"{file} is empty; using defaults");
                return new ConfigLoadResult(new ShellvisConfig(), file, false, warnings);
            }

            Validate(config, warnings);
            return new ConfigLoadResult(config, file, false, warnings);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            // Line and column matter here: a config error the user cannot locate is
            // barely more useful than no message at all.
            warnings.Add(
                $"{file} could not be parsed at line {ex.Start.Line}, column {ex.Start.Column}: "
                + $"{ex.Message}; using defaults");

            return new ConfigLoadResult(new ShellvisConfig(), file, false, warnings);
        }
    }

    /// <summary>
    /// Write the configuration back, preserving ${VAR} references.
    ///
    /// The preservation is the entire difficulty. Serializing the in-memory object
    /// would write the RESOLVED value, so the first programmatic change would copy every
    /// referenced secret into a plaintext file. So the original text is read, the
    /// resolved values are mapped back to their references, and only then is the file
    /// replaced.
    /// </summary>
    public static void Save(ShellvisConfig config, string? path = null)
    {
        string file = path ?? ShellvisPaths.ConfigFile;
        ShellvisPaths.EnsureCreated();

        string yaml = BuildSerializer().Serialize(config);
        yaml = RestoreReferences(yaml, file);

        // Write via a temporary file and move into place: a crash midway through must
        // not leave a truncated config that fails to parse on the next start.
        string temporary = file + ".tmp";
        File.WriteAllText(temporary, yaml, Encoding.UTF8);
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>Replace ${VAR} with the environment value, warning about anything unset.</summary>
    private static string Expand(string yaml, List<string> warnings)
    {
        return Reference.Replace(yaml, match =>
        {
            string name = match.Groups[1].Value;
            string? value = Environment.GetEnvironmentVariable(name);

            if (value is not null)
                return value;

            // Left as-is rather than blanked: an empty string would silently become a
            // valid-looking but wrong setting, while the literal ${NAME} shows up in
            // any error the value causes later.
            warnings.Add($"{name} is referenced in the config but not set in the environment");
            return match.Value;
        });
    }

    /// <summary>
    /// Put ${VAR} references back where the previous file had them.
    ///
    /// Works by reading the references out of the file on disk and swapping the
    /// resolved value for the reference in the freshly serialized text. Only exact
    /// value matches are replaced, so a coincidentally identical string elsewhere is
    /// left alone.
    /// </summary>
    private static string RestoreReferences(string yaml, string existingFile)
    {
        if (!File.Exists(existingFile))
            return yaml;

        string previous;
        try
        {
            previous = File.ReadAllText(existingFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return yaml;
        }

        foreach (Match match in Reference.Matches(previous))
        {
            string name = match.Groups[1].Value;
            string? value = Environment.GetEnvironmentVariable(name);

            // An empty value would match everywhere; skip rather than corrupt the file.
            if (string.IsNullOrEmpty(value))
                continue;

            yaml = yaml.Replace(value, match.Value, StringComparison.Ordinal);
        }

        return yaml;
    }

    private static void Validate(ShellvisConfig config, List<string> warnings)
    {
        if (config.ConfigVersion > 1)
        {
            warnings.Add(
                $"the config declares version {config.ConfigVersion}, which is newer than this "
                + "build understands; unknown settings will be ignored");
        }

        string[] modes = ["ask", "auto-read", "smart", "yolo"];
        if (!modes.Contains(config.Approvals.Mode, StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add(
                $"approvals.mode '{config.Approvals.Mode}' is not recognised; "
                + "falling back to auto-read");

            config.Approvals.Mode = "auto-read";
        }

        // A zero or negative ceiling would let a turn run forever, which is the one
        // failure mode this setting exists to prevent.
        if (config.Agent.MaxIterations < 1)
        {
            warnings.Add("agent.maxIterations must be at least 1; using 20");
            config.Agent.MaxIterations = 20;
        }

        foreach ((string name, McpServerSection server) in config.McpServers)
        {
            if (server.Transport.Equals("stdio", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(server.Command))
            {
                warnings.Add($"mcpServers.{name} uses stdio but has no command");
            }

            if (server.Transport.Equals("http", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(server.Url))
            {
                warnings.Add($"mcpServers.{name} uses http but has no url");
            }
        }
    }

    private static IDeserializer BuildDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            // Unknown keys are ignored rather than fatal: a config written by a newer
            // build, or one carrying a typo, should still load the parts that are valid.
            .IgnoreUnmatchedProperties()
            .Build();

    private static ISerializer BuildSerializer() =>
        new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

    /// <summary>
    /// The starter file.
    ///
    /// Written with comments and with the interesting settings present but commented
    /// out. A default config that is merely valid teaches nothing; one that shows what
    /// is possible is how someone discovers the MCP section exists at all.
    /// </summary>
    private const string DefaultYaml = """
        # Shellvis configuration.
        #
        # Windows paths: use 'single quotes' or no quotes at all. In DOUBLE quotes YAML
        # treats a backslash as an escape, so "C:\Users\me" is a parse error (\U) and
        # the whole file falls back to defaults with a warning.
        #
        # Values may reference environment variables as ${NAME}. Those references are
        # preserved when Shellvis rewrites this file, so secrets stay out of it.

        configVersion: 1

        model:
          # A catalog id, or "custom" with a baseUrl. Aliases work too (glm, grok,
          # claude, google, gpt, local).
          #   local     laguna, ollama, lmstudio, llamacpp
          #   hosted    openai, codex, anthropic, gemini, xai, groq, deepseek,
          #             mistral, kimi, zai, together, fireworks, cerebras, nvidia
          #   aggregate openrouter
          # Each needs its own key variable in the environment -- see the probe output
          # of "probe providers", or the error message when one is missing.
          provider: laguna
          # model: laguna
          # baseUrl: https://your-host/llama/v1
          # apiKeyEnvVar: OPENROUTER_API_KEY

        agent:
          # Model round trips one user turn may consume.
          # A task driven by clicking needs headroom: a click may cost a round and a
          # re-read another, and twelve ran out mid-calculation.
          maxIterations: 30
          # extraInstructions: |
          #   Prefer PowerShell over cmd. Always show me the command before running it.

        approvals:
          # ask       every write asks
          # auto-read provably read-only commands run silently, everything else asks
          # smart     a cheaper model adjudicates the doubtful cases
          # yolo      nothing asks, except installs and destructive patterns
          mode: auto-read
          timeoutSeconds: 300
          commandAllowlist: []

        # MCP servers. Their tools appear as mcp_<name>_<tool>.
        #
        # trustReadOnly is the ONLY way a remote tool can run without asking, and it
        # lives here rather than being something the server can claim for itself.
        mcpServers: {}
        #  filesystem:
        #    transport: stdio
        #    command: npx
        #    args: ["-y", "@modelcontextprotocol/server-filesystem", "D:\\Dev"]
        #    trustReadOnly: ["read_file", "list_directory"]
        #
        #  internal:
        #    transport: http
        #    url: https://mcp.example.internal/v1
        #    headers:
        #      Authorization: Bearer ${INTERNAL_MCP_TOKEN}

        # Extra directories to search for skills.
        skillDirectories: []

        # Home Assistant. The tools are only offered once a token is present, so
        # leaving this out simply means the house is not part of Shellvis' reach.
        # The token belongs in the HASS_TOKEN environment variable, not in this file.
        homeAssistant:
          # baseUrl: http://homeassistant.local:8123
          tokenEnvVar: HASS_TOKEN

        # Browser automation. Shellvis drives Chrome or Edge over the DevTools
        # protocol, using its own browser profile -- Chrome and Edge refuse remote
        # debugging on the default profile since version 136.
        browser:
          # Hosts the browser may never be pointed at. Matched by suffix, so
          # example.com also blocks ads.example.com.
          blocklist: []
          # Off by default: a url can arrive from a web page or a tool description,
          # and this is what stops such a url aiming the browser at your own network.
          allowPrivateUrls: false
          debugPort: 9222

        # Dictation. Recognition is local; nothing is sent anywhere.
        #
        # deviceIndex: -1 uses the Windows default recording device. Set it explicitly
        # when the machine has several microphones and the default is not the one you
        # speak into -- that is the usual reason dictation hears nothing. The transcript
        # lists the devices with their indices the first time you use the microphone.
        voice:
          deviceIndex: -1
          # language: de-DE     # empty follows the Windows display language
          #
          # Which recogniser dictation uses. Whisper runs locally and is markedly better
          # than the recogniser built into Windows, whose German dictation turned "Welche
          # Termine liegen diese Woche an" into "Dänische Termine legen diese Woche an".
          # The model is downloaded once, on first use, to
          # %LOCALAPPDATA%\Shellvis\Models -- nothing is ever sent anywhere.
          #
          #   engine: auto        # auto = whisper when its model is here, else Windows
          #                       # whisper = insist on whisper; sapi = the Windows engine
          #   whisperModel: small # tiny 74 MB | base 141 MB | small 465 MB | medium 1.5 GB
          #
          # Unset, whisperModel follows what was chosen during setup. Set it here to
          # override that.

        # Hooks: external commands given a say at points in the turn. Each is confirmed
        # once, interactively, before it ever runs -- a hook is an arbitrary command
        # line with your rights, and pre_tool_call hooks can veto what the agent does.
        #
        # Protocol: the hook gets JSON on stdin and may answer with JSON on stdout.
        #   {"decision": "block", "reason": "..."}   refuse the action
        #   {"context": "..."}                       add a note the model will see
        #   {"replacement": "..."}                   rewrite a tool result
        # Anything else it prints is ignored, so a logging one-liner is fine.
        #
        # Events raised by this build: pre_tool_call, post_tool_call,
        # transform_tool_result, pre_llm_call, post_llm_call, on_session_start,
        # on_session_end, on_session_reset. The rest of the protocol is accepted and
        # warned about, because a hook that can never fire looks installed.
        hooks: {}
        # hooks:
        #   pre_tool_call:
        #     - command: 'powershell -File C:\tools\audit.ps1'
        #       matcher: '^(powershell_run|psgallery_install)$'
        #       timeoutSeconds: 15

        """;
}
