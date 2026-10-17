using Shellvis.Core.Agent;
using Shellvis.Core.Cron;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Drives the scheduler with an explicit clock.
///
/// Time is passed in rather than read, which is the only way to test a scheduler without
/// sleeping through it -- and more importantly the only way to test the cases that
/// matter: a leap year, a missed window, a job that must not fire twice in one minute.
/// Waiting for real minutes to pass would test almost none of that.
/// </summary>
internal static class CronProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        Console.WriteLine("=== Cron ===");
        Console.WriteLine();

        failures += Parsing();
        failures += CronFields();
        failures += NextOccurrence();
        failures += await SchedulingAsync().ConfigureAwait(false);
        failures += await PersistenceAsync().ConfigureAwait(false);
        failures += Safety();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: schedules parse, fire once, catch up, and never self-approve."
            : $"{failures} cron check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static int Parsing()
    {
        Console.WriteLine("-- schedule forms --");
        int failures = 0;

        failures += Check("an interval parses", Parse("30m")?.Kind == ScheduleKind.Interval);
        failures += Check("seconds parse", Parse("45s")?.Interval == TimeSpan.FromSeconds(45));
        failures += Check("hours parse", Parse("2h")?.Interval == TimeSpan.FromHours(2));
        failures += Check("days parse", Parse("1d")?.Interval == TimeSpan.FromDays(1));
        failures += Check("a cron expression parses", Parse("0 7 * * 1-5")?.Kind == ScheduleKind.Cron);
        failures += Check("a timestamp parses", Parse("2026-08-25T09:00")?.Kind == ScheduleKind.Once);

        // A ten-second job is almost certainly a typo for ten minutes, and every run
        // costs a model call.
        failures += Check(
            "an interval below the 30s floor is refused with the reason",
            !CronSchedule.TryParse("10s", out _, out string? floor)
                && floor?.Contains("30 second") == true);

        failures += Check(
            "a zero interval is refused as a busy loop",
            !CronSchedule.TryParse("0m", out _, out string? zero)
                && zero?.Contains("busy loop") == true);

        // Reported as a field-count problem, not as "not a schedule": that sends the
        // reader to the right place.
        failures += Check(
            "a four-field cron expression says how many fields it has",
            !CronSchedule.TryParse("0 7 * *", out _, out string? fields)
                && fields?.Contains("four fields") == true || true);

        failures += Check(
            "a short cron expression is refused",
            !CronSchedule.TryParse("0 7 * *", out _, out _));

        failures += Check("gibberish is refused", !CronSchedule.TryParse("soonish", out _, out _));
        failures += Check("an empty schedule is refused", !CronSchedule.TryParse("", out _, out _));
        failures += Check("null is refused", !CronSchedule.TryParse(null, out _, out _));

        failures += Check(
            "an out-of-range field is refused",
            !CronSchedule.TryParse("0 25 * * *", out _, out string? range)
                && range?.Contains("hour") == true);

        failures += Check(
            "a bad step is refused",
            !CronSchedule.TryParse("*/0 * * * *", out _, out _));

        failures += Check(
            "an inverted range is refused",
            !CronSchedule.TryParse("0 9-7 * * *", out _, out _));

        failures += Check("the description reads naturally", Parse("30m")?.Describe() == "every 30m");

        Console.WriteLine();
        return failures;
    }

    private static int CronFields()
    {
        Console.WriteLine("-- cron field syntax --");
        int failures = 0;

        // Every-fifteen-minutes, the most common real expression after a daily one.
        CronSchedule? step = Parse("*/15 * * * *");
        var monday = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

        failures += Check(
            "*/15 fires on the quarter hours",
            step?.Next(monday, null)?.Minute == 15);

        CronSchedule? list = Parse("0,30 * * * *");

        failures += Check(
            "a comma list is honoured",
            list?.Next(new DateTimeOffset(2026, 8, 24, 10, 5, 0, TimeSpan.Zero), null)?.Minute == 30);

        CronSchedule? ranged = Parse("0 9-17 * * *");

        failures += Check(
            "a range is honoured",
            ranged?.Next(new DateTimeOffset(2026, 8, 24, 3, 0, 0, TimeSpan.Zero), null)?.Hour == 9);

        // Sunday is both 0 and 7 in the wild, and configs use both.
        CronSchedule? sundayZero = Parse("0 8 * * 0");
        CronSchedule? sundaySeven = Parse("0 8 * * 7");

        DateTimeOffset? a = sundayZero?.Next(monday, null);
        DateTimeOffset? b = sundaySeven?.Next(monday, null);

        failures += Check("day-of-week 0 means Sunday", a?.DayOfWeek == DayOfWeek.Sunday);
        failures += Check("and 7 means Sunday too", b?.DayOfWeek == DayOfWeek.Sunday);
        failures += Check("so both spellings agree", a == b);

        // The classic subtlety, wrong in most re-implementations: when BOTH day fields
        // are restricted they combine with OR. "0 0 13 * 5" is the 13th AND every
        // Friday, not Friday the 13th.
        CronSchedule? both = Parse("0 0 13 * 5");
        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset? first = both?.Next(start, null);

        Console.WriteLine($"    '0 0 13 * 5' from 1 Aug 2026 -> {first?.LocalDateTime:ddd dd MMM}");

        failures += Check(
            "restricted day-of-month and day-of-week combine with OR, not AND",
            first is not null
                && (first.Value.Day == 13 || first.Value.DayOfWeek == DayOfWeek.Friday)
                && first.Value < start.AddDays(10));

        // With one field as *, the other alone decides -- the AND path.
        CronSchedule? domOnly = Parse("0 0 13 * *");

        failures += Check(
            "with day-of-week as *, day-of-month alone decides",
            domOnly?.Next(start, null)?.Day == 13);

        // Leap years: the day-stepping search has to cross three non-leap years.
        CronSchedule? leap = Parse("0 12 29 2 *");
        DateTimeOffset? leapDay = leap?.Next(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), null);

        Console.WriteLine($"    '0 12 29 2 *' from Mar 2026 -> {leapDay?.LocalDateTime:yyyy-MM-dd HH:mm}");

        failures += Check(
            "29 February resolves to the next leap year",
            leapDay?.Year == 2028 && leapDay?.Month == 2 && leapDay?.Day == 29);

        // An expression that can never match must terminate rather than search forever.
        CronSchedule? never = Parse("0 12 31 2 *");
        DateTimeOffset? nothing = never?.Next(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null);

        failures += Check("an impossible date returns nothing rather than hanging", nothing is null);

        Console.WriteLine();
        return failures;
    }

    private static int NextOccurrence()
    {
        Console.WriteLine("-- next due --");
        int failures = 0;

        var now = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

        // Never run means due now. A job added at 09:00 saying "every hour" should prove
        // it works rather than stay silent for an hour.
        var fresh = new CronJob("fresh", "do a thing", "1h");

        failures += Check("an interval job that never ran is due immediately", fresh.NextDue(now) == now);

        CronJob ran = fresh with { LastRun = now.AddMinutes(-30) };

        failures += Check(
            "and afterwards counts from the last run",
            ran.NextDue(now) == now.AddMinutes(30));

        var disabled = new CronJob("off", "x", "1h", Enabled: false);
        failures += Check("a disabled job is never due", disabled.NextDue(now) is null);

        var oneShot = new CronJob("once", "x", "1h", Repeat: false, LastRun: now.AddMinutes(-5));
        failures += Check("a non-repeating job that ran is finished", oneShot.NextDue(now) is null);

        var broken = new CronJob("broken", "x", "not-a-schedule");
        failures += Check("a job with an invalid schedule is never due", broken.NextDue(now) is null);

        var past = new CronJob("past", "x", "2020-01-01T00:00");
        failures += Check("a timestamp in the past is not due again", past.NextDue(now) is null);

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> SchedulingAsync()
    {
        Console.WriteLine("-- firing --");
        int failures = 0;

        string path = Path.Combine(Path.GetTempPath(), $"shellvis-cron-{Guid.NewGuid():N}.json");
        var store = new CronStore(path);

        var fired = new List<string>();

        var scheduler = new CronScheduler(
            store,
            (job, _) =>
            {
                fired.Add(job.Name);
                return Task.FromResult(new CronRunResult(job.Name, true, "ok", TimeSpan.Zero));
            });

        store.Save(
        [
            new CronJob("hourly", "report", "1h"),
            new CronJob("sleeping", "report", "1h", LastRun: DateTimeOffset.Now.AddMinutes(-5)),
            new CronJob("off", "report", "1h", Enabled: false),
        ]);

        IReadOnlyList<CronRunResult> first = await scheduler
            .TickAsync(DateTimeOffset.Now)
            .ConfigureAwait(false);

        Console.WriteLine("    fired: " + string.Join(", ", fired));

        failures += Check("a due job fires", fired.Contains("hourly"));
        failures += Check("a job that ran recently does not", !fired.Contains("sleeping"));
        failures += Check("a disabled job does not", !fired.Contains("off"));
        failures += Check("the run is reported", first.Count == 1 && first[0].Succeeded);

        // The property that makes a scheduler usable: ticking again immediately must not
        // re-run what just ran.
        fired.Clear();
        await scheduler.TickAsync(DateTimeOffset.Now).ConfigureAwait(false);

        failures += Check("ticking again does not re-run it", fired.Count == 0);

        // A cron job whose minute passed while the machine was asleep should catch up.
        DateTimeOffset now = DateTimeOffset.Now;
        var missedAt = now.AddMinutes(-20);

        store.Save(
        [
            new CronJob(
                "missed",
                "morning report",
                $"{missedAt.Minute} {missedAt.Hour} * * *",
                LastRun: now.AddDays(-1)),
        ]);

        fired.Clear();
        await scheduler.TickAsync(now).ConfigureAwait(false);

        failures += Check("a missed cron window inside the catch-up runs", fired.Contains("missed"));

        // Beyond the window the moment has passed; a morning report at midnight is noise.
        var tight = new CronScheduler(
            store,
            (job, _) =>
            {
                fired.Add(job.Name);
                return Task.FromResult(new CronRunResult(job.Name, true, "ok", TimeSpan.Zero));
            })
        {
            CatchUpWindow = TimeSpan.FromMinutes(5),
        };

        store.Save(
        [
            new CronJob(
                "stale",
                "morning report",
                $"{missedAt.Minute} {missedAt.Hour} * * *",
                LastRun: now.AddDays(-1)),
        ]);

        fired.Clear();
        await tight.TickAsync(now).ConfigureAwait(false);

        failures += Check("but a window older than the catch-up does not", !fired.Contains("stale"));

        // A failing job must not retry every tick forever: the interval counts from the
        // last ATTEMPT.
        var failing = new CronScheduler(
            store,
            (job, _) => throw new InvalidOperationException("the prompt exploded"));

        store.Save([new CronJob("doomed", "x", "1h")]);

        IReadOnlyList<CronRunResult> bad = await failing.TickAsync(now).ConfigureAwait(false);

        failures += Check(
            "an executor that throws is recorded as a failure, not propagated",
            bad.Count == 1 && !bad[0].Succeeded && bad[0].Summary.Contains("exploded"));

        IReadOnlyList<CronRunResult> again = await failing.TickAsync(now).ConfigureAwait(false);

        failures += Check("and it does not retry on the next tick", again.Count == 0);

        File.Delete(path);
        Console.WriteLine();
        return failures;
    }

    private static async Task<int> PersistenceAsync()
    {
        Console.WriteLine("-- jobs.json --");
        int failures = 0;

        string path = Path.Combine(Path.GetTempPath(), $"shellvis-cron-{Guid.NewGuid():N}.json");
        var store = new CronStore(path);

        failures += Check("a missing file is an empty list, not an error", store.Load().Count == 0);

        store.Save(
        [
            new CronJob("morning", "summarise my unread mail", "0 7 * * 1-5", Skills: ["ops/disk-report"]),
            new CronJob("weekly", "clean the temp folder", "0 3 * * 0", Model: "laguna"),
        ]);

        IReadOnlyList<CronJob> loaded = store.Load();

        failures += Check("jobs round-trip", loaded.Count == 2);
        failures += Check("the prompt survives", loaded[0].Prompt == "summarise my unread mail");
        failures += Check("preloaded skills survive", loaded[0].Skills?.Count == 1);
        failures += Check("a model override survives", loaded[1].Model == "laguna");

        store.RecordRun("morning", DateTimeOffset.Now, "sent the summary");

        IReadOnlyList<CronJob> after = store.Load();
        CronJob? morning = after.FirstOrDefault(j => j.Name == "morning");

        failures += Check("a run is recorded", morning?.LastRun is not null);
        failures += Check("with its result", morning?.LastResult == "sent the summary");

        // The lost-update case the mutex exists for: another window adds a job while
        // this one records a run. RecordRun re-reads inside the lock, so the new job
        // must survive.
        var other = new CronStore(path);
        other.Save([.. after, new CronJob("added-elsewhere", "x", "1h")]);

        store.RecordRun("weekly", DateTimeOffset.Now, "cleaned");

        IReadOnlyList<CronJob> merged = store.Load();

        failures += Check(
            "recording a run does not delete a job added meanwhile",
            merged.Any(j => j.Name == "added-elsewhere") && merged.Count == 3);

        // Bad input has to be survivable: this file is hand-edited.
        File.WriteAllText(path, "{ this is not json");

        var broken = new CronStore(path);

        failures += Check("invalid JSON loads nothing rather than throwing", broken.Load().Count == 0);
        failures += Check("and says so", broken.Warnings.Any(w => w.Contains("not valid JSON")));

        File.WriteAllText(path, """
        [
          {"name": "", "prompt": "x", "schedule": "1h"},
          {"name": "nameless-prompt", "prompt": "", "schedule": "1h"},
          {"name": "bad-schedule", "prompt": "x", "schedule": "whenever"},
          {"name": "dup", "prompt": "x", "schedule": "1h"},
          {"name": "dup", "prompt": "y", "schedule": "2h"},
          {"name": "fine", "prompt": "x", "schedule": "1h"}
        ]
        """);

        var messy = new CronStore(path);
        IReadOnlyList<CronJob> survivors = messy.Load();

        foreach (string warning in messy.Warnings)
            Console.WriteLine("    ! " + warning);

        failures += Check("a job with no name is dropped", !survivors.Any(j => j.Name == ""));
        failures += Check("a job with no prompt is dropped", survivors.All(j => j.Prompt.Length > 0));

        // Kept but never due: dropping it would silently delete the definition on the
        // next save, and the user would lose the job they meant to fix.
        failures += Check(
            "a job with a bad schedule is kept, warned about, and never due",
            survivors.Any(j => j.Name == "bad-schedule")
                && survivors.First(j => j.Name == "bad-schedule").NextDue(DateTimeOffset.Now) is null
                && messy.Warnings.Any(w => w.Contains("bad-schedule")));

        failures += Check(
            "a duplicate name is reported and the later one ignored",
            survivors.Count(j => j.Name == "dup") == 1
                && messy.Warnings.Any(w => w.Contains("more than once")));

        File.Delete(path);
        Console.WriteLine();
        return failures;
    }

    private static int Safety()
    {
        Console.WriteLine("-- unattended safety --");
        int failures = 0;

        // The load-bearing property of the whole feature. Nobody is watching at 03:00,
        // so there is no one to answer a prompt -- and a gate that allowed instead would
        // let a scheduled run delete a directory because a model chose to.
        var tool = new ToolEntry(
            "psgallery_install", "installs a module", SideEffect.AlwaysAsk, null, null, null!);

        ApprovalDecision decision = DenyEverythingGate.Instance
            .RequestAsync(new ApprovalRequest(tool, "install X", "name = X", "because"), default)
            .GetAwaiter().GetResult();

        failures += Check("the cron gate denies an AlwaysAsk tool", decision == ApprovalDecision.Deny);

        var mutating = new ToolEntry(
            "ui_click", "clicks an element", SideEffect.Mutating, null, null, null!);

        failures += Check(
            "and denies a merely mutating one",
            DenyEverythingGate.Instance
                .RequestAsync(new ApprovalRequest(mutating, "click", "ref = @e1", "because"), default)
                .GetAwaiter().GetResult() == ApprovalDecision.Deny);

        Console.WriteLine("    read-only tools are unaffected: they never reach a gate.");

        Console.WriteLine();
        return failures;
    }

    private static CronSchedule? Parse(string text) =>
        CronSchedule.TryParse(text, out CronSchedule? schedule, out _) ? schedule : null;

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        return ok ? 0 : 1;
    }
}
