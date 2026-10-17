using System.Text;
using Shellvis.Core.Config;

namespace Shellvis.Core.Skills;

/// <summary>
/// Writes a SKILL.md.
///
/// Extracted so the <c>skill_manage</c> tool and the post-turn reflection produce byte-
/// identical files. Two writers would drift, and the thing they would drift on is the YAML
/// frontmatter -- which the index parses, so a divergence shows up as a skill that exists
/// on disk and cannot be found.
/// </summary>
public static class SkillWriter
{
    /// <summary>Longest body accepted. A skill is instructions, not a transcript.</summary>
    public const int MaxBodyLength = 8000;

    /// <summary>
    /// Patterns that must never end up in a skill.
    ///
    /// A skill body is written by a model out of whatever it just saw, and what it just saw
    /// may have included a token in a command line or an environment dump. The skill file
    /// is plain text on disk, gets read into the prompt of every later session, and is the
    /// sort of thing people copy between machines -- exactly the path this project keeps
    /// secrets off with the ${VAR} rule. So a body that looks like it carries a credential
    /// is refused rather than filtered: silently removing part of an instruction would
    /// leave a skill that reads as complete and is not.
    /// </summary>
    private static readonly string[] SecretMarkers =
    [
        "sk-", "ghp_", "github_pat_", "xoxb-", "xoxp-", "AKIA", "-----BEGIN",
        "api_key=", "apikey=", "password=", "passwd=", "bearer ",
    ];

    /// <summary>
    /// Write or replace a skill.
    /// </summary>
    /// <returns>A sentence for the transcript, whether it worked or not.</returns>
    public static string Write(
        SkillIndex index,
        string name,
        string? description,
        string? body,
        string? category = null)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (string.IsNullOrWhiteSpace(name))
            return "error: a skill name is required.";

        // The name becomes a folder, so anything path-like has to be refused rather than
        // sanitised: quietly renaming a skill would confuse every later reference to it.
        if (name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return "error: a skill name may not contain path characters.";

        if (string.IsNullOrWhiteSpace(description))
            return "error: a description is required. It is the only part the model sees "
                + "before deciding whether to load the skill, so it must say WHEN to use it.";

        if (string.IsNullOrWhiteSpace(body))
            return "error: the skill body is required.";

        if (body.Length > MaxBodyLength)
            return $"error: the skill body is {body.Length} characters; the limit is {MaxBodyLength}.";

        if ((FindSecretMarker(body) ?? FindSecretMarker(description)) is { } marker)
            return $"error: refused, the text contains '{marker}' which looks like a credential.";

        string folder = category is { Length: > 0 }
            ? Path.Combine(ShellvisPaths.SkillsDirectory, Sanitise(category), name)
            : Path.Combine(ShellvisPaths.SkillsDirectory, name);

        try
        {
            Directory.CreateDirectory(folder);

            string file = Path.Combine(folder, "SKILL.md");
            bool existed = File.Exists(file);

            // Frontmatter is written rather than appended so an update replaces the
            // metadata cleanly instead of accumulating stale blocks.
            var content = new StringBuilder();
            content.AppendLine("---")
                   .Append("name: ").AppendLine(name)
                   .Append("description: ").AppendLine(Quote(description))
                   .AppendLine("---")
                   .AppendLine()
                   .Append(body.TrimEnd())
                   .AppendLine();

            File.WriteAllText(file, content.ToString(), Encoding.UTF8);

            // The snapshot manifest would reject itself on the next read anyway, but
            // clearing it now means this session sees the new skill immediately.
            index.Invalidate();

            return $"{(existed ? "updated" : "wrote")} skill '{name}' at {file}. It is "
                + "available to skill_view now and will appear in the prompt index next "
                + "session.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"error: could not write the skill: {ex.Message}";
        }
    }

    private static string? FindSecretMarker(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        foreach (string marker in SecretMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return marker;
        }

        return null;
    }

    private static string Sanitise(string value) =>
        string.Concat(value.Split(Path.GetInvalidFileNameChars())).Replace('\\', '/').Trim('/');

    /// <summary>
    /// Quote a description for YAML if it contains anything that would break the scalar. A
    /// colon in a description is common and unquoted would silently truncate the value at
    /// the colon.
    /// </summary>
    private static string Quote(string value) =>
        value.IndexOfAny([':', '#', '\n', '"']) >= 0
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal).ReplaceLineEndings(" ") + "\""
            : value;
}
