using System.Text;
using Microsoft.Extensions.AI;

namespace Shellvis.Core.Sessions;

/// <summary>Settings for context compaction.</summary>
/// <param name="ContextTokens">
/// The model's context window. Only an estimate is needed: the threshold is a fraction
/// of it, so being 20% out changes when compaction happens, not whether it works.
/// </param>
/// <param name="Threshold">
/// Fraction of the window at which to compact. Well below 1 on purpose: compaction
/// itself needs room for the summarising call, and a turn that discovers it is over
/// budget mid-flight has no way to recover.
/// </param>
/// <param name="ProtectRecent">
/// Messages at the end that are never summarised. The current line of work has to
/// survive verbatim, or the model loses the thread it was in the middle of.
/// </param>
public sealed record CompactionOptions(
    int ContextTokens = 32_000,
    double Threshold = 0.6,
    int ProtectRecent = 12);

/// <summary>What a compaction did.</summary>
/// <param name="Compacted">Whether anything was replaced.</param>
/// <param name="Summary">The summary that replaced the older messages.</param>
/// <param name="MessagesBefore">History length before.</param>
/// <param name="MessagesAfter">History length after.</param>
/// <param name="Detail">Human-readable outcome.</param>
public sealed record CompactionResult(
    bool Compacted,
    string? Summary,
    int MessagesBefore,
    int MessagesAfter,
    string Detail);

/// <summary>
/// Decides when a conversation is too long and replaces its older half with a summary.
///
/// The load-bearing part is not the summarising, it is where the cut is allowed to
/// fall. A provider rejects a request outright when an assistant message announces a
/// tool call whose result is missing, or when a tool result appears with no call to
/// belong to. Hermes' own notes name orphaned tool_call_ids as the single most common
/// cause of a 400, and every naive "keep the last N messages" implementation produces
/// them the first time a turn ends mid-tool-call. So the cut point is moved until the
/// history is intact, and the intactness is checked rather than assumed.
///
/// Compaction is also lossy by definition, which is why the caller rotates the stored
/// session instead of overwriting it: the summary lives in the new session, the
/// verbatim history stays in the old one.
/// </summary>
public sealed class ContextCompactor(IChatClient summariser, CompactionOptions? options = null)
{
    private readonly CompactionOptions _options = options ?? new CompactionOptions();

    /// <summary>Rough token estimate. Four characters per token is close enough for a threshold.</summary>
    public static int EstimateTokens(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => (m.Text?.Length ?? 0) / 4) + (messages.Count() * 4);

    /// <summary>Whether the history has grown past the point where it should be compacted.</summary>
    public bool ShouldCompact(IReadOnlyList<ChatMessage> history) =>
        EstimateTokens(history) > _options.ContextTokens * _options.Threshold;

    /// <summary>
    /// Compact a history in place, returning what happened.
    ///
    /// The list is mutated rather than copied because the agent loop holds the same
    /// reference; handing back a new list would silently leave the loop on the old one.
    /// </summary>
    public async Task<CompactionResult> CompactAsync(
        List<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        int before = history.Count;

        if (!ShouldCompact(history))
            return new CompactionResult(false, null, before, before, "below the threshold");

        // The system message is never summarised: it carries the persona, the working
        // rules and the skill index, and losing it changes how the agent behaves.
        int head = history.Count > 0 && history[0].Role == ChatRole.System ? 1 : 0;

        int cut = FindCutPoint(history, head);

        if (cut <= head)
        {
            return new CompactionResult(
                false, null, before, before,
                "no safe cut point: the recent messages already fill the budget");
        }

        List<ChatMessage> toSummarise = history[head..cut];

        string summary = await SummariseAsync(toSummarise, cancellationToken).ConfigureAwait(false);

        // Replace as one operation so the history is never briefly inconsistent.
        history.RemoveRange(head, cut - head);
        history.Insert(head, new ChatMessage(ChatRole.User, BuildSummaryMessage(summary)));

        return new CompactionResult(
            true, summary, before, history.Count,
            $"summarised {toSummarise.Count} message(s) into one; {before} -> {history.Count}");
    }

    /// <summary>
    /// Find the last index that may be summarised, without splitting a tool exchange.
    ///
    /// Walks back from the protected tail and keeps moving earlier while the boundary
    /// would separate a tool call from its result. Moving the cut EARLIER is always
    /// safe: it keeps more verbatim history at the cost of summarising less.
    /// </summary>
    private int FindCutPoint(List<ChatMessage> history, int head)
    {
        int cut = Math.Max(head, history.Count - _options.ProtectRecent);

        // A tool message at the boundary belongs to an assistant message before it, so
        // the whole exchange has to fall on one side.
        while (cut > head && IsToolResult(history[cut]))
            cut--;

        // And an assistant message that announced tool calls must keep its results,
        // which sit after it.
        while (cut > head && HasToolCalls(history[cut - 1]))
            cut--;

        return cut;
    }

    private static bool IsToolResult(ChatMessage message) =>
        message.Role == ChatRole.Tool
        || message.Contents.Any(c => c is FunctionResultContent);

    private static bool HasToolCalls(ChatMessage message) =>
        message.Contents.Any(c => c is FunctionCallContent);

    /// <summary>
    /// Ask the model to summarise, with instructions aimed at what a resumed
    /// conversation actually needs.
    ///
    /// Not a prose precis: the summary has to preserve decisions, file paths, names and
    /// unfinished work, because those are what the next turn will reference. A summary
    /// that reads well but drops the path it was editing is worse than useless.
    /// </summary>
    private async Task<string> SummariseAsync(
        List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var transcript = new StringBuilder();

        foreach (ChatMessage message in messages)
        {
            string? text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            transcript.Append(message.Role.Value).Append(": ")
                      .AppendLine(Clip(text, 2000));
        }

        var request = new List<ChatMessage>
        {
            new(ChatRole.System, """
                Summarise this conversation so it can be continued by someone who has not
                read it. Preserve, in this order of priority:

                - decisions taken and the reason for each
                - exact file paths, command names, identifiers and numbers
                - anything the user asked for that is not finished yet
                - what was already tried and did not work, so it is not retried

                Drop pleasantries, restated context and successful intermediate steps
                whose outcome is already reflected elsewhere. Write compact prose or
                bullets, no preamble.
                """),
            new(ChatRole.User, transcript.ToString()),
        };

        try
        {
            // No tools: the summariser must not start doing things. Passing the tool
            // list here would let a summarisation call run a command.
            ChatResponse response = await summariser
                .GetResponseAsync(request, new ChatOptions { ToolMode = ChatToolMode.None }, cancellationToken)
                .ConfigureAwait(false);

            string summary = response.Text ?? string.Empty;

            return summary.Length > 0
                ? summary
                : FallbackSummary(messages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A failed summarising call must not fail the turn. A mechanical summary is
            // far worse than a real one but far better than losing the history or
            // stalling on an over-long context.
            return FallbackSummary(messages);
        }
    }

    /// <summary>
    /// A summary built without the model, for when the summarising call fails.
    ///
    /// Keeps the user's own words, since those carry the intent, and lists which tools
    /// ran. Crude, but it preserves the two things a resumed conversation most needs.
    /// </summary>
    private static string FallbackSummary(List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(mechanical summary: the summarising model was unavailable)");

        List<string> asked = messages
            .Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => Clip(m.Text!, 200))
            .ToList();

        if (asked.Count > 0)
        {
            sb.AppendLine().AppendLine("The user asked:");
            foreach (string request in asked)
                sb.Append("- ").AppendLine(request);
        }

        List<string> tools = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => c.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (tools.Count > 0)
            sb.AppendLine().Append("Tools used: ").AppendLine(string.Join(", ", tools));

        return sb.ToString();
    }

    /// <summary>
    /// Wrap the summary so the model treats it as recalled context rather than as
    /// something the user just said.
    /// </summary>
    private static string BuildSummaryMessage(string summary) =>
        "Earlier in this conversation, summarised because it grew too long:\n\n"
        + summary.Trim()
        + "\n\nContinue from here.";

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
