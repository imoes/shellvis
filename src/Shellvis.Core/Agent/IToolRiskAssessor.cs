using Shellvis.Core.Shell;
using Shellvis.Core.Tools;

namespace Shellvis.Core.Agent;

/// <summary>
/// Decides how risky a specific tool CALL is, as opposed to how risky the tool is in
/// general.
///
/// This exists because the static flag on a tool is necessarily pessimistic.
/// <c>powershell_run</c> has to be declared mutating, since it can do anything -- but
/// the overwhelming majority of actual calls are queries. Prompting for every
/// <c>Get-CimInstance</c> trains the user to click Allow without reading, which is
/// strictly worse than not asking: it destroys the value of the prompts that matter.
///
/// So the tool's flag is the ceiling, and an assessor may lower it for a specific set
/// of arguments when it can prove the call is safe. It may never raise... except to
/// <see cref="SideEffect.AlwaysAsk"/>, which is the one direction that is always
/// allowed, because escalating a dangerous call is never wrong.
/// </summary>
public interface IToolRiskAssessor
{
    SideEffect Assess(ToolEntry tool, IReadOnlyDictionary<string, object?> arguments);
}

/// <summary>Takes every tool at its declared word. The conservative default.</summary>
public sealed class StaticRiskAssessor : IToolRiskAssessor
{
    public static readonly StaticRiskAssessor Instance = new();

    public SideEffect Assess(ToolEntry tool, IReadOnlyDictionary<string, object?> arguments) =>
        tool.SideEffect;
}

/// <summary>
/// Lowers <c>powershell_run</c> to read-only when the script provably only reads, and
/// raises it to always-ask when it matches a dangerous pattern.
///
/// This is the conditional auto mode in practice. Everything else keeps its declared
/// risk, so adding a tool never accidentally opts into silent execution.
/// </summary>
public sealed class PowerShellRiskAssessor : IToolRiskAssessor
{
    /// <summary>Tools whose first argument is a script this assessor understands.</summary>
    private static readonly Dictionary<string, string> ScriptArguments = new(StringComparer.Ordinal)
    {
        ["powershell_run"] = "script",

        // The 5.1 fallback is the same language and gets the same reading. Left out, every
        // provable read against a legacy module would raise a prompt, which is how a user
        // learns to click Allow without looking -- the exact failure the classifier exists
        // to prevent.
        ["powershell_run_winps"] = "script",

        // A remote script is the same language and gets the same reading. What differs is
        // the consequence of getting it wrong, which is handled below rather than by
        // refusing to look at it: a provable read on a server should not raise a prompt any
        // more than a local one, or the prompts that matter stop being read.
        ["remote_run"] = "script",
    };

    /// <summary>
    /// Tools where a script that is NOT provably read-only is escalated rather than merely
    /// asked about.
    ///
    /// Running code on another machine is not the same act as running it here, and the
    /// difference is not what it does but where. The blast radius is a server somebody else
    /// depends on, an "always allow" answer given once for convenience would cover every
    /// future machine, and yolo mode is a statement about routine local work rather than a
    /// waiver on remote administration. So a remote write always asks -- the same treatment
    /// the privileged broker gets, for the same reason.
    /// </summary>
    private static readonly HashSet<string> EscalateWhenNotReadOnly = new(StringComparer.Ordinal)
    {
        "remote_run",
    };

    /// <summary>The most recent verdict, so the console can explain why a call ran silently.</summary>
    public ScriptVerdict? LastVerdict { get; private set; }

    public SideEffect Assess(ToolEntry tool, IReadOnlyDictionary<string, object?> arguments)
    {
        if (!ScriptArguments.TryGetValue(tool.Name, out string? parameter))
            return tool.SideEffect;

        if (!arguments.TryGetValue(parameter, out object? raw) || raw is null)
            return tool.SideEffect;

        string script = raw.ToString() ?? string.Empty;

        if (ReadOnlyClassifier.IsAlwaysDangerous(script, out string reason))
        {
            LastVerdict = new ScriptVerdict(false, reason);
            return SideEffect.AlwaysAsk;
        }

        ScriptVerdict verdict = ReadOnlyClassifier.Classify(script);
        LastVerdict = verdict;

        if (verdict.IsProvablyReadOnly)
            return SideEffect.ReadOnly;

        if (EscalateWhenNotReadOnly.Contains(tool.Name))
            return SideEffect.AlwaysAsk;

        // Otherwise the tool's own declaration stands. The assessor only ever lowers the
        // ceiling, or raises it to AlwaysAsk, which is the one direction that is always
        // safe.
        return tool.SideEffect;
    }
}
