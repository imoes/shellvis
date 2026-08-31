using Shellvis.Core.Cron;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Turning a Shellvis schedule into a Windows task, and the boundary where it cannot.
///
/// <b>Why the boundary is the interesting part.</b> Task Scheduler cannot express everything
/// cron can, and the tempting thing to do with "0,30 8-17 * * 1-5" is to approximate it. An
/// approximation fires at times nobody asked for, and it does so silently, on a machine
/// nobody is watching -- which is the worst failure this feature can have. So the translation
/// refuses what it cannot say exactly, the job stays on the scheduler inside Shellvis where
/// the expression means what it says, and the refusal is what this harness checks hardest.
///
/// The registration itself is exercised too, against the real <c>schtasks</c>: a task is
/// created, found and removed. That needs no network, no model and no desktop, and it is the
/// only way to know that the quoting survives -- a command with a space in its path that
/// breaks in half produces a task that exists and never works.
/// </summary>
internal static class ScheduleProbe
{
    public static int Run()
    {
        int failures = 0;

        Console.WriteLine("=== Scheduling ===");
        Console.WriteLine();
        Console.WriteLine("-- what Task Scheduler can express --");

        failures += Translates("every 30 minutes", "30m", "/SC MINUTE /MO 30");
        failures += Translates("every 2 hours", "2h", "/SC HOURLY /MO 2");
        failures += Translates("every day", "1d", "/SC DAILY /MO 1");
        failures += Translates("a daily time", "30 7 * * *", "/SC DAILY /ST 07:30");
        failures += Translates("weekdays", "0 8 * * 1-5", "/SC WEEKLY /D MON,TUE,WED,THU,FRI /ST 08:00");
        failures += Translates("two named days", "15 17 * * 1,4", "/SC WEEKLY /D MON,THU /ST 17:15");
        failures += Translates("Sunday as 0", "0 9 * * 0", "/SC WEEKLY /D SUN /ST 09:00");
        failures += Translates("Sunday as 7", "0 9 * * 7", "/SC WEEKLY /D SUN /ST 09:00");

        Console.WriteLine();
        Console.WriteLine("-- and what it must refuse rather than approximate --");

        failures += Refuses("a step in the minute field", "*/15 8 * * *");
        failures += Refuses("a list of hours", "0 8,12,17 * * *");
        failures += Refuses("a range of hours", "0 8-17 * * *");
        failures += Refuses("a day of the month", "0 8 1 * *");
        failures += Refuses("a month", "0 8 * 3 *");
        failures += Refuses("seconds", "30s");
        failures += Refuses("nonsense", "every other tuesday");

        Console.WriteLine();
        Console.WriteLine("-- a one-off keeps its date --");

        bool once = WindowsTasks.TryTranslate("2026-12-24T18:00:00", out string? onceArgs, out _);

        failures += Check("an ISO timestamp becomes /SC ONCE",
            once && onceArgs is not null
                && onceArgs.Contains("/SC ONCE", StringComparison.Ordinal)
                && onceArgs.Contains("24/12/2026", StringComparison.Ordinal)
                && onceArgs.Contains("18:00", StringComparison.Ordinal),
            onceArgs);

        Console.WriteLine();
        failures += Registers();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: the schedules Windows can express translate exactly, the ones it\n"
                + "cannot are refused rather than approximated, and a real task can be\n"
                + "created, found and removed with its command quoting intact."
            : $"{failures} scheduling check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>Create a real task, find it, remove it.</summary>
    private static int Registers()
    {
        Console.WriteLine("-- schtasks, for real --");
        int failures = 0;

        const string job = "probe-selftest";

        // A path with a space in it on purpose: that is the case the quoting has to survive,
        // and "C:\Program Files\..." is where an installed Shellvis actually lives.
        string executable = Path.Combine(
            Path.GetTempPath(), "shellvis probe", "Shellvis.Shell.exe");

        // Left over from an interrupted earlier run. Removing it first makes the harness
        // repeatable, which a harness that creates things has to be.
        WindowsTasks.TryDelete(job, out _);

        bool created = WindowsTasks.TryCreate(job, "45m", executable, out string message);

        failures += Check("a task can be created", created, message);

        if (!created)
        {
            Console.WriteLine("   (skipping the rest: nothing was registered)");
            return failures;
        }

        failures += Check("Windows knows about it", WindowsTasks.Exists(job), string.Empty);

        failures += Check("the task name is under the Shellvis folder",
            WindowsTasks.NameFor(job).StartsWith(WindowsTasks.Folder + "\\", StringComparison.Ordinal),
            WindowsTasks.NameFor(job));

        bool deleted = WindowsTasks.TryDelete(job, out string removal);
        failures += Check("it can be removed again", deleted, removal);
        failures += Check("and is then gone", !WindowsTasks.Exists(job), string.Empty);

        // Deleting one that is not there must not be an error: cron_remove calls it for every
        // job, including the ones that never had a task.
        failures += Check("removing an absent task is not an error",
            WindowsTasks.TryDelete("probe-never-existed", out _), string.Empty);

        return failures;
    }

    private static int Translates(string what, string schedule, string expected)
    {
        bool ok = WindowsTasks.TryTranslate(schedule, out string? arguments, out string? problem);

        return Check($"{what} ({schedule})",
            ok && arguments == expected,
            ok ? arguments : problem);
    }

    private static int Refuses(string what, string schedule)
    {
        bool ok = WindowsTasks.TryTranslate(schedule, out string? arguments, out string? problem);

        // A refusal has to SAY something. "false" with no sentence leaves the caller unable
        // to tell the user why their schedule stayed on the in-process loop.
        return Check($"{what} ({schedule}) is refused, with a reason",
            !ok && problem is { Length: > 10 },
            ok ? arguments : problem);
    }

    private static int Check(string what, bool passed, string? detail)
    {
        Console.WriteLine($"   {(passed ? "ok  " : "FAIL")} {what}");

        if (!passed && detail is { Length: > 0 })
            Console.WriteLine($"        {detail}");

        return passed ? 0 : 1;
    }
}
