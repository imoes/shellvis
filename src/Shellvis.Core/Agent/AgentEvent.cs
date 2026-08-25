namespace Shellvis.Core.Agent;

/// <summary>Why a turn stopped.</summary>
public enum TurnEndReason
{
    /// <summary>The model answered with text and asked for nothing more. The normal ending.</summary>
    Answered,

    /// <summary>The user interrupted.</summary>
    Interrupted,

    /// <summary>The iteration budget ran out before the model was finished.</summary>
    BudgetExhausted,

    /// <summary>A tool approval was refused and the model had nothing else to offer.</summary>
    Refused,

    /// <summary>The provider failed in a way retrying would not fix.</summary>
    Failed,
}

/// <summary>
/// Something the agent did, as the console needs to render it.
///
/// Modelled as an event stream rather than a return value because the interesting part
/// of an agent turn is what happens DURING it. A method that returns only the final
/// answer would leave the console blank for thirty seconds while shell commands and
/// tool calls scroll past invisibly, which is exactly the opacity this project exists
/// to remove.
///
/// The shape follows Hermes' TUI gateway event surface, which is the most thoroughly
/// exercised design available for this: message deltas, tool lifecycle in three
/// stages, and explicit approval requests.
/// </summary>
public abstract record AgentEvent
{
    /// <summary>The model produced a fragment of its answer.</summary>
    public sealed record AssistantDelta(string Text) : AgentEvent;

    /// <summary>The model finished a complete message.</summary>
    public sealed record AssistantMessage(string Text) : AgentEvent;

    /// <summary>The model is thinking out loud, where the provider exposes that separately.</summary>
    public sealed record ReasoningDelta(string Text) : AgentEvent;

    /// <summary>A tool call is about to run. <paramref name="Preview"/> is the console one-liner.</summary>
    public sealed record ToolStarted(string CallId, string Tool, string Preview) : AgentEvent;

    /// <summary>A tool call finished.</summary>
    public sealed record ToolCompleted(
        string CallId,
        string Tool,
        string Result,
        TimeSpan Duration,
        bool Succeeded) : AgentEvent;

    /// <summary>A tool call was not run because approval was refused.</summary>
    public sealed record ToolRefused(string CallId, string Tool, string Reason) : AgentEvent;

    /// <summary>
    /// The history was summarised because it grew too long. The store reacts by
    /// rotating the session, so the verbatim history is preserved rather than lost.
    /// </summary>
    public sealed record Compacted(string Detail, string? Summary) : AgentEvent;

    /// <summary>
    /// Shellvis saying something about itself rather than answering: a skill it wrote down,
    /// a mode that changed. Separate from <see cref="Failure"/> because it is not a problem,
    /// and separate from <see cref="AssistantMessage"/> because it is not the answer.
    /// </summary>
    public sealed record Announcement(string Text) : AgentEvent;

    /// <summary>Progress marker so the console can show which round of the loop is running.</summary>
    public sealed record IterationStarted(int Iteration, int Budget) : AgentEvent;

    /// <summary>The turn is over.</summary>
    public sealed record TurnFinished(
        TurnEndReason Reason,
        int Iterations,
        string? FinalText) : AgentEvent;

    /// <summary>Something went wrong, described for a human rather than a stack trace.</summary>
    public sealed record Failure(string Message) : AgentEvent;
}
