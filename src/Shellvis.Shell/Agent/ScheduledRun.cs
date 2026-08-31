using Microsoft.UI.Dispatching;

using Shellvis.Core.Cron;

namespace Shellvis.Shell.Agent;

/// <summary>
/// One job, run because Windows asked, with no window and nobody watching.
///
/// <b>Why the scheduler moved out of the process.</b> The in-process loop only runs while
/// Shellvis is running. A briefing meant for eight in the morning does not happen if the
/// machine was rebooted at seven, and there is no way to see from Windows that anything is
/// scheduled at all -- Task Scheduler shows nothing, because nothing was ever registered.
/// So the trigger belongs to Windows and the work belongs here: a task calls this executable
/// with <c>--job &lt;name&gt;</c>, the run happens, the process exits.
///
/// <b>What it deliberately does not do.</b> Show anything. There is no pill, no console and no
/// alert: those belong to a running instance and this is not one. The report goes into the
/// session store, where the next interactive session can find it, and into the run record on
/// the job itself, which <c>cron_list</c> reads back. A scheduled run that pops a window on a
/// machine somebody else is using is the interruption this project has argued itself out of
/// twice.
///
/// <b>Approvals are refused</b>, exactly as in the in-process loop, and by the same gate. That
/// property is what makes an unattended run safe to have at all, and it must not depend on
/// which of the two paths started it.
/// </summary>
internal static class ScheduledRun
{
    /// <summary>The switch a Windows task passes to name the job.</summary>
    public const string JobArgument = "--job";

    /// <summary>The switch for a prompt to ask as an ordinary turn.</summary>
    public const string PromptArgument = "--prompt";

    /// <summary>
    /// What this process was started to do, or null when it was started to show a window.
    /// </summary>
    public static Errand? RequestedErrand()
    {
        if (Value(JobArgument) is { Length: > 0 } job)
            return new Errand(job, null);

        return Value(PromptArgument) is { Length: > 0 } prompt
            ? new Errand(null, prompt)
            : null;
    }

    private static string? Value(string switchName)
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals(switchName, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    /// <summary>
    /// Whether this process was started to run a job rather than to show a window.
    /// </summary>
    /// <remarks>
    /// Read from the real command line rather than from the activation arguments: an
    /// unpackaged WinUI app receives nothing useful in <c>LaunchActivatedEventArgs</c>, which
    /// is a detail worth writing down because the code that looks correct does not work.
    /// </remarks>
    public static string? RequestedJob()
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals(JobArgument, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    /// <summary>
    /// Run the named job and return what to say about it.
    ///
    /// Returns rather than reports, so the caller decides what to do with the outcome -- and
    /// so this is testable without a scheduler, a task, or a desktop.
    /// </summary>
    public static async Task<string> RunAsync(
        Errand errand, DispatcherQueue dispatcher, CancellationToken cancellationToken)
    {
        var store = new CronStore();
        CronJob? job = null;

        if (errand.Job is { Length: > 0 } name)
        {
            job = store.Load().FirstOrDefault(
                j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (job is null)
                return $"no scheduled job named '{name}' in {store.Path}.";

            if (!job.Enabled)
                return $"'{job.Name}' is disabled; nothing was run.";
        }
        else if (errand.Prompt is { Length: > 0 } prompt)
        {
            // A bare prompt is run through the same unattended machinery as a job, under a
            // name that says where it came from. It is NOT run as an interactive turn: there
            // is no user here to answer a permission prompt, so the deny-everything gate has
            // to apply to this exactly as it does to a job.
            job = new CronJob(
                Name: "--prompt",
                Prompt: prompt,
                Schedule: "1d",
                Repeat: false);
        }
        else
        {
            return "nothing to do: neither a job nor a prompt was given.";
        }

        AgentSession session;

        try
        {
            // The same session an interactive run gets, with the deny-everything gate. Built
            // in full rather than trimmed: the job's whole point is that it has the tool set,
            // and a cut-down session would answer differently from the same job run by hand.
            session = AgentSession.Create(dispatcher, DenyEverythingGate.Instance);
        }
        catch (Exception ex)
        {
            return $"'{job.Name}' could not start: {ex.Message}";
        }

        try
        {
            CronRunResult result = await session
                .RunScheduledJobAsync(job, cancellationToken)
                .ConfigureAwait(false);

            // Only a real job gets a run recorded. A one-off prompt has no entry to update,
            // and inventing one would put a job in the list that nobody scheduled.
            if (errand.Job is { Length: > 0 })
                store.RecordRun(job.Name, DateTimeOffset.Now, result.Summary);

            return $"'{job.Name}' {(result.Succeeded ? "ran" : "failed")} in "
                + $"{result.Duration.TotalSeconds:F1}s: {result.Summary}";
        }
        finally
        {
            session.Dispose();
        }
    }
}
