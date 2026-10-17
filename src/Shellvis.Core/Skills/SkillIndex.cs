using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Shellvis.Core.Skills;

/// <summary>
/// Discovers skills and builds the index that goes into the system prompt.
///
/// The design point is progressive disclosure, in three tiers. Only names and one-line
/// descriptions reach the system prompt; <c>skills_list</c> gives the same thing
/// filtered by category; <c>skill_view</c> loads a full body on demand. A dozen skills
/// of a few thousand words each would otherwise consume the context before the user has
/// said anything.
///
/// Discovery is cached twice. An in-memory copy serves repeat calls in a session, and a
/// disk snapshot survives restarts -- validated against a manifest of every index
/// file's modification time and size, so any edit invalidates it. Without the snapshot,
/// every cold start walks the whole skills tree and parses every frontmatter block.
/// </summary>
public sealed class SkillIndex
{
    /// <summary>Snapshot format version, so a changed shape invalidates old files.</summary>
    private const int SnapshotVersion = 1;

    private readonly List<string> _roots;
    private readonly string _snapshotPath;

    private List<SkillDefinition>? _cached;

    public SkillIndex(IEnumerable<string> roots, string? snapshotPath = null)
    {
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        _snapshotPath = snapshotPath
            ?? Path.Combine(Config.ShellvisPaths.Home, ".skills-snapshot.json");
    }

    /// <summary>Every discovered skill, from cache when possible.</summary>
    public IReadOnlyList<SkillDefinition> All => _cached ??= Load();

    /// <summary>Forget the caches, on disk as well, so the next read rescans.</summary>
    public void Invalidate()
    {
        _cached = null;

        try
        {
            if (File.Exists(_snapshotPath))
                File.Delete(_snapshotPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale snapshot is harmless: the manifest check rejects it anyway.
        }
    }

    /// <summary>Find one skill by name, with or without its category prefix.</summary>
    public SkillDefinition? Find(string name)
    {
        string wanted = name.Trim().Replace('\\', '/');

        return All.FirstOrDefault(s =>
                s.QualifiedName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(s => s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Read a skill's body.
    ///
    /// Loaded on demand rather than cached: a body can be thousands of words, and the
    /// point of the index is precisely that they are not all held in memory.
    /// </summary>
    public SkillContent? Read(string name)
    {
        SkillDefinition? definition = Find(name);
        if (definition is null)
            return null;

        try
        {
            string text = File.ReadAllText(definition.Path);
            return new SkillContent(definition, StripFrontmatter(text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Read one supporting file belonging to a skill.</summary>
    public string? ReadSupportingFile(string skillName, string relativePath)
    {
        SkillDefinition? definition = Find(skillName);
        if (definition is null)
            return null;

        string root = Path.GetDirectoryName(definition.Path)!;
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));

        // Containment check, not a courtesy: without it, "../../../../secrets.txt"
        // reads anything the process can reach.
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return File.Exists(candidate) ? File.ReadAllText(candidate) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Build the block that goes into the system prompt.
    ///
    /// Names and descriptions only, grouped by category, with an explicit instruction
    /// to load before acting. Erring towards loading is deliberate: a skill exists
    /// because the plain approach gets something wrong, so the cost of reading one
    /// unnecessarily is far lower than the cost of skipping one that mattered.
    /// </summary>
    public string BuildPromptSection(IReadOnlySet<string> availableTools)
    {
        List<SkillDefinition> visible = All
            .Where(s => s.ShouldShow(availableTools))
            .OrderBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (visible.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## Skills")
          .AppendLine()
          .AppendLine("These are instructions written for specific tasks. When one applies, load it")
          .AppendLine("with skill_view before you act. Err on the side of loading: a skill exists")
          .AppendLine("because the obvious approach gets something wrong.")
          .AppendLine();

        string? category = null;

        foreach (SkillDefinition skill in visible)
        {
            if (skill.Category != category)
            {
                category = skill.Category;
                if (category.Length > 0)
                    sb.AppendLine().Append(category).AppendLine(":");
            }

            sb.Append("- ").Append(skill.QualifiedName)
              .Append(": ").AppendLine(skill.Description);
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ discovery

    private List<SkillDefinition> Load()
    {
        List<IndexFile> files = Enumerate();

        if (TryLoadSnapshot(files) is { } cached)
            return cached;

        var skills = new List<SkillDefinition>();

        // One skill per qualified name, first root wins.
        //
        // Roots are searched in the order they were given, and the caller lists the user own
        // directory before the one that ships with the product. Without this the same name
        // found in both appears TWICE in the prompt index, which spends context on a
        // duplicate and asks the model to choose between two entries that look identical.
        // Found by the harness the moment a skill started shipping with the application.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (IndexFile file in files)
        {
            SkillDefinition? parsed = Parse(file);

            if (parsed is not null && claimed.Add(parsed.QualifiedName))
                skills.Add(parsed);
        }

        SaveSnapshot(files, skills);
        return skills;
    }

    private List<IndexFile> Enumerate()
    {
        var files = new List<IndexFile>();

        foreach (string root in _roots)
        {
            if (!Directory.Exists(root))
                continue;

            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string path in found)
            {
                try
                {
                    var info = new FileInfo(path);
                    files.Add(new IndexFile(path, root, info.LastWriteTimeUtc.Ticks, info.Length));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        // Stable order so the manifest comparison is a straight sequence check.
        return files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private SkillDefinition? Parse(IndexFile file)
    {
        string text;
        try
        {
            text = File.ReadAllText(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string folder = Path.GetDirectoryName(file.Path)!;
        string folderName = Path.GetFileName(folder);

        // The category is the folder between the root and the skill, if there is one.
        string relative = Path.GetRelativePath(file.Root, folder).Replace('\\', '/');
        int slash = relative.LastIndexOf('/');
        string category = slash > 0 ? relative[..slash] : string.Empty;

        Frontmatter matter = ReadFrontmatter(text);

        return new SkillDefinition(
            Name: matter.Name ?? folderName,
            // A skill with no description is still usable, but the model has nothing to
            // decide on, so say so rather than leaving the line blank.
            Description: matter.Description ?? "(no description)",
            Category: category,
            Path: file.Path,
            RequiresTools: matter.RequiresTools ?? [],
            FallbackForTools: matter.FallbackForTools ?? [],
            SupportingFiles: FindSupportingFiles(folder));
    }

    private static IReadOnlyList<string> FindSupportingFiles(string folder)
    {
        var files = new List<string>();

        // Only these four, matching the convention. An unbounded scan would pull in
        // build output and anything else that happens to sit beside the skill.
        foreach (string sub in new[] { "references", "templates", "scripts", "assets" })
        {
            string path = Path.Combine(folder, sub);
            if (!Directory.Exists(path))
                continue;

            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    files.Add(Path.GetRelativePath(folder, file).Replace('\\', '/'));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return files;
    }

    private static Frontmatter ReadFrontmatter(string text)
    {
        string normalised = text.ReplaceLineEndings("\n");

        if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
            return new Frontmatter();

        int end = normalised.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            return new Frontmatter();

        string yaml = normalised[4..end];

        try
        {
            return new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<Frontmatter>(yaml) ?? new Frontmatter();
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // A malformed frontmatter block should degrade to "no metadata", not make
            // the skill disappear: the body may still be exactly what the user wants.
            return new Frontmatter();
        }
    }

    private static string StripFrontmatter(string text)
    {
        string normalised = text.ReplaceLineEndings("\n");

        if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
            return text;

        int end = normalised.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            return text;

        // Skip past the closing marker and its newline.
        int bodyStart = normalised.IndexOf('\n', end + 4);
        return bodyStart < 0 ? string.Empty : normalised[(bodyStart + 1)..].TrimStart('\n');
    }

    // ------------------------------------------------------------------- snapshot

    private List<SkillDefinition>? TryLoadSnapshot(List<IndexFile> current)
    {
        if (!File.Exists(_snapshotPath))
            return null;

        try
        {
            Snapshot? snapshot = JsonSerializer.Deserialize<Snapshot>(
                File.ReadAllText(_snapshotPath));

            if (snapshot is null || snapshot.Version != SnapshotVersion)
                return null;

            // Exact manifest match, not a heuristic. Any added, removed, edited or
            // resized index file invalidates the whole snapshot -- cheaper than being
            // clever, and it cannot go stale silently.
            if (snapshot.Manifest.Count != current.Count)
                return null;

            for (int i = 0; i < current.Count; i++)
            {
                ManifestEntry entry = snapshot.Manifest[i];
                IndexFile file = current[i];

                if (!entry.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)
                    || entry.Ticks != file.Ticks
                    || entry.Length != file.Length)
                {
                    return null;
                }
            }

            return snapshot.Skills;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveSnapshot(List<IndexFile> files, List<SkillDefinition> skills)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);

            var snapshot = new Snapshot
            {
                Version = SnapshotVersion,
                Manifest = files.Select(f => new ManifestEntry
                {
                    Path = f.Path,
                    Ticks = f.Ticks,
                    Length = f.Length,
                }).ToList(),
                Skills = skills,
            };

            // Written via a temporary file: a half-written snapshot would be rejected
            // by the version check, but leaving one around serves no purpose.
            string temporary = _snapshotPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
            File.Move(temporary, _snapshotPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The snapshot is an optimisation. Failing to write it costs a rescan next
            // time and nothing else.
        }
    }

    private sealed record IndexFile(string Path, string Root, long Ticks, long Length);

    private sealed class Frontmatter
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public List<string>? RequiresTools { get; set; }

        public List<string>? FallbackForTools { get; set; }
    }

    private sealed class ManifestEntry
    {
        public string Path { get; set; } = string.Empty;

        public long Ticks { get; set; }

        public long Length { get; set; }
    }

    private sealed class Snapshot
    {
        public int Version { get; set; }

        public List<ManifestEntry> Manifest { get; set; } = [];

        public List<SkillDefinition> Skills { get; set; } = [];
    }
}
