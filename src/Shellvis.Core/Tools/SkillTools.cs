using System.Text;
using Shellvis.Core.Config;
using Shellvis.Core.Skills;

namespace Shellvis.Core.Tools;

/// <summary>
/// Skills as tools: the second and third tiers of progressive disclosure.
///
/// The first tier is the index in the system prompt, which the model always sees.
/// <c>skills_list</c> is the same information filtered, for when it wants to look
/// around. <c>skill_view</c> loads a full body, and is the only call that costs real
/// context.
///
/// <c>skill_manage</c> lets the agent write skills as well as read them, which is what
/// makes the mechanism accumulate: a procedure worked out once and saved is available
/// to every later session, unlike one buried in a transcript.
/// </summary>
public sealed class SkillTools(SkillIndex index)
{
    /// <summary>Subfolders a supporting file may be written into.</summary>
    private static readonly string[] AllowedSubfolders = ["references", "templates", "scripts", "assets"];

    [ShellvisTool(
        "skills_list",
        SideEffect.ReadOnly,
        Description =
            "List the available skills with their descriptions. Skills are instructions "
            + "written for specific tasks. Pass a category to narrow the list.",
        PreviewParameter = "category",
        Glyph = "book")]
    public string ListSkills(string? category = null)
    {
        IReadOnlyList<SkillDefinition> skills = index.All;

        if (category is { Length: > 0 })
        {
            skills = skills
                .Where(s => s.Category.Contains(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (skills.Count == 0)
        {
            return category is { Length: > 0 }
                ? $"no skills in category '{category}'."
                : "no skills are installed. Use skill_manage to create one.";
        }

        var sb = new StringBuilder();
        sb.Append(skills.Count).AppendLine(" skill(s):");

        foreach (SkillDefinition skill in skills.OrderBy(s => s.QualifiedName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("  ").Append(skill.QualifiedName).Append(": ").AppendLine(skill.Description);

            if (skill.SupportingFiles.Count > 0)
            {
                sb.Append("      files: ")
                  .AppendLine(string.Join(", ", skill.SupportingFiles.Take(6)));
            }
        }

        sb.AppendLine().AppendLine("Load one with skill_view before acting on its subject.");
        return sb.ToString();
    }

    [ShellvisTool(
        "skill_view",
        SideEffect.ReadOnly,
        Description =
            "Load the full instructions of one skill. Do this before acting on a task a "
            + "skill covers. Pass filePath to read one of its supporting files instead "
            + "of the main body.",
        PreviewParameter = "name",
        Glyph = "book")]
    public string ViewSkill(string name, string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "error: a skill name is required.";

        if (filePath is { Length: > 0 })
        {
            string? file = index.ReadSupportingFile(name, filePath);

            return file
                ?? $"error: '{filePath}' is not a readable file of skill '{name}'. "
                    + "Use skills_list to see which files it has.";
        }

        SkillContent? content = index.Read(name);

        if (content is null)
        {
            IEnumerable<string> nearby = index.All
                .Where(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                         || name.Contains(s.Name, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.QualifiedName)
                .Take(3);

            string suggestion = nearby.Any()
                ? $" Did you mean {string.Join(" or ", nearby)}?"
                : " Use skills_list to see what is available.";

            return $"error: no skill named '{name}'.{suggestion}";
        }

        var sb = new StringBuilder();
        sb.Append("skill: ").AppendLine(content.Definition.QualifiedName);
        sb.Append("purpose: ").AppendLine(content.Definition.Description);

        if (content.Definition.SupportingFiles.Count > 0)
        {
            sb.Append("files: ")
              .AppendLine(string.Join(", ", content.Definition.SupportingFiles));
        }

        sb.AppendLine().Append(content.Body);
        return sb.ToString();
    }

    [ShellvisTool(
        "skill_manage",
        SideEffect.Mutating,
        Description =
            "Create, update or delete a skill. A skill is your procedural memory: reusable "
            + "instructions for a kind of task, so later sessions inherit it instead of "
            + "rediscovering it. "
            // The trigger conditions live here as well as in the system prompt, because a
            // description is read at the moment the choice is being made while the prompt
            // was read hundreds of tokens ago.
            + "Create when: a task took five or more tool calls and succeeded, an error had "
            + "to be worked around, the user corrected your approach and the new one worked, "
            + "or a non-obvious workflow turned up. Update when: a skill you used was stale, "
            + "wrong or missing a step -- patch it immediately rather than working around it, "
            + "because an unmaintained skill will still be trusted. Skip simple one-offs, and "
            + "put single FACTS in memory instead. "
            + "A good skill states when it applies, then numbered steps with the exact "
            + "commands, then the pitfalls, then how to verify it worked. "
            + "Actions: create, update, delete, write_file.",
        PreviewParameter = "name",
        Glyph = "book")]
    public string ManageSkill(
        string action,
        string name,
        string? description = null,
        string? body = null,
        string? category = null,
        string? filePath = null,
        string? fileContent = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "error: a skill name is required.";

        // The name becomes a folder, so anything path-like in it has to be refused
        // rather than sanitised: quietly renaming a skill would confuse every later
        // reference to it.
        if (name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return "error: a skill name may not contain path characters.";

        return action.Trim().ToLowerInvariant() switch
        {
            "create" or "update" => Write(name, description, body, category),
            "delete" => Delete(name),
            "write_file" => WriteSupportingFile(name, filePath, fileContent),
            _ => "error: action must be create, update, delete or write_file.",
        };
    }

    /// <summary>
    /// Delegates to <see cref="SkillWriter"/>, which the post-turn reflection uses too, so
    /// both produce byte-identical files.
    /// </summary>
    private string Write(string name, string? description, string? body, string? category) =>
        SkillWriter.Write(index, name, description, body, category);

    private string Delete(string name)
    {
        SkillDefinition? skill = index.Find(name);
        if (skill is null)
            return $"error: no skill named '{name}'.";

        string folder = Path.GetDirectoryName(skill.Path)!;

        // Refuse to delete outside the writable skills directory: an external skill
        // directory from the config belongs to whoever put it there.
        string root = Path.GetFullPath(ShellvisPaths.SkillsDirectory);
        if (!Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return $"error: '{name}' lives outside the writable skills directory and "
                + "cannot be deleted from here.";
        }

        try
        {
            Directory.Delete(folder, recursive: true);
            index.Invalidate();
            return $"deleted skill '{name}' and its folder.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"error: could not delete the skill: {ex.Message}";
        }
    }

    private string WriteSupportingFile(string name, string? filePath, string? content)
    {
        if (string.IsNullOrWhiteSpace(filePath) || content is null)
            return "error: a file path and its content are required.";

        SkillDefinition? skill = index.Find(name);
        if (skill is null)
            return $"error: no skill named '{name}'. Create it first.";

        string normalised = filePath.Replace('\\', '/').TrimStart('/');
        string top = normalised.Split('/')[0];

        // Restricting to the four conventional folders keeps a skill recognisable, and
        // stops the agent using a skill folder as general scratch space.
        if (!AllowedSubfolders.Contains(top, StringComparer.OrdinalIgnoreCase))
        {
            return $"error: supporting files must live under {string.Join(", ", AllowedSubfolders)}.";
        }

        string folder = Path.GetDirectoryName(skill.Path)!;
        string target = Path.GetFullPath(Path.Combine(folder, normalised));

        if (!target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "error: the path escapes the skill folder.";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content, Encoding.UTF8);
            index.Invalidate();

            return $"wrote {normalised} into skill '{name}'.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"error: could not write the file: {ex.Message}";
        }
    }

    private static string Sanitise(string value) =>
        string.Concat(value.Split(Path.GetInvalidFileNameChars())).Replace('\\', '/').Trim('/');

    /// <summary>
    /// Quote a description for YAML if it contains anything that would break the
    /// scalar. A colon in a description is common and unquoted would silently truncate
    /// the value at the colon.
    /// </summary>
    private static string Quote(string value) =>
        value.IndexOfAny([':', '#', '\n', '"']) >= 0
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal).ReplaceLineEndings(" ") + "\""
            : value;
}
