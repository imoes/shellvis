using Microsoft.Extensions.AI;
using Shellvis.Core.Agent;
using Shellvis.Core.Cron;
using Shellvis.Core.Skills;

namespace Shellvis.Shell.Agent;

/// <summary>
/// Scheduled runs.
///
/// A cron run is not a turn in the user's conversation and must not become one, so it
/// gets its own <see cref="AgentLoop"/> with an empty history. Sharing the interactive
/// loop would splice unrelated work into the middle of whatever the user was doing --
/// and worse, the summary the model wrote at 03:00 would then be part of the context of
/// the next thing the user asked.
/// </summary>
internal sealed partial class AgentSession
{
    private CancellationTokenSource? _cronStop;

    /// <summary>Whether any job is configured, for the status line.</summary>
    public int CronJobCount { get; private set; }

    /// <summary>
    /// Start the scheduler.
    ///
    /// <paramref name="report"/> is called on the UI thread for each run and each
    /// problem. A scheduled run that leaves no trace in the console would be an agent
    /// touching the machine invisibly, which is the opposite of what this project is
    /// for.
    /// </summary>
    public void StartCron(Action<string, bool, CronRunResult?> report)
    {
        var store = new CronStore();
        IReadOnlyList<CronJob> jobs = store.Load();

        foreach (string warning in store.Warnings)
            report($"cron: {warning}", false, null);

        CronJobCount = jobs.Count;

        if (jobs.Count == 0)
            return;

        var scheduler = new CronScheduler(store, RunJobAsync);

        scheduler.Ran += result => Post(() => report(
            $"cron '{result.Job}' {(result.Succeeded ? "ran" : "failed")} in "
                + $"{result.Duration.TotalSeconds:F1}s: {result.Summary}",
            !result.Succeeded,
            result));

        scheduler.Problem += problem => Post(() => report($"cron: {problem}", true, null));

        _cronStop = new CancellationTokenSource();

        // Fire and forget on purpose: the scheduler owns its own loop and its own
        // cancellation, and awaiting it here would block window activation forever.
        _ = Task.Run(() => scheduler.RunAsync(_cronStop.Token));

        report(
            $"{jobs.Count} scheduled job(s) armed. Approvals are denied in scheduled "
            + "runs, so only read-only actions happen unattended.",
            false,
            null);
    }

    private void Post(Action action) => _dispatcher.TryEnqueue(() => action());

    /// <summary>
    /// Run one job now, for a Windows task that asked for it by name.
    ///
    /// The same method the in-process scheduler uses, reached from outside. Sharing it is the
    /// point: two paths that ran a job slightly differently would mean "it works when I run it
    /// by hand" -- and the difference would be in the part nobody watches.
    /// </summary>
    internal Task<CronRunResult> RunScheduledJobAsync(CronJob job, CancellationToken cancellationToken) =>
        RunJobAsync(job, cancellationToken);

    /// <summary>
    /// Execute one job.
    ///
    /// Serialised against interactive turns through the same gate. Two loops sharing one
    /// PowerShell runspace and one tool registry cannot run at once -- that is the
    /// concurrency bug this project already met once, and a scheduled job firing while
    /// the user is mid-turn is exactly how it would come back.
    /// </summary>
    private async Task<CronRunResult> RunJobAsync(CronJob job, CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Nobody is watching, so clarify answers "nobody is here" instead of
            // opening a dialog that would hang the job until its own timeout. Set
            // inside the gate, which is what makes one flag safe for two loops.
            _unattended.Value = true;

            string prompt = BuildCronPrompt(job);

            // A loop of its own over the SAME tool registry. The registry is shared
            // deliberately: a second PowerShell runspace would cost a second of startup
            // and a copy of the SDK in memory for something that runs once an hour. What
            // is NOT shared is the history, which is the part that would corrupt the
            // conversation.
            var loop = new AgentLoop(
                _cronClient!,
                _registry,
                DenyEverythingGate.Instance,
                new AgentOptions(
                    MaxIterations: 8,
                    SystemPrompt: CronSystemPrompt));

            var answer = new System.Text.StringBuilder();
            bool failed = false;

            await foreach (AgentEvent evt in loop.RunAsync(prompt, cancellationToken)
                .ConfigureAwait(false))
            {
                switch (evt)
                {
                    case AgentEvent.AssistantMessage message:
                        answer.Append(message.Text);
                        break;

                    case AgentEvent.Failure failure:
                        failed = true;
                        answer.Append("[failure] ").Append(failure.Message);
                        break;
                }
            }

            clock.Stop();

            string report = answer.ToString().Trim();

            // Split before flattening: the headline is a line, and ReplaceLineEndings would
            // dissolve the very boundary that identifies it.
            string? headline = CronReport.TakeHeadline(ref report);

            string summary = report.ReplaceLineEndings(" ").Trim();

            if (summary.Length == 0)
                summary = "the run produced no answer";

            // A run that did not finish is news whatever it was about, and the model never
            // got to say so: it is the failure that stopped it. The same judgement the
            // scheduler makes for a job that threw, made here for one that gave up.
            //
            // A scheduled task quietly ceasing to work is the worst outcome this feature
            // has, because it looks exactly like a machine on which nothing is happening.
            if (failed && headline is null)
                headline = $"The scheduled job '{job.Name}' could not finish.";

            return new CronRunResult(job.Name, !failed, summary, clock.Elapsed, headline);
        }
        finally
        {
            _unattended.Value = false;
            _turnGate.Release();
        }
    }

    /// <summary>
    /// The job prompt, with its skills named up front.
    ///
    /// Named rather than injected: the skill index is already in the system prompt, and
    /// telling the model which ones apply lets it fetch the bodies it needs through
    /// skill_view. Pasting whole skill bodies into every scheduled run would spend the
    /// context budget before the job starts.
    /// </summary>
    private static string BuildCronPrompt(CronJob job)
    {
        if (job.Skills is not { Count: > 0 })
            return job.Prompt;

        return $"Apply these skills to this task: {string.Join(", ", job.Skills)}.\n\n{job.Prompt}";
    }

    /// <summary>
    /// The operating rules for an unattended run.
    ///
    /// It says plainly that approvals are refused. Without that the model spends rounds
    /// retrying a write that will never be permitted, and reports failure rather than
    /// reporting what it did manage to find out.
    /// </summary>
    private static string CronSystemPrompt =>
        // Built rather than constant, because it carries the date, and a scheduled job is
        // the case where a wrong date matters most: "yesterday's errors" or "this week's
        // appointments" is most of what a recurring job asks for, and nobody is watching to
        // notice the answer was about a week two years ago.
        $"""
        You are Shellvis, running a scheduled job on this Windows machine. Nobody is
        watching, so there is nobody to approve anything: every action that changes
        state will be refused. Gather what you need with read-only tools and report
        what you found.

        Today is {DateTime.Now:dddd, d MMMM yyyy}, local time {DateTime.Now:HH:mm}. Never
        guess a date; work it out from this one.

        Report what the tools returned and nothing more. An empty result is a result: if
        there is nothing to report, say that. Nobody is here to notice an invented answer.

        - If a task genuinely needs a change, say so and stop rather than retrying.
        - Be brief. This report is read later, out of context.
        - Reply in the language the task was written in.

        If -- and only if -- you found something the user should be told about NOW, end your
        report with one final line:

            {CronReport.Marker} <one sentence, the thing itself, not that you looked>

        This raises a notification on their screen. Leave the line out entirely for a routine
        run, and that is most runs: nothing found, nothing changed, nothing due, or news that
        can wait until they next look. Deadlines today, a failure, something addressed to
        them that will not keep -- those are worth it. A notification for a routine result
        teaches them to ignore the next one, and then the one that mattered is lost too.
        """;

    /// <summary>
    /// The client scheduled runs talk to.
    ///
    /// Held separately so a job's model override has somewhere to go, and so a scheduled
    /// run cannot be affected by an interactive model switch mid-flight.
    /// </summary>
    private IChatClient? _cronClient;

    private void StopCron()
    {
        _cronStop?.Cancel();
        _cronStop?.Dispose();
        _cronStop = null;
    }
}
