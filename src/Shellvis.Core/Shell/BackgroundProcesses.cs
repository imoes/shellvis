using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Shellvis.Core.Shell;

/// <summary>What a background process is doing.</summary>
public sealed record ProcessStatus(
    string Id,
    string Command,
    int ProcessId,
    bool Running,
    int? ExitCode,
    DateTime Started,
    TimeSpan Elapsed,
    int OutputLines)
{
    public override string ToString()
    {
        string state = Running
            ? $"running for {Elapsed.TotalSeconds:F0}s"
            : $"exited {ExitCode?.ToString() ?? "?"} after {Elapsed.TotalSeconds:F0}s";

        return $"{Id}  [{state}]  pid {ProcessId}  {OutputLines} line(s)  \"{Command}\"";
    }
}

/// <summary>
/// Long-running commands, started and then checked on.
///
/// <b>Why this is needed.</b> Every shell tool here blocks until its command finishes and
/// then returns the whole output. That is right for a query and wrong for anything that
/// runs for minutes: a build, a copy, a service that has to stay up while something else is
/// tested. Without this the only options are to block the turn for the duration or to lose
/// the process, and a model faced with those two picks the first and the user watches a
/// frozen pill.
///
/// <b>Output is buffered, not streamed to the model.</b> A build prints tens of thousands of
/// lines and putting them in the context would end the conversation. The buffer keeps the
/// last few hundred lines and says how many it dropped, which is the shape of every other
/// bounded result in this project.
///
/// <b>Nothing survives the process.</b> These are children of this application, and when it
/// exits they are killed rather than orphaned. A background process nobody can see or stop,
/// still holding a file or a port after the thing that started it is gone, is a worse
/// outcome than losing the run.
/// </summary>
public sealed class BackgroundProcesses : IDisposable
{
    /// <summary>How many output lines are kept per process.</summary>
    /// <remarks>
    /// Enough to see what a command is doing and how it ended; far short of a full build
    /// log, which would be megabytes. The count of dropped lines is reported, so a truncated
    /// log never masquerades as a complete one.
    /// </remarks>
    private const int KeepLines = 400;

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    private int _next;

    private sealed class Job
    {
        public required string Id { get; init; }

        public required string Command { get; init; }

        public required Process Process { get; init; }

        public required DateTime Started { get; init; }

        public Queue<string> Lines { get; } = new();

        public int Dropped { get; set; }

        public object Gate { get; } = new();
    }

    /// <summary>Start a command and return immediately.</summary>
    public ProcessStatus Start(string fileName, string? arguments, string? workingDirectory)
    {
        string id = $"p{Interlocked.Increment(ref _next)}";

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var job = new Job
        {
            Id = id,
            Command = $"{fileName} {arguments}".Trim(),
            Process = process,
            Started = DateTime.Now,
        };

        // Event-driven rather than a reader task per stream. A blocking ReadToEnd on a
        // process that runs for an hour would hold two threads for the hour; the events
        // arrive as the lines do and cost nothing while it is quiet.
        process.OutputDataReceived += (_, e) => Append(job, e.Data);
        process.ErrorDataReceived += (_, e) => Append(job, e.Data is null ? null : "err: " + e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _jobs[id] = job;

        return Status(job);
    }

    /// <summary>Everything started in this session, newest first.</summary>
    public IReadOnlyList<ProcessStatus> List() =>
        [.. _jobs.Values.OrderByDescending(j => j.Started).Select(Status)];

    /// <summary>What one process is doing, or null if there is no such id.</summary>
    public ProcessStatus? Poll(string id) =>
        _jobs.TryGetValue(id, out Job? job) ? Status(job) : null;

    /// <summary>The buffered output of one process.</summary>
    public string? Log(string id, int tail = 100)
    {
        if (!_jobs.TryGetValue(id, out Job? job))
            return null;

        lock (job.Gate)
        {
            string[] lines = [.. job.Lines];
            string[] shown = tail >= lines.Length ? lines : lines[^tail..];

            var sb = new StringBuilder();

            // The count of what is missing comes first, because a log that quietly starts
            // in the middle reads as though the command started there.
            if (job.Dropped > 0 || shown.Length < lines.Length)
            {
                sb.Append("(")
                  .Append(job.Dropped + (lines.Length - shown.Length))
                  .AppendLine(" earlier line(s) not shown)");
            }

            foreach (string line in shown)
                sb.AppendLine(line);

            return sb.ToString();
        }
    }

    /// <summary>Wait for one process, up to a limit.</summary>
    public async Task<ProcessStatus?> WaitAsync(
        string id,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!_jobs.TryGetValue(id, out Job? job))
            return null;

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);

        try
        {
            await job.Process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A wait that runs out does NOT kill the process: the caller asked how it was
            // getting on, not for it to stop. Killing here would make "check on it" a
            // destructive operation.
        }

        return Status(job);
    }

    /// <summary>Stop one process and its children.</summary>
    public ProcessStatus? Kill(string id)
    {
        if (!_jobs.TryGetValue(id, out Job? job))
            return null;

        try
        {
            if (!job.Process.HasExited)
                job.Process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Gone between the check and the kill, or never really started. Either way the
            // status below tells the truth about it.
        }

        return Status(job);
    }

    private static void Append(Job job, string? line)
    {
        if (line is null)
            return;

        lock (job.Gate)
        {
            job.Lines.Enqueue(line);

            while (job.Lines.Count > KeepLines)
            {
                job.Lines.Dequeue();
                job.Dropped++;
            }
        }
    }

    private static ProcessStatus Status(Job job)
    {
        bool exited;
        int? code = null;

        try
        {
            exited = job.Process.HasExited;

            if (exited)
                code = job.Process.ExitCode;
        }
        catch (Exception)
        {
            exited = true;
        }

        int lines;

        lock (job.Gate)
            lines = job.Lines.Count + job.Dropped;

        return new ProcessStatus(
            job.Id,
            job.Command,
            SafePid(job.Process),
            !exited,
            code,
            job.Started,
            DateTime.Now - job.Started,
            lines);
    }

    private static int SafePid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Kill everything still running.
    ///
    /// Called when the application closes. A child left behind holding a port or a file,
    /// with nothing left that can show or stop it, is worse than losing the run: the user
    /// has no way to find out it is there.
    /// </summary>
    public void Dispose()
    {
        foreach (Job job in _jobs.Values)
        {
            try
            {
                if (!job.Process.HasExited)
                    job.Process.Kill(entireProcessTree: true);

                job.Process.Dispose();
            }
            catch (Exception)
            {
                // Shutdown is not the place to propagate a failure to tidy up.
            }
        }

        _jobs.Clear();
    }
}
