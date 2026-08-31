using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Shellvis.Core.Cron;

/// <summary>
/// Registering a job with the Windows Task Scheduler.
///
/// <b>Why Windows and not only the loop inside Shellvis.</b> The in-process scheduler only
/// runs while Shellvis runs. A briefing due at eight does not happen if the machine was
/// restarted at seven, and nothing in Windows shows that anything is scheduled at all -- Task
/// Scheduler is empty, because nothing was ever registered. A real task fixes both: it fires
/// whether or not Shellvis is open, it is visible and editable in a tool the user already
/// has, and it survives a reinstall of nothing but this application.
///
/// <b>schtasks rather than the COM API.</b> The command-line tool is stable, it is present on
/// every Windows, and every one of its arguments can be read back out of an error message.
/// The COM interface would add a type library, an apartment and a class of failure that is
/// far harder to explain to whoever has to fix a task that did not fire.
/// </summary>
public static class WindowsTasks
{
    /// <summary>
    /// The folder every Shellvis task lives in.
    ///
    /// A folder rather than a name prefix, so somebody opening Task Scheduler sees what this
    /// application put there, in one place, and can delete the lot without hunting.
    /// </summary>
    public const string Folder = @"\Shellvis";

    /// <summary>The full task name for a job.</summary>
    public static string NameFor(string job) => $@"{Folder}\{job}";

    /// <summary>
    /// Turn a Shellvis schedule into schtasks arguments, or explain why it will not go.
    ///
    /// <b>The boundary is real and is stated rather than papered over.</b> A five-field cron
    /// expression can say things Task Scheduler cannot: "every 15 minutes past the hour on
    /// weekdays in March" has no equivalent. Rather than approximate it -- which would fire at
    /// the wrong times and look like a bug in the job -- the translation refuses, and the
    /// caller leaves that job on the in-process scheduler, where the expression means exactly
    /// what it says.
    /// </summary>
    public static bool TryTranslate(string schedule, out string? arguments, out string? problem)
    {
        arguments = null;
        problem = null;

        if (!CronSchedule.TryParse(schedule, out CronSchedule? parsed, out problem) || parsed is null)
            return false;

        switch (parsed.Kind)
        {
            case ScheduleKind.Interval:
                return TryInterval(parsed, out arguments, out problem);

            case ScheduleKind.Once:
                arguments = string.Create(
                    CultureInfo.InvariantCulture,
                    $"/SC ONCE /SD {parsed.At:dd/MM/yyyy} /ST {parsed.At:HH:mm}");

                return true;

            default:
                return TryCron(schedule, out arguments, out problem);
        }
    }

    private static bool TryInterval(CronSchedule parsed, out string? arguments, out string? problem)
    {
        arguments = null;
        problem = null;

        // The text is what was parsed, and it is the shortest reliable way back to the unit:
        // the parser has already established that it is a number followed by s, m, h or d.
        string text = parsed.Text.Trim().ToLowerInvariant();
        char unit = text[^1];

        if (!int.TryParse(text[..^1], out int every) || every <= 0)
        {
            problem = $"'{parsed.Text}' is not an interval Task Scheduler can take.";
            return false;
        }

        switch (unit)
        {
            case 's':
                // Windows counts in minutes at the finest. A job that wants seconds wants the
                // in-process loop, and saying so is better than silently rounding to a minute.
                problem = "Task Scheduler's finest interval is one minute; a job in seconds "
                    + "stays on the scheduler inside Shellvis.";

                return false;

            case 'm' when every <= 1439:
                arguments = $"/SC MINUTE /MO {every}";
                return true;

            case 'h' when every <= 23:
                arguments = $"/SC HOURLY /MO {every}";
                return true;

            case 'd' when every <= 365:
                arguments = $"/SC DAILY /MO {every}";
                return true;

            default:
                problem = $"'{parsed.Text}' is outside what Task Scheduler accepts for that unit.";
                return false;
        }
    }

    /// <summary>
    /// The two cron shapes Task Scheduler can express: a daily time, and a weekly time.
    /// </summary>
    private static bool TryCron(string schedule, out string? arguments, out string? problem)
    {
        arguments = null;
        problem = null;

        string[] f = schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (f.Length != 5)
        {
            problem = $"'{schedule}' is not a five-field cron expression.";
            return false;
        }

        if (!int.TryParse(f[0], out int minute) || minute is < 0 or > 59
            || !int.TryParse(f[1], out int hour) || hour is < 0 or > 23)
        {
            problem = "Task Scheduler needs a single hour and minute. A cron expression with "
                + "a list, a step or a range in those fields stays on the scheduler inside "
                + "Shellvis, where it means exactly what it says.";

            return false;
        }

        if (f[2] != "*" || f[3] != "*")
        {
            problem = "a day-of-month or month restriction cannot be expressed here; the job "
                + "stays on the scheduler inside Shellvis.";

            return false;
        }

        string time = string.Create(CultureInfo.InvariantCulture, $"/ST {hour:00}:{minute:00}");

        if (f[4] == "*")
        {
            arguments = $"/SC DAILY {time}";
            return true;
        }

        if (Weekdays(f[4]) is { Length: > 0 } days)
        {
            arguments = $"/SC WEEKLY /D {days} {time}";
            return true;
        }

        problem = $"'{f[4]}' is not a day-of-week list Task Scheduler can take.";
        return false;
    }

    /// <summary>A cron day-of-week field as schtasks day names, or empty when it cannot be.</summary>
    private static string Weekdays(string field)
    {
        string[] names = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
        var days = new List<string>();

        foreach (string part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Contains('-', StringComparison.Ordinal))
            {
                string[] ends = part.Split('-');

                if (ends.Length != 2
                    || !int.TryParse(ends[0], out int from) || !int.TryParse(ends[1], out int to)
                    || from is < 0 or > 7 || to is < 0 or > 7 || to < from)
                {
                    return string.Empty;
                }

                for (int d = from; d <= to; d++)
                    Add(days, names[d % 7]);

                continue;
            }

            if (!int.TryParse(part, out int single) || single is < 0 or > 7)
                return string.Empty;

            Add(days, names[single % 7]);
        }

        return string.Join(',', days);

        static void Add(List<string> into, string day)
        {
            if (!into.Contains(day))
                into.Add(day);
        }
    }

    /// <summary>
    /// Create or replace the task for a job.
    ///
    /// <c>/F</c> replaces an existing task of the same name rather than failing. That is the
    /// right behaviour here because the job is the source of truth: if its schedule changed,
    /// the task must change with it, and a half-updated pair is worse than either.
    /// </summary>
    public static bool TryCreate(string job, string schedule, string executable, out string message)
    {
        if (!TryTranslate(schedule, out string? when, out string? problem))
        {
            message = problem ?? "that schedule cannot be expressed as a Windows task.";
            return false;
        }

        // Quoted twice on purpose. schtasks takes the whole command as one argument, and the
        // path inside it needs quotes of its own or a Program Files path breaks in half.
        string command = $"\"\\\"{executable}\\\" {ScheduledRunArgument} \\\"{job}\\\"\"";

        return Run(
            $"/Create /F /TN \"{NameFor(job)}\" /TR {command} {when}",
            out message);
    }

    /// <summary>Remove the task for a job. A task that is not there is not an error.</summary>
    /// <remarks>
    /// <b>Asked, not inferred from the message.</b> The first version read schtasks' answer and
    /// looked for "cannot find" -- which works on an English Windows and fails on every other
    /// one. This machine answers in German, and the harness caught it: removing a task that
    /// was never there was reported as a failure, which would have told a user to go and
    /// delete something in Task Scheduler that does not exist. Localised output is data for a
    /// human, not a control flow.
    /// </remarks>
    public static bool TryDelete(string job, out string message)
    {
        if (!Exists(job))
        {
            message = "there was no Windows task for it.";
            return true;
        }

        return Run($"/Delete /F /TN \"{NameFor(job)}\"", out message);
    }

    /// <summary>Whether Windows has a task for this job.</summary>
    public static bool Exists(string job) => Run($"/Query /TN \"{NameFor(job)}\"", out _);

    /// <summary>The switch the task passes to name the job. Mirrors the shell's own constant.</summary>
    private const string ScheduledRunArgument = "--job";

    private static bool Run(string arguments, out string message)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                message = "schtasks could not be started.";
                return false;
            }

            var output = new StringBuilder();
            output.Append(process.StandardOutput.ReadToEnd());
            output.Append(process.StandardError.ReadToEnd());

            // Bounded, because a task that will not be created is a case somebody has to read
            // about, and schtasks answers a bad argument with its entire usage screen.
            if (!process.WaitForExit(20_000))
            {
                message = "schtasks did not finish within twenty seconds.";
                return false;
            }

            message = Clip(output.ToString());

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // Deliberately broad: this launches a process, and a machine with a policy
            // against schtasks, a redirected PATH or a locked-down account fails in ways not
            // worth enumerating. What matters is that the caller can still write the job.
            message = ex.Message;
            return false;
        }
    }

    private static string Clip(string text)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();

        while (flat.Contains("  ", StringComparison.Ordinal))
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);

        return flat.Length <= 300 ? flat : flat[..300] + "...";
    }
}
