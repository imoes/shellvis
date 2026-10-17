using Shellvis.Core.Agent;
using Shellvis.Core.Tools;

namespace Shellvis.Core.Cron;

/// <summary>What happened on one scheduled run.</summary>
/// <param name="Headline">
/// One sentence, when the run found something the user should be told about now -- and null
/// when it did not.
///
/// <b>Why the model decides this and not a rule here.</b> "Is this news" is a judgement about
/// content: three routine mails are not, one from the person whose deadline is tomorrow is.
/// No condition available at this layer can tell those apart, and the alternatives are both
/// wrong -- announcing every run trains the user to ignore the announcement, announcing none
/// makes a scheduled assistant pointless. So the run is asked to say, in a form that is
/// absent by default: no line, no notice.
/// </param>
public sealed record CronRunResult(
    string Job,
    bool Succeeded,
    string Summary,
    TimeSpan Duration,
    string? Headline = null);

/// <summary>
/// An approval gate that refuses everything.
///
/// This is the load-bearing safety property of the whole cron feature. A scheduled run
/// happens with nobody watching, so there is no one to answer a prompt -- and a gate
/// that allowed instead would mean an unattended agent could run
/// <c>Remove-Item -Recurse -Force</c> at three in the morning because a model chose to.
/// Refusing is also what a timed-out interactive prompt does, so the behaviour is
/// consistent: no human, no permission.
///
/// Read-only tools are unaffected: they never reach a gate.
/// </summary>
public sealed class DenyEverythingGate : IApprovalGate
{
    public static readonly DenyEverythingGate Instance = new();

    public Task<ApprovalDecision> RequestAsync(
        ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Deny);
}

/// <summary>
/// Fires scheduled jobs.
///
/// Deliberately knows nothing about how a job is executed. The executor is supplied by
/// the caller, which is what lets the shell give each run a genuinely fresh agent -- its
/// own history, its own runspace -- while a probe can supply something instant. Building
/// the agent in here would have hard-wired one answer to the isolation question.
/// </summary>
public sealed class CronScheduler(
    CronStore store,
    Func<CronJob, CancellationToken, Task<CronRunResult>> executor,
    TimeSpan? tick = null)
{
    /// <summary>
    /// How often the schedule is re-examined.
    ///
    /// Thirty seconds, which is also the minimum interval. A finer tick would burn
    /// wake-ups on a laptop for no gain, since cron itself has minute resolution.
    /// </summary>
    private readonly TimeSpan _tick = tick ?? TimeSpan.FromSeconds(30);

    /// <summary>
    /// How late a missed one-shot may still run.
    ///
    /// A machine that was asleep or switched off at 07:00 should still send the morning
    /// report when it wakes at 08:30. Beyond the grace period the moment has passed and
    /// running would be surprising rather than helpful -- a report about yesterday
    /// delivered at midnight is noise.
    /// </summary>
    public TimeSpan CatchUpWindow { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Raised for each run, so the console can show it.</summary>
    public event Action<CronRunResult>? Ran;

    /// <summary>Raised when a job could not even be started.</summary>
    public event Action<string>? Problem;

    /// <summary>
    /// One pass over the schedule. Returns the jobs that ran.
    ///
    /// Public so a probe can drive time explicitly instead of waiting for a timer, which
    /// is the only way to test a scheduler without sleeping through it.
    /// </summary>
    public async Task<IReadOnlyList<CronRunResult>> TickAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CronJob> jobs = store.Load();

        foreach (string warning in store.Warnings)
            Problem?.Invoke(warning);

        var results = new List<CronRunResult>();

        foreach (CronJob job in jobs)
        {
            if (!IsDue(job, now))
                continue;

            CronRunResult result;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                result = await executor(job, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A job that threw is news whatever it was for. The model never got to
                // judge, so the judgement is made here, and only here: this is the one case
                // where a headline is not the run's own opinion.
                result = new CronRunResult(
                    job.Name, false, $"failed: {ex.Message}", clock.Elapsed,
                    Headline: $"The scheduled job '{job.Name}' failed.");
            }

            // Recorded even on failure. An interval counts from the last ATTEMPT, not
            // the last success: without this a job whose prompt always fails would
            // retry on every tick forever.
            store.RecordRun(job.Name, now, result.Summary);

            results.Add(result);
            Ran?.Invoke(result);
        }

        return results;
    }

    /// <summary>
    /// Whether a job should run at this moment.
    /// </summary>
    public bool IsDue(CronJob job, DateTimeOffset now)
    {
        if (!job.Enabled)
            return false;

        // Windows owns this one's timing. Running it here as well would run it twice on any
        // machine where Shellvis happens to be open at the moment the task fires -- two
        // briefings, two sets of notifications, and a user who cannot tell which is which.
        //
        // Checked from a flag on the job rather than by asking Task Scheduler: this runs on
        // a timer, and launching a process per tick to ask a question whose answer changes
        // only when someone edits a job is the wrong trade.
        if (job.WindowsTask)
            return false;

        CronSchedule? schedule = job.Parsed;

        if (schedule is null)
            return false;

        if (!job.Repeat && job.LastRun is not null)
            return false;

        switch (schedule.Kind)
        {
            case ScheduleKind.Interval:
                // Never run means due now: a job added at 09:00 with "every hour"
                // should prove it works rather than stay silent until 10:00.
                return job.LastRun is null || now >= job.LastRun.Value + schedule.Interval;

            case ScheduleKind.Once:
                return now >= schedule.At && now - schedule.At <= CatchUpWindow;

            default:
                // For cron, "due" means a matching minute lies between the last run and
                // now. Computing the next occurrence FROM THE LAST RUN rather than from
                // now is what makes a missed window catch up, and what stops a job
                // firing twice inside the same minute when the tick is shorter than a
                // minute.
                DateTimeOffset from = job.LastRun ?? now - CatchUpWindow;
                DateTimeOffset? next = schedule.Next(from, job.LastRun);

                return next is not null && next <= now && now - next.Value <= CatchUpWindow;
        }
    }

    /// <summary>
    /// Run until cancelled.
    ///
    /// A PeriodicTimer rather than a delay loop: it does not drift, which matters for a
    /// scheduler that is meant to fire on the minute.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_tick);

        // One pass immediately, so a job that was due while the app was closed is not
        // held back by a full tick.
        await TickAsync(DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await TickAsync(DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
    }

    /// <summary>A listing with the next due time worked out, for showing the user.</summary>
    public IReadOnlyList<string> Describe(DateTimeOffset now)
    {
        IReadOnlyList<CronJob> jobs = store.Load();

        if (jobs.Count == 0)
            return ["no scheduled jobs."];

        var lines = new List<string>();

        foreach (CronJob job in jobs.OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase))
        {
            DateTimeOffset? due = job.NextDue(now);

            string when = due is null
                ? job.Enabled ? "not scheduled again" : "disabled"
                : $"next {due.Value.LocalDateTime:dd.MM. HH:mm}";

            lines.Add($"{job}  |  {when}");
        }

        return lines;
    }
}
