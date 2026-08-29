using System.Text;

using Shellvis.Core.Agent;

namespace Shellvis.Core.Tools;

/// <summary>
/// Asking the user, when the answer is theirs to give.
/// </summary>
public sealed class ClarifyTools(IClarifier clarifier)
{
    /// <summary>The most options a question may carry.</summary>
    /// <remarks>
    /// Four. Beyond that the list stops being a decision and becomes a menu to study, and the
    /// user is better served by being asked in their own words. Anything past the fourth is
    /// dropped and the answer says so rather than silently losing it.
    /// </remarks>
    private const int MaxOptions = 4;

    /// <summary>A header longer than this is a sentence, not a label.</summary>
    private const int MaxHeader = 12;

    [ShellvisTool(
        "clarify",
        SideEffect.ReadOnly,
        Description =
            "Ask the user a question with two to four concrete options when their answer "
            + "would change what you do next, and you cannot settle it yourself. The user can "
            + "always write something else instead of picking. Use it for decisions that are "
            + "theirs -- which of several approaches, which account, which file. Do NOT use it "
            + "for anything you can find out by looking, and never to ask whether to carry on: "
            + "a question that costs the user attention and changes nothing is worse than no "
            + "question. Give each option a short label and a description saying what it means "
            + "or costs.",
        PreviewParameter = "question",
        Glyph = "person")]
    public async Task<string> Clarify(
        string question,
        string[] options,
        string? header = null,
        string[]? descriptions = null,
        bool multiSelect = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
            return "error: a question is required.";

        string[] labels = (options ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim())
            .ToArray();

        // One option is not a choice, it is a statement. Refused with the reason, so the model
        // either finds a second option or stops asking and acts.
        if (labels.Length < 2)
        {
            return "error: clarify needs at least two options. With only one there is nothing "
                + "to decide -- either name a real alternative, or proceed and say what you "
                + "assumed.";
        }

        string note = string.Empty;

        if (labels.Length > MaxOptions)
        {
            note = $" (only the first {MaxOptions} of {labels.Length} options were shown)";
            labels = [.. labels.Take(MaxOptions)];
        }

        var choices = new List<ClarifyOption>(labels.Length);

        for (int i = 0; i < labels.Length; i++)
        {
            string description = descriptions is not null && i < descriptions.Length
                ? descriptions[i]?.Trim() ?? string.Empty
                : string.Empty;

            choices.Add(new ClarifyOption(labels[i], description));
        }

        string chip = (header ?? string.Empty).Trim();

        if (chip.Length > MaxHeader)
            chip = chip[..MaxHeader];

        if (chip.Length == 0)
            chip = "Choose";

        ClarifyAnswer answer;

        try
        {
            answer = await clarifier
                .AskAsync(new ClarifyRequest(question.Trim(), chip, choices, multiSelect), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "the question was cancelled along with the turn.";
        }
        catch (Exception ex)
        {
            return $"the question could not be asked: {ex.Message}. Proceed on your best "
                + "assumption and say what you assumed.";
        }

        // No answer is NOT a refusal, and the difference has to be spelled out or the model
        // treats it as one and gives up. A timeout, a cancelled dialog and a scheduled run all
        // land here, and in every one of them the right move is to continue with a stated
        // assumption rather than to stop.
        if (!answer.Answered)
        {
            return "nobody answered -- the question timed out, was dismissed, or this is a "
                + "scheduled run with no one present. Decide yourself, act, and say in your "
                + "answer which option you assumed.";
        }

        if (!string.IsNullOrWhiteSpace(answer.Other))
            return $"the user wrote: {answer.Other.Trim()}{note}";

        if (answer.Chosen.Count == 0)
        {
            return "the user closed the question without choosing. Decide yourself and say "
                + "which option you assumed.";
        }

        var text = new StringBuilder("the user chose: ");
        text.Append(string.Join(", ", answer.Chosen));
        text.Append(note);

        return text.ToString();
    }
}
