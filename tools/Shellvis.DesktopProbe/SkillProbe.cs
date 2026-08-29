using Shellvis.Core.Config;
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

        // --------------------------------------------------- the shipped skill
        //
        // A skill ships with the product now, and a file that exists in the repository is
        // not the same thing as a skill the agent can see. Three separate steps can lose
        // it: the build may not copy it, the index may not be pointed at the copy, and the
        // frontmatter may not parse. Each one is checked.
        Console.WriteLine();
        Console.WriteLine("the shipped secretary skill:");

        string bundled = ShellvisPaths.BundledSkillsDirectory;

        failures += Expect(Directory.Exists(bundled),
            $"the build copied a skills folder next to the binary ({bundled})");

        var shipped = new SkillIndex([bundled], Path.Combine(root, ".bundled.json"));

        SkillDefinition? secretary = shipped.All
            .FirstOrDefault(d => d.QualifiedName == "assistant/secretary");

        failures += Expect(secretary is not null, "assistant/secretary is discovered");
        failures += Expect(
            secretary?.Description.Contains("triage", StringComparison.OrdinalIgnoreCase) == true,
            "its frontmatter parsed, so it has a real description");

        // It declares the tools it needs, so it is offered only where it can be followed.
        failures += Expect(secretary?.RequiresTools.Count > 0,
            "it declares the tools it depends on");
        failures += Expect(
            secretary?.ShouldShow(new HashSet<string>(StringComparer.Ordinal)) == false,
            "and is hidden when those tools are absent");
        failures += Expect(
            secretary?.ShouldShow(new HashSet<string>(
                secretary.RequiresTools, StringComparer.Ordinal)) == true,
            "and offered when they are present");

        // The body must NOT be in the prompt index: this skill is long, and the whole
        // reason for three-tier disclosure is that it costs nothing until it is loaded.
        string shippedPrompt = shipped.BuildPromptSection(
            new HashSet<string>(secretary?.RequiresTools ?? [], StringComparer.Ordinal));

        failures += Expect(
            shippedPrompt.Contains("secretary", StringComparison.Ordinal),
            "it appears in the prompt index by name");
        failures += Expect(
            !shippedPrompt.Contains("DISCRETION IS ABSOLUTE", StringComparison.Ordinal),
            "but its body does not, so it costs a line and not a page");

        // Every shipped skill, not just the one this block names. A second one was added
        // without touching these checks, and a file that ships but never parses is exactly
        // the failure the secretary skill already had once: an unquoted colon in its
        // description silently dropped ALL its metadata.
        foreach (SkillDefinition definition in shipped.All)
        {
            failures += Expect(
                definition.Description != "(no description)",
                $"{definition.QualifiedName} has a description, so its frontmatter parsed");
        }

        failures += Expect(
            shipped.All.Any(d => d.QualifiedName == "assistant/daily-briefing"),
            "assistant/daily-briefing ships too");

        // A user skill of the same name must win. What ships is a default, not a rule.
        WriteSkill(root, "assistant/secretary", "secretary", "the user own version", "mine\n");

        var both = new SkillIndex([root, bundled], Path.Combine(root, ".both.json"));

        failures += Expect(
            both.All.Count(d => d.QualifiedName == "assistant/secretary") == 1,
            "a user skill of the same name does not double up");
        failures += Expect(
            both.Find("assistant/secretary")?.Description == "the user own version",
            "and it is the user own that wins over the shipped one");

        // And the check that actually matters for the product: the SHELL ships them. The
        // block above runs against this harness own copy, which proves the skill parses and
        // behaves, and would keep passing if the Shell stopped copying the folder entirely.
        // Only reachable from a repository build; an installed copy has no sibling project,
        // and the probe says so rather than passing silently.
        string? shellOutput = FindShellOutput();

        if (shellOutput is null)
        {
            Console.WriteLine(
                "  ..   the Shell build output is not beside this harness, so whether the");
            Console.WriteLine(
                "       application itself ships the skills is NOT checked by this run.");
        }
        else
        {
            failures += Expect(
                File.Exists(Path.Combine(shellOutput, "skills", "assistant", "secretary", "SKILL.md")),
                "the application itself ships the skill, not just this harness");
        }

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

    /// <summary>
    /// The Shell build output, when this harness is running from the repository.
    ///
    /// Walks up looking for the solution rather than assuming a fixed depth, because the
    /// path from a probe binary to the repository root is exactly the kind of constant that
    /// is wrong after the next reorganisation and fails in a way nobody reads.
    /// </summary>
    private static string? FindShellOutput()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null && !File.Exists(Path.Combine(here.FullName, "Shellvis.slnx")))
            here = here.Parent;

        if (here is null)
            return null;

        string shell = Path.Combine(here.FullName, "src", "Shellvis.Shell", "bin");

        if (!Directory.Exists(shell))
            return null;

        // Whichever configuration was built most recently: the harness should report on the
        // build that exists, not insist on one that may not have been made.
        return Directory
            .EnumerateDirectories(shell, "win-x64", SearchOption.AllDirectories)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static int Expect(bool condition, string what)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }
}
