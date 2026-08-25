using Shellvis.Core.Tools;

namespace Shellvis.Core.Agent;

/// <summary>How a permission request was answered.</summary>
public enum ApprovalDecision
{
    /// <summary>Run it this once.</summary>
    Once,

    /// <summary>Run it, and stop asking for this tool for the rest of the session.</summary>
    Session,

    /// <summary>Run it, and remember the allowance permanently.</summary>
    Always,

    /// <summary>Do not run it.</summary>
    Deny,
}

/// <summary>One pending permission request.</summary>
/// <param name="Tool">The tool wanting to run.</param>
/// <param name="Preview">One-line description of the call, as the console shows it.</param>
/// <param name="Arguments">Full arguments, for the expandable detail view.</param>
/// <param name="Reason">Why approval is needed at all.</param>
public sealed record ApprovalRequest(
    ToolEntry Tool,
    string Preview,
    string Arguments,
    string Reason);

/// <summary>
/// Decides whether a tool call may proceed.
///
/// An interface rather than a callback so the agent loop stays testable: tests use
/// <see cref="AutoApprove"/> or <see cref="DenyAll"/>, while the real shell puts a
/// modal over the pill. It is also the seam where the conditional auto mode lives, so
/// the loop itself never has to know about permission modes.
/// </summary>
public interface IApprovalGate
{
    /// <summary>
    /// Ask for permission. Implementations must honour cancellation: a user who
    /// interrupts while a prompt is open should not be left waiting on it.
    /// </summary>
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Approves everything. For tests and for the yolo mode.
///
/// Note that this deliberately does NOT bypass <see cref="SideEffect.AlwaysAsk"/> --
/// that decision belongs to the gate that consults the user, not to a blanket
/// approver. Wiring yolo mode to this type is therefore a conscious choice to accept
/// unattended installs, and the real shell gate must not be built this way.
/// </summary>
public sealed class AutoApprove : IApprovalGate
{
    public static readonly AutoApprove Instance = new();

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Once);
}

/// <summary>Refuses everything. Useful for a dry run that shows what the model wanted to do.</summary>
public sealed class DenyAll : IApprovalGate
{
    public static readonly DenyAll Instance = new();

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Deny);
}
