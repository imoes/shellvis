using Shellvis.Core.Skills;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Verifies skill discovery, and specifically the text that reaches the model.
///
/// The distinction matters because of how the MCP namespacing bug hid: that probe
/// checked the registry, the registry was correct, and the defect lived entirely in
/// what went onto the wire. So this asserts on the generated prompt section, not only
/// on the parsed index -- above all that the prompt carries names and descriptions and
/// NOT bodies, because that separation is the whole point of the design.
/// </summary>
internal static class SkillProbe
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "shellvis-skill-probe");

        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);

        Directory.CreateDirectory(root);

        // A phrase that must never appear in the prompt index. If it does, bodies are
        // being injected and the context budget is being spent on every skill at once.
        const string bodyMarker = "BODY-SHOULD-NOT-BE-IN-THE-PROMPT";

        WriteSkill(root, "office/quarterly-report", "quarterly-report",
            "Build the quarterly report deck from the sales workbook.",
            $"# Steps\n\n1. Read the workbook.\n2. Build the deck.\n\n{bodyMarker}\n");

        WriteSkill(root, "excel-without-office", "excel-without-office",
            "Read spreadsheets when Excel is not installed.",
            "Use excel_read.\n",
            fallbackFor: "excel_read");

        WriteSkill(root, "needs-missing-tool", "needs-missing-tool",
            "Only relevant when a nonexistent tool exists.",
            "Never shown.\n",
            requires: "tool_that_does_not_exist");

        string snapshot = Path.Combine(root, ".snapshot.json");
        var index = new SkillIndex([root], snapshot);

        int failures = 0;

        // ------------------------------------------------------------- discovery
        failures += Expect(index.All.Count == 3, $"all three skills discovered (found {index.All.Count})");

        SkillDefinition? report = index.Find("quarterly-report");
        failures += Expect(report is not null, "a skill is findable by its bare name");
        failures += Expect(
            report?.QualifiedName == "office/quarterly-report",
            "the category comes from the folder structure");
        failures += Expect(
            index.Find("office/quarterly-report") is not null,
            "and by its qualified name");

        // ------------------------------------------------- the prompt section only
        var available = new HashSet<string>(StringComparer.Ordinal) { "excel_read", "skill_view" };
        string prompt = index.BuildPromptSection(available);

        Console.WriteLine("\n--- prompt section as the model receives it ---");
        Console.WriteLine(prompt.TrimEnd());
        Console.WriteLine("--- end ---\n");

        failures += Expect(
            prompt.Contains("quarterly-report", StringComparison.Ordinal),
            "the prompt names the skill");
        failures += Expect(
            prompt.Contains("Build the quarterly report deck", StringComparison.Ordinal),
            "the prompt carries its description");
        failures += Expect(
            !prompt.Contains(bodyMarker, StringComparison.Ordinal),
            "the prompt does NOT carry the body");

        // ------------------------------------------------------ conditional showing
        failures += Expect(
            !prompt.Contains("excel-without-office", StringComparison.Ordinal),
            "a fallback skill is hidden while its real tool exists");
        failures += Expect(
            !prompt.Contains("needs-missing-tool", StringComparison.Ordinal),
            "a skill needing an absent tool is hidden");

        string withoutExcel = index.BuildPromptSection(
            new HashSet<string>(StringComparer.Ordinal) { "skill_view" });
        failures += Expect(
            withoutExcel.Contains("excel-without-office", StringComparison.Ordinal),
            "and the fallback appears once the real tool is gone");

        // -------------------------------------------------------- tier three: body
        SkillContent? content = index.Read("quarterly-report");
        failures += Expect(content is not null, "the body loads on demand");
        failures += Expect(
            content?.Body.Contains(bodyMarker, StringComparison.Ordinal) == true,
            "the loaded body has the content");
        failures += Expect(
            content?.Body.Contains("description:", StringComparison.Ordinal) == false,
            "the frontmatter is stripped from the body");

        // ------------------------------------------------------------- snapshot
        failures += Expect(File.Exists(snapshot), "a snapshot is written");

        var second = new SkillIndex([root], snapshot);
        failures += Expect(second.All.Count == 3, "a fresh index reads the snapshot");

        // Touching a file must invalidate it, or an edited skill stays stale forever.
        File.AppendAllText(Path.Combine(root, "office", "quarterly-report", "SKILL.md"), "\nedited\n");
        var third = new SkillIndex([root], snapshot);
        failures += Expect(
            third.Read("quarterly-report")?.Body.Contains("edited", StringComparison.Ordinal) == true,
            "editing a skill invalidates the snapshot");

        // ------------------------------------------------------ path containment
        string? escaped = index.ReadSupportingFile("quarterly-report", "../../../../secrets.txt");
        failures += Expect(escaped is null, "a traversal path is refused");

        // ---------------------------------------------------------- the tool surface
        var tools = new SkillTools(index);

        string listed = tools.ListSkills();
        failures += Expect(
            listed.Contains("quarterly-report", StringComparison.Ordinal),
            "skills_list shows the skill");
        failures += Expect(
            !listed.Contains(bodyMarker, StringComparison.Ordinal),
            "skills_list does not leak bodies either");

        string viewed = tools.ViewSkill("quarterly-report");
        failures += Expect(
            viewed.Contains(bodyMarker, StringComparison.Ordinal),
            "skill_view returns the body");

        string missing = tools.ViewSkill("quarterly");
        failures += Expect(
            missing.Contains("Did you mean", StringComparison.Ordinal),
            "a near miss suggests the right name");

        string refused = tools.ManageSkill("create", "bad/name", "d", "b");
        failures += Expect(
            refused.StartsWith("error:", StringComparison.Ordinal),
            "a path-like skill name is refused rather than sanitised");

        string noDescription = tools.ManageSkill("create", "nameless", null, "body");
        failures += Expect(
            noDescription.StartsWith("error:", StringComparison.Ordinal),
            "a skill without a description is refused");

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: three-tier disclosure holds, and bodies never reach the prompt."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    private static void WriteSkill(
        string root, string relativeFolder, string name, string description, string body,
        string? requires = null, string? fallbackFor = null)
    {
        string folder = Path.Combine(root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folder);

        var frontmatter = new List<string>
        {
            "---",
            $"name: {name}",
            $"description: {description}",
        };

        if (requires is not null)
            frontmatter.Add($"requires_tools: [{requires}]");

        if (fallbackFor is not null)
            frontmatter.Add($"fallback_for_tools: [{fallbackFor}]");

        frontmatter.Add("---");
        frontmatter.Add(string.Empty);

        File.WriteAllText(
            Path.Combine(folder, "SKILL.md"),
            string.Join("\n", frontmatter) + body);
    }

    private static int Expect(bool condition, string what)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }
}
