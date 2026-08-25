namespace Shellvis.Core.Skills;

/// <summary>
/// One skill: a folder containing SKILL.md with YAML frontmatter and a Markdown body.
/// </summary>
/// <param name="Name">Identifier, from the frontmatter or the folder name.</param>
/// <param name="Description">
/// One line describing WHEN to use it. This is the only part that goes into the system
/// prompt, so it has to be enough for the model to decide whether to load the rest.
/// </param>
/// <param name="Category">Parent folder, when skills are grouped. Empty at the root.</param>
/// <param name="Path">Absolute path to the SKILL.md file.</param>
/// <param name="RequiresTools">
/// Tools the skill needs. Hidden when they are absent, since instructions for a
/// capability that is not present only invite the model to try it anyway.
/// </param>
/// <param name="FallbackForTools">
/// Tools this skill stands in for. Hidden when they ARE present -- the skill exists to
/// describe a workaround, and a workaround should not compete with the real thing.
/// </param>
/// <param name="SupportingFiles">
/// Files under references/, templates/, scripts/ or assets/, loadable individually.
/// </param>
public sealed record SkillDefinition(
    string Name,
    string Description,
    string Category,
    string Path,
    IReadOnlyList<string> RequiresTools,
    IReadOnlyList<string> FallbackForTools,
    IReadOnlyList<string> SupportingFiles)
{
    /// <summary>Full name including category, as the model addresses it.</summary>
    public string QualifiedName =>
        Category.Length == 0 ? Name : $"{Category}/{Name}";

    /// <summary>
    /// Whether this skill should be offered, given what the session can actually do.
    ///
    /// The two conditions pull in opposite directions on purpose. A skill about driving
    /// Excel is useless without the Excel tools, and a skill explaining how to fake it
    /// with CSV is noise once those tools exist.
    /// </summary>
    public bool ShouldShow(IReadOnlySet<string> availableTools)
    {
        if (RequiresTools.Count > 0 && !RequiresTools.All(availableTools.Contains))
            return false;

        if (FallbackForTools.Count > 0 && FallbackForTools.Any(availableTools.Contains))
            return false;

        return true;
    }
}

/// <summary>
/// A skill with its body loaded. Kept separate from the definition because the body is
/// the expensive part and is only read on demand.
/// </summary>
public sealed record SkillContent(SkillDefinition Definition, string Body);
