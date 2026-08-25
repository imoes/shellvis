using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shellvis.Core.Hooks;

/// <summary>What a hook asked for.</summary>
/// <param name="Blocked">The action must not proceed.</param>
/// <param name="Reason">Why it was blocked, to be told to the model.</param>
/// <param name="Context">Extra text for the model to see.</param>
/// <param name="Replacement">A rewritten payload, for the transform events.</param>
public sealed record HookOutcome(
    bool Blocked = false,
    string? Reason = null,
    string? Context = null,
    string? Replacement = null)
{
    public static readonly HookOutcome None = new();

    /// <summary>Whether the hook had anything to say at all.</summary>
    public bool IsEmpty => !Blocked && Reason is null && Context is null && Replacement is null;
}

/// <summary>
/// Asked before a hook is allowed to run for the first time.
///
/// A hook is an arbitrary command line that runs with the user's rights on every tool
/// call. That is a large amount of power to grant by editing a text file, so the grant
/// is confirmed once, interactively, and remembered. Deliberately an interface rather
/// than a callback into the UI, so the console probes can answer it without a window.
/// </summary>
public interface IHookConsent
{
    /// <summary>Whether this hook may run. Called at most once per hook per process.</summary>
    Task<bool> AllowAsync(HookDefinition hook, CancellationToken cancellationToken);
}

/// <summary>Grants every hook. For probes and for a deliberately unattended mode.</summary>
public sealed class AllowAllHooks : IHookConsent
{
    public static readonly AllowAllHooks Instance = new();

    public Task<bool> AllowAsync(HookDefinition hook, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}

/// <summary>
/// Runs configured hooks.
///
/// The protocol is deliberately the simplest thing that can work, and the same one
/// Hermes uses: JSON in on stdin, optional JSON out on stdout. Anything the hook writes
/// that is not JSON is a no-op rather than an error -- that matters because the most
/// common hook is a one-line script that logs something and says nothing, and it would
/// be hostile for `echo done` to break a turn.
/// </summary>
public sealed class HookRunner(
    IReadOnlyList<HookDefinition> hooks,
    IHookConsent? consent = null,
    string? sessionId = null)
{
    private readonly IReadOnlyList<HookDefinition> _hooks = hooks;
    private readonly IHookConsent _consent = consent ?? AllowAllHooks.Instance;

    /// <summary>
    /// Consent answers for this process, so a hook is asked about once and not once per
    /// tool call. Denials are cached too: being asked again after saying no is worse
    /// than the original question.
    /// </summary>
    private readonly Dictionary<string, bool> _granted = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _consentGate = new(1, 1);

    /// <summary>Diagnostics worth surfacing: a hook that failed, timed out or was denied.</summary>
    private readonly List<string> _notes = [];

    public string? SessionId { get; set; } = sessionId;

    /// <summary>Whether any hook at all is configured, so the caller can skip the work.</summary>
    public bool HasAny => _hooks.Count > 0;

    /// <summary>Whether any hook is attached to this event.</summary>
    public bool Has(HookEvent value) => _hooks.Any(h => h.Event == value);

    /// <summary>Take and clear the accumulated notes.</summary>
    public IReadOnlyList<string> DrainNotes()
    {
        lock (_notes)
        {
            List<string> copy = [.. _notes];
            _notes.Clear();
            return copy;
        }
    }

    private void Note(string text)
    {
        lock (_notes)
        {
            if (_notes.Count < 50)
                _notes.Add(text);
        }
    }

    /// <summary>
    /// Fire every hook attached to an event and combine what they said.
    /// </summary>
    /// <param name="value">Which event.</param>
    /// <param name="toolName">Tool name, for matcher filtering and for the payload.</param>
    /// <param name="payload">Event-specific fields added to the JSON handed to the hook.</param>
    public async Task<HookOutcome> FireAsync(
        HookEvent value,
        string? toolName = null,
        JsonObject? payload = null,
        CancellationToken cancellationToken = default)
    {
        if (_hooks.Count == 0)
            return HookOutcome.None;

        var applicable = _hooks
            .Where(h => h.Event == value && h.Matches(toolName))
            .ToList();

        if (applicable.Count == 0)
            return HookOutcome.None;

        var contexts = new List<string>();
        string? replacement = null;

        foreach (HookDefinition hook in applicable)
        {
            if (!await IsAllowedAsync(hook, cancellationToken).ConfigureAwait(false))
                continue;

            HookOutcome outcome = await RunOneAsync(
                hook, value, toolName, payload, replacement, cancellationToken).ConfigureAwait(false);

            // A block short-circuits the rest. Running further hooks after one has
            // vetoed the action would let a later hook's context describe something
            // that is not going to happen.
            if (outcome.Blocked)
            {
                return outcome with
                {
                    Context = contexts.Count > 0 ? string.Join("\n", contexts) : outcome.Context,
                };
            }

            if (outcome.Context is { Length: > 0 } context)
                contexts.Add(context);

            // Transforms chain: the next hook sees the previous one's output, so two
            // filters compose instead of the last one winning.
            if (outcome.Replacement is not null)
                replacement = outcome.Replacement;
        }

        if (contexts.Count == 0 && replacement is null)
            return HookOutcome.None;

        return new HookOutcome(
            Context: contexts.Count > 0 ? string.Join("\n", contexts) : null,
            Replacement: replacement);
    }

    private async Task<bool> IsAllowedAsync(HookDefinition hook, CancellationToken cancellationToken)
    {
        string key = hook.ConsentKey;

        await _consentGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_granted.TryGetValue(key, out bool known))
                return known;

            bool allowed = await _consent.AllowAsync(hook, cancellationToken).ConfigureAwait(false);
            _granted[key] = allowed;

            if (!allowed)
                Note($"hook declined and will not run this session: {hook.Command}");

            return allowed;
        }
        finally
        {
            _consentGate.Release();
        }
    }

    private async Task<HookOutcome> RunOneAsync(
        HookDefinition hook,
        HookEvent value,
        string? toolName,
        JsonObject? payload,
        string? chained,
        CancellationToken cancellationToken)
    {
        var input = new JsonObject
        {
            ["hook_event_name"] = HookCatalog.NameOf(value),
            ["session_id"] = SessionId,
            ["cwd"] = Environment.CurrentDirectory,
        };

        if (toolName is not null)
            input["tool_name"] = toolName;

        if (payload is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in payload)
                input[pair.Key] = pair.Value?.DeepClone();
        }

        // A chained transform must see what the previous hook produced, not the original.
        if (chained is not null)
            input["tool_result"] = chained;

        int timeout = Math.Clamp(hook.TimeoutSeconds, 1, HookDefinition.MaxTimeoutSeconds);

        try
        {
            (string stdout, string stderr, int exit, bool timedOut) = await ExecuteAsync(
                hook.Command, input.ToJsonString(), timeout, cancellationToken).ConfigureAwait(false);

            if (timedOut)
            {
                // A timed-out hook is NOT treated as a block. Blocking on timeout would
                // mean a slow or broken hook silently disables the agent's tools, which
                // is a worse failure than the hook not running.
                Note($"hook timed out after {timeout}s and was ignored: {hook.Command}");
                return HookOutcome.None;
            }

            if (exit != 0 && stdout.Trim().Length == 0)
            {
                string detail = stderr.Trim();

                if (detail.Length > 300)
                    detail = detail[..300] + " ...";

                Note($"hook exited {exit}: {hook.Command}"
                    + (detail.Length > 0 ? $" -- {detail}" : string.Empty));

                return HookOutcome.None;
            }

            return Interpret(stdout, hook);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A hook that cannot even be started must not take the turn down with it.
            Note($"hook could not run: {hook.Command} -- {ex.Message}");
            return HookOutcome.None;
        }
    }

    /// <summary>
    /// Read what the hook wrote.
    ///
    /// Non-JSON output is silently accepted as "nothing to say". That is a deliberate
    /// choice about who the protocol serves: the common case is a two-line script that
    /// appends to a log, and treating its `echo` as a protocol violation would make
    /// hooks something only careful programs can use.
    /// </summary>
    private HookOutcome Interpret(string stdout, HookDefinition hook)
    {
        string text = stdout.Trim();

        if (text.Length == 0 || text[0] is not ('{' or '['))
            return HookOutcome.None;

        JsonNode? node;

        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            // Output that STARTS like JSON but is malformed is worth a note: it means
            // the hook meant to say something and failed, unlike plain log output.
            Note($"hook wrote output that looks like JSON but does not parse: {hook.Command}");
            return HookOutcome.None;
        }

        if (node is not JsonObject obj)
            return HookOutcome.None;

        string? decision = obj["decision"]?.GetValue<string>();
        string? reason = obj["reason"]?.GetValue<string>();
        string? context = obj["context"]?.GetValue<string>();

        // Both spellings, because a transform hook naturally reaches for the name of
        // what it is transforming.
        string? replacement = obj["replacement"]?.GetValue<string>()
            ?? obj["tool_result"]?.GetValue<string>();

        bool blocked = decision is not null
            && decision.Equals("block", StringComparison.OrdinalIgnoreCase);

        if (blocked && string.IsNullOrWhiteSpace(reason))
            reason = $"blocked by a hook ({hook.Command})";

        return new HookOutcome(blocked, reason, context, replacement);
    }

    /// <summary>
    /// Run the command with the JSON on its stdin.
    ///
    /// Through cmd.exe so that a hook can be a plain command line with pipes and
    /// redirection, the way it is written in the config file. PowerShell would be the
    /// richer host but costs a runspace start per invocation, and a pre_tool_call hook
    /// runs on every single tool call.
    /// </summary>
    private static async Task<(string Stdout, string Stderr, int Exit, bool TimedOut)> ExecuteAsync(
        string command, string json, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,

            // Arguments, NOT ArgumentList. ArgumentList applies .NET's argument-quoting
            // rules, which are not cmd's: a command containing quotes -- a script path
            // with a space in it, so the ordinary case -- came out as ""C:\...\x.cmd""
            // and cmd reported it as not found. /s tells cmd to strip exactly the first
            // and last quote and take the rest verbatim, which is the documented way to
            // hand it a command line built elsewhere.
            Arguments = "/s /c \"" + command + "\"",
        };

        using var process = new Process { StartInfo = startInfo };

        process.Start();

        // Both streams are read concurrently with the write. Reading them in sequence
        // deadlocks as soon as one pipe's buffer fills, which for a chatty hook happens
        // at a few kilobytes.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.StandardInput.WriteAsync(json.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A hook that ignores its stdin and exits immediately closes the pipe. That
            // is legitimate -- an on_session_start logger does not read anything.
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // The whole tree: a hook that starts a child would otherwise leave it
                // running after the hook itself is abandoned.
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            return (string.Empty, string.Empty, -1, true);
        }

        return (
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false),
            process.ExitCode,
            false);
    }
}
