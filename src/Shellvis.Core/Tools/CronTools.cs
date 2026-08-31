using System.Text;

using Shellvis.Core.Cron;

namespace Shellvis.Core.Tools;

/// <summary>
/// Configuring the scheduler from a conversation.
///
/// <b>Why this exists.</b> The scheduler could run jobs and could not be told about them: the
/// only way in was to hand-edit <c>jobs.json</c>, in the right JSON shape, with the right
/// snake_case keys, and restart. That is not a feature anybody has, and the gap was reported
/// as exactly that -- "there is no way to configure a scheduler". Saying it in words is the
/// interface this application already has for everything else.
///
/// <b>Why writing one asks every time.</b> A scheduled job is the one thing here that acts
/// with nobody watching, on a timer, indefinitely. It is the highest-consequence thing a
/// sentence can create, and the consequence arrives later, when whoever agreed to it has
/// forgotten. So every write is <see cref="SideEffect.AlwaysAsk"/>, which no permission mode
/// waives -- and a scheduled run cannot create jobs at all, because its own approvals are
/// refused. A timer that can add timers is a thing that grows while nobody looks.
/// </summary>
/// <param name="executable">
/// The Shellvis a Windows task should call. Passed in rather than discovered here, because
/// Core has no business knowing which executable is hosting it -- and on a developer machine
/// the answer differs between a Debug run, a Release run and an installed copy.
/// </param>
public sealed class CronTools(CronStore store, string executable)
{
    /// <summary>
    /// A ceiling on how many jobs may exist.
    ///
    /// Not a resource limit -- twenty jobs cost nothing to store. It is a limit on how much
    /// unattended activity can accumulate through a series of individually reasonable
    /// requests, each of which was approved on its own and none of which was "twenty jobs".
    /// </summary>
    private const int MaxJobs = 20;

    [ShellvisTool(
        "cron_list",
        SideEffect.ReadOnly,
        Description =
            "The scheduled jobs: what each one does, when it runs, when it last ran and when "
            + "it is next due. Read this before adding one, so the same job does not go up twice.",
        Glyph = "clock")]
    public string List()
    {
        IReadOnlyList<CronJob> jobs = store.Load();

        var sb = new StringBuilder();

        foreach (string warning in store.Warnings)
            sb.Append("warning: ").AppendLine(warning);

        if (jobs.Count == 0)
        {
            sb.Append("no scheduled jobs. The file is ").Append(store.Path)
              .AppendLine(", and cron_add writes it.");

            return sb.ToString();
        }

        DateTimeOffset now = DateTimeOffset.Now;

        sb.Append(jobs.Count).AppendLine(" scheduled job(s):");

        foreach (CronJob job in jobs)
        {
            sb.Append("  ").Append(job.Name)
              .Append(job.Enabled ? "  " : "  [disabled]  ")
              .Append(job.Parsed?.Describe() ?? $"UNPARSEABLE schedule '{job.Schedule}'");

            if (job.NextDue(now) is { } due)
                sb.Append("  next ").Append(due.ToString("ddd HH:mm"));

            if (job.LastRun is { } last)
                sb.Append("  last ").Append(last.ToString("dd.MM. HH:mm"));

            sb.AppendLine();

            // The prompt is the job. A listing that omits it tells the reader a job exists
            // and not what it will do, which is the one thing they need to judge it.
            sb.Append("      ").AppendLine(Oneline(job.Prompt));

            if (job.Skills is { Count: > 0 })
                sb.Append("      skills: ").AppendLine(string.Join(", ", job.Skills));

            if (job.LastResult is { Length: > 0 } result)
                sb.Append("      last result: ").AppendLine(Oneline(result));
        }

        return sb.ToString();
    }

    [ShellvisTool(
        "cron_add",
        SideEffect.AlwaysAsk,
        Description =
            "Add a scheduled job. The schedule is an interval (30m, 2h, 1d), a five-field "
            + "cron expression (0 8 * * 1-5), or an ISO timestamp for a single run. Say what "
            + "you want found, not how to find it: the run has the whole tool set. "
            + "IMPORTANT: a scheduled run happens with nobody watching and every approval "
            + "refused, so it can only READ. Do not create a job that has to change "
            + "something. End the prompt with a note that it should raise a notification "
            + "only if it finds something that matters.",
        PreviewParameter = "name",
        Glyph = "clock")]
    public string Add(string name, string prompt, string schedule, string? skills = null, bool repeat = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "error: a job needs a name; it is how you refer to it later.";

        if (string.IsNullOrWhiteSpace(prompt))
            return "error: a job needs a prompt. What should it find out?";

        if (!CronSchedule.TryParse(schedule, out CronSchedule? parsed, out string? problem))
            return $"error: {problem}";

        List<CronJob> jobs = [.. store.Load()];

        if (jobs.Any(j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"a job named '{name}' already exists. Remove it first, or pick another "
                + "name. Nothing was changed.";
        }

        if (jobs.Count >= MaxJobs)
        {
            return $"there are already {jobs.Count} jobs, which is the limit. Remove one "
                + "first; a machine quietly running more than that on timers is not "
                + "something anyone asked for in one piece.";
        }

        // The Windows task first, because whether it worked decides which scheduler owns the
        // timing -- and a job saved as "Windows owns it" when the task does not exist would
        // never run at all. Failing this way round, the worst case is a job that runs on the
        // in-process loop, which is what happened before any of this existed.
        bool asTask = false;
        string taskNote;

        if (string.IsNullOrWhiteSpace(executable))
        {
            taskNote = "no Windows task: this build could not work out its own path.";
        }
        else if (WindowsTasks.TryCreate(name.Trim(), schedule.Trim(), executable, out string message))
        {
            asTask = true;
            taskNote = $"registered with Windows Task Scheduler as {WindowsTasks.NameFor(name.Trim())}, "
                + "so it fires whether or not Shellvis is open.";
        }
        else
        {
            taskNote = $"no Windows task ({message}) -- it runs on the scheduler inside "
                + "Shellvis instead, which means only while Shellvis is running.";
        }

        jobs.Add(new CronJob(
            Name: name.Trim(),
            Prompt: prompt.Trim(),
            Schedule: schedule.Trim(),
            Repeat: repeat,
            Skills: Split(skills),
            Enabled: true,
            WindowsTask: asTask));

        store.Save(jobs);

        return $"added '{name}': {parsed!.Describe()}. {taskNote}";
    }

    [ShellvisTool(
        "cron_remove",
        SideEffect.AlwaysAsk,
        Description =
            "Remove a scheduled job by name. Ask cron_list first if the name is not certain: "
            + "removing the wrong job is silent, and nobody notices a job that stopped.",
        PreviewParameter = "name",
        Glyph = "clock")]
    public string Remove(string name)
    {
        List<CronJob> jobs = [.. store.Load()];

        CronJob? found = jobs.FirstOrDefault(
            j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (found is null)
        {
            return $"no job named '{name}'. Present: "
                + (jobs.Count == 0 ? "none" : string.Join(", ", jobs.Select(j => j.Name)));
        }

        jobs.Remove(found);
        store.Save(jobs);

        // The task goes too. A Windows task left behind would keep calling Shellvis with the
        // name of a job that no longer exists -- forever, on a timer, reporting an error into
        // a task history nobody reads.
        string taskNote = found.WindowsTask
            ? WindowsTasks.TryDelete(found.Name, out string message)
                ? " Its Windows task was removed too."
                : $" WARNING: its Windows task could NOT be removed ({message}); remove "
                    + $"{WindowsTasks.NameFor(found.Name)} in Task Scheduler by hand."
            : string.Empty;

        // What it was is echoed back, because a removal is the one operation whose result is
        // invisible: the proof that the right one went is the description of what went.
        return $"removed '{found.Name}' ({found.Parsed?.Describe() ?? found.Schedule}): "
            + Oneline(found.Prompt) + taskNote;
    }

    [ShellvisTool(
        "cron_enable",
        SideEffect.AlwaysAsk,
        Description =
            "Switch a scheduled job on or off without deleting it. Use this rather than "
            + "removing a job the user may want back -- a disabled job keeps its prompt, its "
            + "schedule and its history.",
        PreviewParameter = "name",
        Glyph = "clock")]
    public string Enable(string name, bool enabled = true)
    {
        List<CronJob> jobs = [.. store.Load()];
        int at = jobs.FindIndex(j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (at < 0)
        {
            return $"no job named '{name}'. Present: "
                + (jobs.Count == 0 ? "none" : string.Join(", ", jobs.Select(j => j.Name)));
        }

        if (jobs[at].Enabled == enabled)
            return $"'{jobs[at].Name}' is already {(enabled ? "enabled" : "disabled")}.";

        jobs[at] = jobs[at] with { Enabled = enabled };
        store.Save(jobs);

        return $"'{jobs[at].Name}' is now {(enabled ? "enabled" : "disabled")}.";
    }

    private static IReadOnlyList<string>? Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A prompt on one line, so a listing stays a listing.</summary>
    private static string Oneline(string text)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();

        while (flat.Contains("  ", StringComparison.Ordinal))
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);

        return flat.Length <= 120 ? flat : flat[..120] + "...";
    }
}
