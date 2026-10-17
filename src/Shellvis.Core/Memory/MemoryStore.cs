using System.Text;
using System.Text.RegularExpressions;
using Shellvis.Core.Config;

namespace Shellvis.Core.Memory;

/// <summary>Which of the two stores an entry belongs to.</summary>
public enum MemoryTarget
{
    /// <summary>Facts about the machine, the tools and the work. The larger store.</summary>
    Memory,

    /// <summary>Facts about the person: role, preferences, conventions, corrections.</summary>
    User,
}

/// <summary>The outcome of a memory write.</summary>
/// <param name="Succeeded">Whether anything changed.</param>
/// <param name="Message">What happened, in words, for the model and the transcript.</param>
public sealed record MemoryResult(bool Succeeded, string Message);

/// <summary>
/// Durable declarative facts, injected into every turn.
///
/// The second half of "get smarter over time", and the half this project was missing. It
/// had skills -- procedures, loaded on demand -- and nothing for facts. So a finding like
/// "free space here is reported by Get-PSDrive, and C: is the only fixed volume" had
/// nowhere to go but a skill, where it is only read if the model happens to load it. A fact
/// that is only sometimes read is not a fact the agent knows.
///
/// Modelled on Hermes' MemoryStore, including three properties that are easy to leave out
/// and expensive to leave out:
///
/// 1. <b>Content is scanned before it is accepted.</b> These entries go into the SYSTEM
///    PROMPT of every later turn. Text that reaches them came from a web page, a file, a
///    tool result or an MCP server -- so the store is an injection sink, and it is the most
///    valuable one in the application because what lands here is read first, every time,
///    for ever. Refused rather than sanitised.
/// 2. <b>The limit refuses rather than truncates.</b> Silently dropping the oldest entry
///    would make the agent forget something it was told to remember, with nothing said. The
///    write fails and the model is told the usage so it can replace an entry instead.
/// 3. <b>The prompt reads a snapshot frozen at load.</b> The system prompt is built once per
///    session so the provider's prefix cache keeps hitting; letting a mid-session write
///    change what the prompt says would invalidate that cache for every remaining round.
///    The write lands on disk and in the live state, and takes effect next session.
/// </summary>
public sealed class MemoryStore
{
    /// <summary>
    /// Between entries. A section sign because it does not occur in ordinary prose and
    /// needs no escaping, unlike a blank line, which any multi-line entry contains.
    /// </summary>
    private const string Delimiter = "\n§\n";

    /// <summary>
    /// Hermes' limits, kept rather than re-derived.
    ///
    /// The exact numbers matter less than that a bound exists: this text is prepended to
    /// every request for the life of the installation, so an unbounded store is a slowly
    /// growing tax on every single turn. These are small enough to stay cheap and large
    /// enough for a few dozen facts.
    /// </summary>
    private const int MemoryLimit = 2200;
    private const int UserLimit = 1375;

    /// <summary>
    /// Zero-width and direction-override characters. Present in text for exactly one
    /// reason at this point in a pipeline, and it is not a good one.
    /// </summary>
    private static readonly char[] Invisible =
    [
        '​', '‌', '‍', '⁠', '﻿',
        '‪', '‫', '‬', '‭', '‮',
    ];

    /// <summary>
    /// What must never enter a store that is read into the system prompt.
    ///
    /// Taken from Hermes' list and extended for Windows. Two families: instructions aimed
    /// at a later session's model, and payloads aimed at the user's secrets. Both would be
    /// read before anything the user says, in every session, which is why this list is
    /// worth more than its length suggests.
    /// </summary>
    private static readonly (string Pattern, string Name)[] Threats =
    [
        (@"ignore\s+(previous|all|above|prior)\s+instructions", "prompt injection"),
        (@"you\s+are\s+now\s+", "role hijack"),
        (@"do\s+not\s+tell\s+the\s+user", "concealment"),
        (@"system\s+prompt\s+override", "prompt override"),
        (@"disregard\s+(your|all|any)\s+(instructions|rules|guidelines)", "rule bypass"),

        // Exfiltration, in the shapes this machine can actually run.
        (@"(curl|wget|iwr|invoke-webrequest)[^\n]*\$?\{?\w*(KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL|API)",
            "credential exfiltration"),
        (@"(cat|type|get-content)\s+[^\n]*(\.env|credentials|\.netrc|\.npmrc|id_rsa)", "secret read"),

        // Persistence.
        (@"authorized_keys", "ssh backdoor"),
        (@"CurrentVersion\\+Run", "autostart persistence"),
        (@"\.shellvis[\\/]secrets", "secret store access"),
    ];

    private readonly Dictionary<MemoryTarget, List<string>> _live = new()
    {
        [MemoryTarget.Memory] = [],
        [MemoryTarget.User] = [],
    };

    /// <summary>
    /// What the system prompt was built from. Frozen deliberately -- see the type remarks.
    /// </summary>
    private readonly Dictionary<MemoryTarget, string> _snapshot = new()
    {
        [MemoryTarget.Memory] = string.Empty,
        [MemoryTarget.User] = string.Empty,
    };

    public MemoryStore()
    {
        foreach (MemoryTarget target in Enum.GetValues<MemoryTarget>())
        {
            _live[target] = ReadFile(PathFor(target));
            _snapshot[target] = Render(target, _live[target]);
        }
    }

    public static string Directory => Path.Combine(ShellvisPaths.Home, "memory");

    /// <summary>
    /// The block to put in the system prompt, or empty when nothing is remembered.
    /// </summary>
    public string PromptSection()
    {
        var sb = new StringBuilder();

        foreach (MemoryTarget target in Enum.GetValues<MemoryTarget>())
        {
            if (_snapshot[target] is { Length: > 0 } block)
                sb.AppendLine(block).AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Everything currently remembered, for the model to read and edit.</summary>
    public IReadOnlyList<string> Entries(MemoryTarget target) => _live[target];

    public string Usage(MemoryTarget target) =>
        $"{Joined(target).Length}/{Limit(target)} characters";

    /// <summary>Add a fact. Refuses a duplicate, a threat, or an entry that will not fit.</summary>
    public MemoryResult Add(MemoryTarget target, string? content)
    {
        string entry = (content ?? string.Empty).Trim();

        if (entry.Length == 0)
            return new MemoryResult(false, "error: a memory entry cannot be empty.");

        if (Screen(entry) is { } refusal)
            return new MemoryResult(false, refusal);

        return Mutate(target, entries =>
        {
            // Exact duplicates are a no-op rather than an error: a model re-learning
            // something it already knows is normal, and an error would send it looking for
            // a problem that does not exist.
            if (entries.Contains(entry, StringComparer.Ordinal))
                return "that is already remembered.";

            int total = string.Join(Delimiter, entries.Append(entry)).Length;

            if (total > Limit(target))
            {
                return $"error: {Usage(target)} used; this entry ({entry.Length} characters) "
                    + "would not fit. Replace or remove an existing entry first.";
            }

            entries.Add(entry);
            return $"remembered. {Usage(target)} used.";
        });
    }

    /// <summary>
    /// Replace the entry containing <paramref name="find"/>.
    ///
    /// Substring rather than exact match, because the model is asking to correct something
    /// it half remembers, and requiring the entry verbatim would mean reading it back first
    /// -- a round spent to change a sentence.
    /// </summary>
    public MemoryResult Replace(MemoryTarget target, string? find, string? replacement)
    {
        string needle = (find ?? string.Empty).Trim();
        string entry = (replacement ?? string.Empty).Trim();

        if (needle.Length == 0 || entry.Length == 0)
            return new MemoryResult(false, "error: replacing needs both the text to find and the new text.");

        if (Screen(entry) is { } refusal)
            return new MemoryResult(false, refusal);

        return Mutate(target, entries =>
        {
            int index = entries.FindIndex(e => e.Contains(needle, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                return $"error: nothing remembered contains '{needle}'.";

            string previous = entries[index];
            entries[index] = entry;

            if (string.Join(Delimiter, entries).Length > Limit(target))
            {
                entries[index] = previous;
                return $"error: that replacement does not fit. {Usage(target)} used.";
            }

            return $"replaced. {Usage(target)} used.";
        });
    }

    public MemoryResult Remove(MemoryTarget target, string? find)
    {
        string needle = (find ?? string.Empty).Trim();

        if (needle.Length == 0)
            return new MemoryResult(false, "error: removing needs the text to find.");

        return Mutate(target, entries =>
        {
            int index = entries.FindIndex(e => e.Contains(needle, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                return $"error: nothing remembered contains '{needle}'.";

            entries.RemoveAt(index);
            return $"forgotten. {Usage(target)} used.";
        });
    }

    /// <summary>
    /// Do the work under a cross-process lock, re-reading first.
    ///
    /// Two pills open at once is normal, and both write here. Without the re-read the
    /// second one would save a stale list and silently drop what the first remembered --
    /// the same lost-update the cron store had to be fixed for.
    /// </summary>
    private MemoryResult Mutate(MemoryTarget target, Func<List<string>, string> change)
    {
        using var mutex = new Mutex(false, @"Global\Shellvis.Memory");
        bool held = false;

        try
        {
            try
            {
                held = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                // A process died holding it. Treated as acquired: refusing for ever after a
                // crash would make memory unusable until the machine is rebooted.
                held = true;
            }

            string file = PathFor(target);
            List<string> entries = ReadFile(file);

            string message = change(entries);

            if (message.StartsWith("error", StringComparison.Ordinal))
                return new MemoryResult(false, message);

            _live[target] = entries;
            WriteFile(file, entries);

            return new MemoryResult(true, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MemoryResult(false, $"error: could not write memory: {ex.Message}");
        }
        finally
        {
            if (held)
                mutex.ReleaseMutex();
        }
    }

    private static string PathFor(MemoryTarget target) =>
        Path.Combine(Directory, target == MemoryTarget.User ? "USER.md" : "MEMORY.md");

    private static int Limit(MemoryTarget target) =>
        target == MemoryTarget.User ? UserLimit : MemoryLimit;

    private string Joined(MemoryTarget target) => string.Join(Delimiter, _live[target]);

    private static string Render(MemoryTarget target, List<string> entries)
    {
        if (entries.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        sb.AppendLine(target == MemoryTarget.User
            ? "## What you know about this user"
            : "## What you know about this machine and this work");

        foreach (string entry in entries)
            sb.Append("- ").AppendLine(entry.ReplaceLineEndings(" "));

        return sb.ToString().TrimEnd();
    }

    /// <summary>Reject content that must not reach a system prompt. Null means it may.</summary>
    private static string? Screen(string content)
    {
        foreach (char c in Invisible)
        {
            if (content.Contains(c, StringComparison.Ordinal))
            {
                return $"error: refused, the text contains an invisible character "
                    + $"(U+{(int)c:X4}). Memory is read into the system prompt, so text that "
                    + "hides part of itself cannot be accepted.";
            }
        }

        foreach ((string pattern, string name) in Threats)
        {
            if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            {
                return $"error: refused, the text matches a {name} pattern. Memory is read "
                    + "into the system prompt of every later session and must not carry "
                    + "instructions or payloads.";
            }
        }

        return null;
    }

    private static List<string> ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            return [.. File.ReadAllText(path, Encoding.UTF8)
                .Split(Delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void WriteFile(string path, List<string> entries)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Written aside and moved, so an interrupted write cannot leave a half file that
        // reads back as fewer memories than were saved.
        string temporary = path + ".new";
        File.WriteAllText(temporary, string.Join(Delimiter, entries), Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }
}
