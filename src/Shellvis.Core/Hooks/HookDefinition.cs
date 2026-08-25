using System.Text.RegularExpressions;

namespace Shellvis.Core.Hooks;

/// <summary>
/// The points at which an external program may be given a say.
///
/// The full set is declared, matching Hermes' protocol, so that a hook configuration
/// written for one is readable by the other. But not every one of them has a place to
/// fire from in this build -- there are no subagents yet, and the HTTP layer is inside
/// the provider SDK rather than in reach. Rather than accept such a configuration in
/// silence and let the user believe their hook is installed, the loader warns for events
/// this build never raises. See <see cref="HookCatalog.Fires"/>.
/// </summary>
public enum HookEvent
{
    /// <summary>Before a tool runs. May block it.</summary>
    PreToolCall,

    /// <summary>After a tool ran, with its output.</summary>
    PostToolCall,

    /// <summary>May rewrite a tool result before the model sees it.</summary>
    TransformToolResult,

    /// <summary>May rewrite shell output before it is shown or returned.</summary>
    TransformTerminalOutput,

    /// <summary>Before each model round trip.</summary>
    PreLlmCall,

    /// <summary>After each model round trip.</summary>
    PostLlmCall,

    /// <summary>Before the HTTP request to the provider.</summary>
    PreApiRequest,

    /// <summary>After the HTTP response from the provider.</summary>
    PostApiRequest,

    /// <summary>When a session opens.</summary>
    OnSessionStart,

    /// <summary>When a session closes.</summary>
    OnSessionEnd,

    /// <summary>When a session is finalised, after the last turn.</summary>
    OnSessionFinalize,

    /// <summary>When the user starts a fresh conversation.</summary>
    OnSessionReset,

    /// <summary>When a delegated subagent finishes.</summary>
    SubagentStop,
}

/// <summary>Names and capabilities of the hook events.</summary>
public static class HookCatalog
{
    /// <summary>
    /// The wire name of an event: lower snake_case, as it appears in config and in the
    /// JSON handed to the hook.
    /// </summary>
    public static string NameOf(HookEvent value) => value switch
    {
        HookEvent.PreToolCall => "pre_tool_call",
        HookEvent.PostToolCall => "post_tool_call",
        HookEvent.TransformToolResult => "transform_tool_result",
        HookEvent.TransformTerminalOutput => "transform_terminal_output",
        HookEvent.PreLlmCall => "pre_llm_call",
        HookEvent.PostLlmCall => "post_llm_call",
        HookEvent.PreApiRequest => "pre_api_request",
        HookEvent.PostApiRequest => "post_api_request",
        HookEvent.OnSessionStart => "on_session_start",
        HookEvent.OnSessionEnd => "on_session_end",
        HookEvent.OnSessionFinalize => "on_session_finalize",
        HookEvent.OnSessionReset => "on_session_reset",
        HookEvent.SubagentStop => "subagent_stop",
        _ => value.ToString(),
    };

    /// <summary>Parse a wire name, or null when it is not an event.</summary>
    public static HookEvent? Parse(string name)
    {
        foreach (HookEvent value in Enum.GetValues<HookEvent>())
        {
            if (NameOf(value).Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    /// <summary>
    /// Whether this build actually raises the event.
    ///
    /// Kept as data next to the enum rather than as a comment, because it is the thing
    /// the config loader has to tell the user about. A hook that can never fire is worse
    /// than an unsupported one: it looks installed.
    /// </summary>
    public static bool Fires(HookEvent value) => value switch
    {
        HookEvent.PreToolCall => true,
        HookEvent.PostToolCall => true,
        HookEvent.TransformToolResult => true,
        HookEvent.PreLlmCall => true,
        HookEvent.PostLlmCall => true,
        HookEvent.OnSessionStart => true,
        HookEvent.OnSessionEnd => true,
        HookEvent.OnSessionReset => true,

        // Shell output already passes through the tool result, so the terminal-specific
        // transform has no separate site yet.
        HookEvent.TransformTerminalOutput => false,

        // The HTTP exchange happens inside the provider SDK; reaching it means a
        // pipeline policy, which is a separate piece of work.
        HookEvent.PreApiRequest => false,
        HookEvent.PostApiRequest => false,

        // No subagents, and no finalisation distinct from session end.
        HookEvent.OnSessionFinalize => false,
        HookEvent.SubagentStop => false,

        _ => false,
    };

    public static IReadOnlyList<string> AllNames =>
        [.. Enum.GetValues<HookEvent>().Select(NameOf)];
}

/// <summary>
/// One configured hook.
/// </summary>
/// <param name="Event">Which point it attaches to.</param>
/// <param name="Command">
/// The command line to run. Executed through the shell so that a hook can be a one-liner
/// rather than requiring a script file.
/// </param>
/// <param name="Matcher">
/// Regex on the tool name, for the tool events. Null matches everything. A hook that
/// fires on every tool is rarely what someone means and is the easy way to make an
/// agent slow, so narrowing is cheap and worth encouraging.
/// </param>
/// <param name="TimeoutSeconds">
/// How long the hook may take. A hook is synchronous in the turn -- the agent waits --
/// so an unbounded one is an agent that hangs.
/// </param>
public sealed record HookDefinition(
    HookEvent Event,
    string Command,
    Regex? Matcher = null,
    int TimeoutSeconds = 60)
{
    /// <summary>The ceiling on a hook timeout, whatever the config asks for.</summary>
    public const int MaxTimeoutSeconds = 300;

    /// <summary>Whether this hook applies to a given tool name.</summary>
    public bool Matches(string? toolName)
    {
        if (Matcher is null)
            return true;

        return toolName is not null && Matcher.IsMatch(toolName);
    }

    /// <summary>
    /// The identity used for consent, so that approving a hook approves THAT hook.
    ///
    /// Event and command together: the same script attached to a different event is a
    /// different thing to agree to, because the data it receives and the power it has
    /// differ -- a pre_tool_call hook can block actions, a post one cannot.
    /// </summary>
    public string ConsentKey => $"{HookCatalog.NameOf(Event)}::{Command.Trim()}";
}
