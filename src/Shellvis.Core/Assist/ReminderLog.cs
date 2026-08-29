using System.Globalization;
using System.Text.Json;

using Shellvis.Core.Config;

namespace Shellvis.Core.Assist;

/// <summary>
/// What has already been said, so it is not said again.
///
/// <b>Why this is code and not an instruction.</b> A reminder job that runs every five
/// minutes sees the same 11:00 meeting on every tick for the whole hour before it. Telling
/// the model "only mention it once" fails in the way this project has now measured three
/// times: an instruction competing with the task at hand loses, and here it would lose
/// silently, because a repeated reminder looks exactly like a reminder. Worse, each run is a
/// fresh session with no memory of the last one, so there is nothing for it to remember
/// with even in principle.
///
/// So the suppression happens in the tool, before the model sees anything: what has been
/// announced is simply not in the result.
///
/// <b>Why a file and not the job record.</b> jobs.json holds definitions the user edits by
/// hand. Writing bookkeeping into it would rewrite their file on a timer and put a growing
/// list of announced ids in the middle of their configuration.
/// </summary>
public sealed class ReminderLog
{
    /// <summary>How long an entry is remembered.</summary>
    /// <remarks>
    /// Long enough that a meeting is not announced twice, short enough that a weekly series
    /// with the same id is announced again next week. Also what keeps the file from growing
    /// without bound, since expired entries are dropped on every write.
    /// </remarks>
    private static readonly TimeSpan Remember = TimeSpan.FromHours(20);

    private readonly string _path;

    /// <summary>Serialises writers across processes: two pills open at once is normal.</summary>
    /// <remarks>
    /// The same treatment jobs.json needs, and for the same reason: this project already had
    /// a lost update there. An ABANDONED mutex counts as acquired, or a crash would leave
    /// reminders broken until the machine was restarted.
    /// </remarks>
    private static readonly Mutex Gate = new(false, "Global\\Shellvis.Reminders");

    public ReminderLog(string? path = null)
    {
        _path = path ?? Path.Combine(ShellvisPaths.Home, "reminded.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    /// <summary>
    /// Keep only what has not been announced, and record the rest as announced.
    ///
    /// One call, doing both, on purpose. Two calls would leave a window in which a result
    /// was returned and not yet recorded, and a job that crashed in between would announce
    /// the same meeting for the rest of the morning.
    /// </summary>
    public IReadOnlyList<T> Fresh<T>(IEnumerable<T> candidates, Func<T, string> keyOf)
    {
        var kept = new List<T>();

        Held(() =>
        {
            Dictionary<string, DateTimeOffset> seen = Read();
            DateTimeOffset now = DateTimeOffset.Now;

            foreach (T candidate in candidates)
            {
                string key = keyOf(candidate);

                if (key.Length == 0)
                    continue;

                if (seen.TryGetValue(key, out DateTimeOffset when) && now - when < Remember)
                    continue;

                seen[key] = now;
                kept.Add(candidate);
            }

            // Expiry happens here rather than on a timer: the only moment the file is open
            // is the only moment worth spending on tidying it.
            foreach (string stale in seen
                .Where(e => now - e.Value >= Remember)
                .Select(e => e.Key)
                .ToList())
            {
                seen.Remove(stale);
            }

            if (kept.Count > 0 || seen.Count > 0)
                Write(seen);
        });

        return kept;
    }

    /// <summary>Whether one thing has already been announced, without recording it.</summary>
    public bool AlreadySaid(string key)
    {
        bool said = false;

        Held(() =>
        {
            said = Read().TryGetValue(key, out DateTimeOffset when)
                && DateTimeOffset.Now - when < Remember;
        });

        return said;
    }

    /// <summary>Forget everything. For a harness, and for a user who wants it said again.</summary>
    public void Clear() => Held(() =>
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (IOException)
        {
            // A file that cannot be deleted expires on its own within the day.
        }
    });

    private Dictionary<string, DateTimeOffset> Read()
    {
        try
        {
            if (!File.Exists(_path))
                return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

            Dictionary<string, string>? raw = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_path));

            if (raw is null)
                return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

            var parsed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

            foreach ((string key, string value) in raw)
            {
                if (DateTimeOffset.TryParse(
                        value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                        out DateTimeOffset when))
                {
                    parsed[key] = when;
                }
            }

            return parsed;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt log means everything gets announced once more. That is the safe
            // direction: the alternative failure, silently suppressing a reminder, is the
            // one that costs someone a meeting.
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, DateTimeOffset> seen)
    {
        try
        {
            var text = seen.ToDictionary(
                e => e.Key,
                e => e.Value.ToString("O", CultureInfo.InvariantCulture),
                StringComparer.Ordinal);

            // Through a sibling file and a move, so a crash mid-write cannot leave a
            // half-written log that then reads as empty.
            string temporary = _path + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(
                text, new JsonSerializerOptions { WriteIndented = true }));

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to record means saying it twice, which is a nuisance. Failing the
            // whole reminder because bookkeeping did not stick would be worse.
        }
    }

    private static void Held(Action action)
    {
        bool acquired = false;

        try
        {
            try
            {
                acquired = Gate.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                // The holder died. The lock is ours and the file is whatever they left,
                // which the reader already treats as recoverable.
                acquired = true;
            }

            action();
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    Gate.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not held by this thread. Nothing to release and nothing to report.
                }
            }
        }
    }
}
