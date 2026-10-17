using System.Text;
using Shellvis.Core.Memory;

namespace Shellvis.Core.Tools;

/// <summary>
/// The memory tool: durable declarative facts.
///
/// One tool with an action rather than four, because the four would be adjacent entries in
/// the catalogue that differ by a verb, and a model choosing between them wastes attention
/// on a decision that carries no meaning.
///
/// The division of labour with skills is stated in the description and not left to
/// inference: facts here, procedures in skills. Getting it wrong in either direction is
/// costly. A procedure in memory is read into every single turn for ever, and a fact in a
/// skill is only read if the model happens to load it.
/// </summary>
public sealed class MemoryTools(MemoryStore store)
{
    [ShellvisTool(
        "memory",
        SideEffect.Mutating,
        Description =
            "Remember a durable FACT across sessions, or edit what is remembered. Facts "
            + "belong here; procedures belong in a skill. Save proactively: a preference "
            + "the user states, a correction they make, something you discovered about "
            + "this machine, a convention or quirk of their setup. Do NOT save task "
            + "progress, results or what you just did. "
            + "Write declarative facts, not instructions to yourself: 'The user prefers "
            + "short answers' is right, 'Always answer briefly' is wrong, because an "
            + "imperative is re-read as an order in a later session and can override what "
            + "is being asked then. "
            + "Targets: 'memory' for the machine and the work, 'user' for the person. "
            + "Actions: add, replace, remove, list.",
        PreviewParameter = "content",
        Glyph = "memory")]
    public string Remember(
        string action,
        string? content = null,
        string? find = null,
        string target = "memory")
    {
        MemoryTarget which = target.Trim().Equals("user", StringComparison.OrdinalIgnoreCase)
            ? MemoryTarget.User
            : MemoryTarget.Memory;

        return action.Trim().ToLowerInvariant() switch
        {
            "add" => store.Add(which, content).Message,
            "replace" => store.Replace(which, find, content).Message,
            "remove" or "delete" or "forget" => store.Remove(which, find).Message,
            "list" or "read" or "show" => List(which),
            _ => "error: action must be add, replace, remove or list.",
        };
    }

    private string List(MemoryTarget target)
    {
        IReadOnlyList<string> entries = store.Entries(target);

        if (entries.Count == 0)
            return $"nothing is remembered under '{Name(target)}' yet.";

        var sb = new StringBuilder();
        sb.Append(Name(target)).Append(", ").Append(store.Usage(target)).AppendLine(":");

        foreach (string entry in entries)
            sb.Append("- ").AppendLine(entry.ReplaceLineEndings(" "));

        return sb.ToString().TrimEnd();
    }

    private static string Name(MemoryTarget target) =>
        target == MemoryTarget.User ? "user" : "memory";
}
