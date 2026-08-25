using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Shellvis.Core.Skills;

/// <summary>What happened in a turn, compressed to what a reflection needs.</summary>
/// <param name="Prompt">What the user asked.</param>
/// <param name="Steps">One line per tool call: the tool and a short preview.</param>
/// <param name="Answer">The final answer.</param>
public sealed record TurnDigest(string Prompt, IReadOnlyList<string> Steps, string Answer)
{
    /// <summary>
    /// Whether this turn is worth reflecting on at all.
    ///
    /// A turn with no tool calls discovered nothing about the machine -- it was a question
    /// answered from the model's own knowledge, and turning that into a skill would fill the
    /// index with restated trivia. The bar is deliberately mechanical rather than a judgement
    /// the reflection has to make: no tools, no reflection, no call, no cost.
    /// </summary>
    public bool WorthReflecting => Steps.Count > 0 && Answer.Length > 0;
}

/// <summary>
/// What the reflection proposed.
///
/// One shape for both stores, with <see cref="Kind"/> deciding which. Two shapes would
/// mean two schemas in one prompt and a model picking between them before it has decided
/// what it wants to say -- the choice of store follows from the content, so it is a field.
/// </summary>
/// <param name="Kind">"skill" for a procedure, "memory" or "user" for a fact.</param>
/// <param name="Name">Skill name. Ignored for a fact.</param>
/// <param name="Description">One line saying when the skill applies. Ignored for a fact.</param>
/// <param name="Body">The skill's instructions, or the fact itself.</param>
public sealed record LearnedNote(string? Kind, string? Name, string? Description, string? Body);

/// <summary>
/// Asks, after a turn, whether anything was learned worth keeping.
///
/// <b>Why this exists as code and not as a line in the system prompt.</b> It was a line in
/// the system prompt first, twice: once as prose, once as the last item of the working-rule
/// list, imperative and with an explicit trigger. Both were tested live and neither fired
/// once -- the turn completed, answered correctly, and called no skill tool. Two phrasings
/// giving the same result is the answer: a model at the end of a fifteen-round turn has no
/// attention left for a meta-task unrelated to the one it was given, and no amount of
/// rewording buys that attention back.
///
/// So the judgement is asked as its own question, in its own call, about a turn that is
/// already finished. It competes with nothing. And the WRITING is done here rather than by
/// the model calling a tool, which moves the reliability from the model's attention to a
/// parser.
/// </summary>
public sealed class SkillReflector(IChatClient client, SkillIndex index, Memory.MemoryStore? memory = null)
{
    /// <summary>
    /// The reflection is a courtesy, not part of the answer. If it takes longer than this
    /// it is abandoned: the user is waiting to type their next prompt, and a local endpoint
    /// with one slot makes them wait for this too.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    /// <summary>How much of a step preview to include. Enough to identify it, not to replay it.</summary>
    private const int StepPreview = 160;

    public async Task<string?> ReflectAsync(TurnDigest digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);

        if (!digest.WorthReflecting)
            return null;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        try
        {
            var options = new ChatOptions
            {
                // No tools, deliberately. This call exists to judge and to draft; giving it
                // the catalogue would let a reflection run commands, which is a second turn
                // the user did not ask for.
                ToolMode = ChatToolMode.None,
            };

            ChatResponse response = await client
                .GetResponseAsync(
                    [new ChatMessage(ChatRole.User, BuildPrompt(digest))],
                    options,
                    budget.Token)
                .ConfigureAwait(false);

            return Apply(response.Text);
        }
        catch (OperationCanceledException)
        {
            // Includes the budget expiring. Silent: a reflection that timed out is not
            // news, and reporting it every turn would train the user to ignore the line.
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    private string BuildPrompt(TurnDigest digest)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            "You are reviewing a task that has just finished on a Windows machine. Decide "
            + "whether anything about it is worth writing down for a later session.");
        sb.AppendLine();
        sb.AppendLine("There are two places to write, and which one matters:");
        sb.AppendLine();
        sb.AppendLine("  skill  -- a PROCEDURE. Numbered steps with the exact commands, the");
        sb.AppendLine("            pitfalls, how to verify it worked. Loaded on demand, so it");
        sb.AppendLine("            can be long. Worth writing after a task that took several");
        sb.AppendLine("            steps, after an error you had to work around, or after");
        sb.AppendLine("            discovering a workflow that was not obvious.");
        sb.AppendLine("  memory -- a FACT about this machine or this work. One sentence. Read");
        sb.AppendLine("            into EVERY later turn, so it must be short and must still");
        sb.AppendLine("            be true in a month.");
        sb.AppendLine("  user   -- a FACT about the person: their role, a preference they");
        sb.AppendLine("            stated, a correction they made, a convention they follow.");
        sb.AppendLine();
        sb.AppendLine("Write facts as statements, never as orders to yourself. \"The user");
        sb.AppendLine("prefers short answers\" is right; \"Always answer briefly\" is wrong -- an");
        sb.AppendLine("imperative gets re-read as a command in a later session and overrides");
        sb.AppendLine("what is being asked then.");
        sb.AppendLine();
        sb.AppendLine("Never write down the answer itself. \"Free space is reported by");
        sb.AppendLine("Get-PSDrive here\" keeps; \"the disk had 41 GB free\" does not. Never");
        sb.AppendLine("include a password, token or key.");
        sb.AppendLine();

        // The observed failure mode of the first live run: it remembered "the cmdlet to
        // list processes is Get-Process". True, declarative, correctly filed as a fact --
        // and worthless, because it is general Windows knowledge the model already has.
        // What earns a place in every future prompt is what the model could NOT have known
        // without this turn.
        sb.AppendLine("It must be something you could not have known without this task, and");
        sb.AppendLine("specific to THIS machine, THIS setup or THIS user. Three things are");
        sb.AppendLine("therefore never worth a line, and they are what a review like this");
        sb.AppendLine("reaches for by mistake:");
        sb.AppendLine();
        sb.AppendLine("  - General knowledge about Windows or PowerShell. You already have it.");
        sb.AppendLine("    \"Get-Process lists processes\" is worthless.");
        sb.AppendLine("  - How one of your own tools behaves: its arguments, its output shape,");
        sb.AppendLine("    what it returns when empty. Its description already says so and you");
        sb.AppendLine("    see the result every time you call it.");
        sb.AppendLine("  - A restatement of what the task asked or answered.");
        sb.AppendLine();
        sb.AppendLine("What IS worth a line: a name, a path, a version, a setting, a habit or a");
        sb.AppendLine("constraint that belongs to this installation. \"The mail account here is");
        sb.AppendLine("Exchange, so the local contacts folder is empty and names resolve through");
        sb.AppendLine("the GAL\" is worth keeping. Most turns are worth nothing at all, and");
        sb.AppendLine("saying so is the common and correct answer -- prefer NONE when unsure.");
        sb.AppendLine();

        IReadOnlyList<SkillDefinition> existing = index.All;

        if (existing.Count > 0)
        {
            sb.AppendLine("Notes that already exist. If one of these covers the ground, reuse");
            sb.AppendLine("its exact name and write the improved version -- it will be updated,");
            sb.AppendLine("not duplicated:");

            foreach (SkillDefinition skill in existing)
                sb.Append("- ").Append(skill.Name).Append(": ").AppendLine(skill.Description);

            sb.AppendLine();
        }

        sb.AppendLine("The task:");
        sb.Append("  asked: ").AppendLine(Clip(digest.Prompt, 400));

        foreach (string step in digest.Steps)
            sb.Append("  step:  ").AppendLine(Clip(step, StepPreview));

        sb.Append("  answered: ").AppendLine(Clip(digest.Answer, 800));
        sb.AppendLine();
        sb.AppendLine("Reply with nothing but the word NONE, or nothing but one JSON object:");
        sb.AppendLine("  {\"kind\":\"skill\",\"name\":\"kebab-case\",\"description\":\"when to use it\",");
        sb.AppendLine("   \"body\":\"markdown steps\"}");
        sb.AppendLine("  {\"kind\":\"memory\",\"body\":\"one factual sentence\"}");
        sb.AppendLine("  {\"kind\":\"user\",\"body\":\"one factual sentence about the person\"}");

        return sb.ToString();
    }

    /// <summary>
    /// Parse the reply and write the skill.
    ///
    /// Defensive about the shape because a model wraps JSON in prose, in fences, or in both
    /// however plainly it was asked not to. The first '{' to the last '}' is what gets
    /// parsed; anything around it is discarded rather than treated as a refusal.
    /// </summary>
    private string? Apply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return null;

        string text = reply.Trim();

        // The intended negative answer, and the common one.
        if (text.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return null;

        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');

        if (start < 0 || end <= start)
            return null;

        LearnedNote? note;

        try
        {
            note = JsonSerializer.Deserialize<LearnedNote>(
                text[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }

        if (note is null || string.IsNullOrWhiteSpace(note.Body))
            return null;

        string kind = (note.Kind ?? string.Empty).Trim().ToLowerInvariant();

        // A fact goes to the store that is read every turn. Refused when there is no store
        // -- better to say nothing than to file a fact as a procedure, where it would only
        // be read if the model happened to load it.
        if (kind is "memory" or "user")
        {
            if (memory is null)
                return null;

            Memory.MemoryTarget target = kind == "user"
                ? Memory.MemoryTarget.User
                : Memory.MemoryTarget.Memory;

            Memory.MemoryResult result = memory.Add(target, note.Body.Trim());

            // A refusal is reported and a success is reported; only "already known" is
            // silent, because it is the ordinary outcome of learning the same thing twice.
            return result.Message.Contains("already remembered", StringComparison.Ordinal)
                ? null
                : $"{kind}: {result.Message}";
        }

        // Anything else is treated as a skill, including a missing kind: a proposal with a
        // name and a description is a procedure whatever it called itself.
        if (string.IsNullOrWhiteSpace(note.Name) || string.IsNullOrWhiteSpace(note.Description))
            return null;

        // SkillWriter enforces the name, size and credential rules, and its refusals are
        // sentences. They are returned rather than swallowed: a reflection that produced
        // something and had it rejected is worth a line, unlike one that produced nothing.
        return SkillWriter.Write(
            index,
            note.Name.Trim(),
            note.Description.Trim(),
            note.Body.Trim(),
            category: "learned");
    }

    private static string Clip(string value, int limit)
    {
        string flat = value.ReplaceLineEndings(" ").Trim();
        return flat.Length <= limit ? flat : flat[..limit] + "...";
    }
}
