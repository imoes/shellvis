using System.Text.Json;
using System.Text.Json.Serialization;
using Shellvis.Core.Config;

namespace Shellvis.Core.Cron;

/// <summary>
/// One scheduled job, as it appears in jobs.json.
/// </summary>
/// <param name="Name">Identity. Used for logging and for addressing the job.</param>
/// <param name="Prompt">What the agent is asked to do.</param>
/// <param name="Schedule">Interval, cron expression or timestamp.</param>
/// <param name="Repeat">
/// Whether it runs again after the first time. A cron expression with repeat off is a
/// perfectly reasonable "next Monday at 7" and is why this is separate from the
/// schedule form.
/// </param>
/// <param name="Skills">Skills to preload, so a scheduled run has its instructions.</param>
/// <param name="Model">Model override, for a cheaper model on a frequent job.</param>
/// <param name="Enabled">Off keeps the definition without running it.</param>
/// <param name="LastRun">When it last ran, which an interval counts from.</param>
/// <param name="LastResult">A one-line record of how it went.</param>
public sealed record CronJob(
    string Name,
    string Prompt,
    string Schedule,
    bool Repeat = true,
    IReadOnlyList<string>? Skills = null,
    string? Model = null,
    bool Enabled = true,
    DateTimeOffset? LastRun = null,
    string? LastResult = null)
{
    /// <summary>
    /// The parsed schedule, or null when it does not parse.
    ///
    /// Not cached in a field: the record is serialised, and a computed member cannot
    /// drift out of step with the text the way a stored copy would.
    /// </summary>
    [JsonIgnore]
    public CronSchedule? Parsed =>
        CronSchedule.TryParse(Schedule, out CronSchedule? parsed, out _) ? parsed : null;

    /// <summary>When this job is next due, given the clock.</summary>
    public DateTimeOffset? NextDue(DateTimeOffset now)
    {
        if (!Enabled)
            return null;

        CronSchedule? schedule = Parsed;

        if (schedule is null)
            return null;

        // A one-shot that has already run is finished, whatever its schedule says.
        if (!Repeat && LastRun is not null)
            return null;

        return schedule.Next(now, LastRun);
    }

    public override string ToString()
    {
        string state = Enabled ? string.Empty : " (disabled)";
        string schedule = Parsed?.Describe() ?? $"INVALID: {Schedule}";
        string last = LastRun is null
            ? "never run"
            : $"last {LastRun.Value.LocalDateTime:dd.MM. HH:mm}";

        return $"{Name}{state}  {schedule}  {last}";
    }
}

/// <summary>
/// Reads and writes jobs.json.
///
/// Guarded by a named mutex rather than a file lock. Two Shellvis windows are an
/// ordinary situation -- the app is a pill someone may open twice -- and both of them
/// write this file to record run times. A lost update there means a job runs twice or
/// not at all, and the plan calls for exactly this mechanism because there is no fcntl
/// on Windows.
/// </summary>
public sealed class CronStore
{
    /// <summary>
    /// Cross-process lock name. Local, not Global: the jobs file lives in the user
    /// profile, so the scope of contention is this user's sessions.
    /// </summary>
    private const string MutexName = "Local\\Shellvis.Cron.Jobs";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public CronStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(ShellvisPaths.Home, "jobs.json");
    }

    public string Path { get; }

    /// <summary>Problems found the last time the file was read.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>
    /// Load the jobs. A missing file is an empty list, not an error.
    /// </summary>
    public IReadOnlyList<CronJob> Load()
    {
        var warnings = new List<string>();

        try
        {
            if (!File.Exists(Path))
            {
                Warnings = warnings;
                return [];
            }

            string text = WithLock(() => File.ReadAllText(Path));

            List<CronJob>? jobs = JsonSerializer.Deserialize<List<CronJob>>(text, Json);

            if (jobs is null)
            {
                Warnings = warnings;
                return [];
            }

            var valid = new List<CronJob>();

            foreach (CronJob job in jobs)
            {
                if (string.IsNullOrWhiteSpace(job.Name))
                {
                    warnings.Add("a job with no name was ignored.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(job.Prompt))
                {
                    warnings.Add($"job '{job.Name}' has no prompt and was ignored.");
                    continue;
                }

                if (!CronSchedule.TryParse(job.Schedule, out _, out string? problem))
                {
                    // Kept in the list but never due, so the definition is not silently
                    // deleted by the next save.
                    warnings.Add($"job '{job.Name}': {problem}");
                }

                if (valid.Any(j => j.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    // Names address jobs, so a duplicate makes "disable X" ambiguous.
                    warnings.Add($"job '{job.Name}' is defined more than once; the later one was ignored.");
                    continue;
                }

                valid.Add(job);
            }

            Warnings = warnings;
            return valid;
        }
        catch (JsonException ex)
        {
            warnings.Add($"{Path} is not valid JSON ({ex.Message}); no jobs were loaded.");
            Warnings = warnings;
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"could not read {Path}: {ex.Message}");
            Warnings = warnings;
            return [];
        }
    }

    /// <summary>Write the jobs back, atomically.</summary>
    public void Save(IReadOnlyList<CronJob> jobs)
    {
        ShellvisPaths.EnsureCreated();

        string text = JsonSerializer.Serialize(jobs, Json);

        WithLock<object?>(() =>
        {
            // Temp file then move: a crash mid-write must not leave a truncated jobs
            // file that fails to parse and loses every schedule.
            string temporary = Path + ".tmp";
            File.WriteAllText(temporary, text);
            File.Move(temporary, Path, overwrite: true);
            return null;
        });
    }

    /// <summary>
    /// Record that a job ran.
    ///
    /// Re-reads the file inside the lock rather than writing a cached list back. Another
    /// window may have added a job in the meantime, and saving a stale snapshot would
    /// delete it -- the classic lost update, and the reason the mutex exists at all.
    /// </summary>
    public void RecordRun(string name, DateTimeOffset when, string result)
    {
        WithLock<object?>(() =>
        {
            List<CronJob> jobs = [.. LoadUnlocked()];

            for (int i = 0; i < jobs.Count; i++)
            {
                if (!jobs[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                jobs[i] = jobs[i] with
                {
                    LastRun = when,
                    LastResult = result.Length > 300 ? result[..300] + " ..." : result,
                };
            }

            string text = JsonSerializer.Serialize(jobs, Json);
            string temporary = Path + ".tmp";
            File.WriteAllText(temporary, text);
            File.Move(temporary, Path, overwrite: true);

            return null;
        });
    }

    private IReadOnlyList<CronJob> LoadUnlocked()
    {
        if (!File.Exists(Path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<CronJob>>(File.ReadAllText(Path), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Run an operation holding the cross-process mutex.
    ///
    /// An abandoned mutex is treated as acquired: it means another process died holding
    /// it, and refusing to proceed would leave cron permanently broken until a reboot.
    /// </summary>
    private static T WithLock<T>(Func<T> operation)
    {
        using var mutex = new Mutex(false, MutexName);
        bool held = false;

        try
        {
            try
            {
                held = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            return operation();
        }
        finally
        {
            if (held)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }
        }
    }
}
