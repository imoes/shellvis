using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;

namespace Shellvis.Core.Shell;

/// <summary>The outcome of one PowerShell invocation.</summary>
/// <param name="Output">Formatted output, as a console would show it.</param>
/// <param name="Errors">Error records, separated so the agent can tell them from output.</param>
/// <param name="Warnings">Warning stream.</param>
/// <param name="HadErrors">Whether anything landed in the error stream.</param>
/// <param name="Duration">Wall clock time.</param>
/// <param name="NewModules">
/// Modules that became available during this call. This is the payload that lets a
/// freshly imported module be used immediately.
/// </param>
public sealed record ShellResult(
    string Output,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    bool HadErrors,
    TimeSpan Duration,
    IReadOnlyList<string> NewModules)
{
    /// <summary>
    /// Render for the model: output first, then errors, with the streams labelled.
    ///
    /// Kept as one text blob rather than a structured object because that is what a
    /// model reads best, and because PowerShell's own formatting is usually the
    /// clearest available rendering of its objects.
    /// </summary>
    public string ToToolText()
    {
        var sb = new StringBuilder();

        if (Output.Length > 0)
            sb.AppendLine(Output.TrimEnd());

        foreach (string warning in Warnings)
            sb.Append("WARNING: ").AppendLine(warning);

        foreach (string error in Errors)
            sb.Append("ERROR: ").AppendLine(error);

        if (sb.Length == 0)
            sb.AppendLine("(no output)");

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Hosts PowerShell 7 in-process and keeps one runspace alive for the session.
///
/// Two reasons the runspace is persistent rather than per-call.
///
/// State has to survive between turns. A model that imports a module, sets a variable
/// or changes directory in one call and finds it gone in the next cannot work
/// incrementally, which is how anyone actually uses a shell.
///
/// And the module diff depends on it. Every invocation compares the loaded module set
/// before and after, so a module the script imported can be reported back with its new
/// commands attached. That is what closes the loop between "load a module" and "use its
/// cmdlets" without stuffing ten thousand cmdlets into the system prompt.
///
/// Not thread-safe: a runspace serves one pipeline at a time. Callers must not invoke
/// concurrently on the same host.
/// </summary>
public sealed class PowerShellHost : IDisposable
{
    /// <summary>Output width used when formatting objects. Wide enough for real tables.</summary>
    private const int FormatWidth = 200;

    private readonly Runspace _runspace;
    private HashSet<string> _knownModules;
    private bool _disposed;

    public PowerShellHost()
    {
        // The hosted engine has to be told where its own modules are before anything
        // else, or the first Out-String call fails with "the module could not be
        // loaded". See ResolveBundledModulePath for why this is not automatic.
        EnsureBundledModulesOnPath();

        // Set process-wide so it is inherited by anything PowerShell launches.
        //
        // WslRunner sets this per invocation, but a model will happily reach for
        // "wsl -l -v" through powershell_run instead of the dedicated WSL tool, and
        // that path bypasses WslRunner entirely. Without the variable, wsl.exe writes
        // UTF-16LE and the output arrives as "D e b i a n   R u n n i n g" -- which a
        // capable model can still read, but only by wasting tokens on it.
        Environment.SetEnvironmentVariable("WSL_UTF8", "1");

        // CreateDefault2 loads only the core engine modules instead of everything on
        // the machine, which cuts startup from seconds to well under one. Anything
        // else the agent needs, it can import explicitly -- and it will be told what
        // that import made available.
        InitialSessionState state = InitialSessionState.CreateDefault2();

        // Utility and Management are not optional in practice: Out-String,
        // Group-Object, Select-Object and Get-Content all live in them, and every
        // formatted result depends on Utility. Importing them up front turns a
        // confusing runtime failure into a startup cost.
        // CimCmdlets matters as much as the other two: Get-CimInstance is the
        // modern way to ask Windows almost anything about itself, and a model
        // reaches for it constantly.
        foreach (string module in new[]
        {
            "Microsoft.PowerShell.Utility",
            "Microsoft.PowerShell.Management",
            "CimCmdlets",
            "Microsoft.PowerShell.Security",
            "Microsoft.PowerShell.Diagnostics",
        })
        {
            state.ImportPSModule(module);
        }

        // The agent is the thing being gated by the approval engine, so the runspace
        // itself does not need to fight it. Restricting here would only push the model
        // towards shelling out to powershell.exe to get around it.
        state.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        _runspace = RunspaceFactory.CreateRunspace(state);
        _runspace.Open();

        _knownModules = ReadLoadedModules();
    }

    /// <summary>Modules currently loaded in this runspace.</summary>
    public IReadOnlyCollection<string> LoadedModules => _knownModules;

    /// <summary>
    /// Where the bundled engine modules were found, or null if none were. Exposed for
    /// diagnostics: a host that silently cannot format its output is very hard to
    /// debug from the outside.
    /// </summary>
    public static string? BundledModulePath { get; private set; }

    /// <summary>
    /// Put the SDK's own modules on PSModulePath.
    ///
    /// PowerShell locates its built-in modules relative to System.Management.Automation.dll,
    /// expecting a sibling "Modules" directory. The SDK ships them under
    /// runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;/Modules, which works by accident in a RID-less build
    /// because the assembly stays down there beside them.
    ///
    /// A RID-specific build (any app with a RuntimeIdentifier, which includes every
    /// WinUI app) flattens the assemblies to the output root while leaving Modules
    /// behind under runtimes/. The assembly and its modules are then in different
    /// places and every command outside Microsoft.PowerShell.Core fails to resolve.
    /// That is exactly what happened here: Get-Module worked, Out-String did not.
    ///
    /// So the directory is discovered rather than assumed, which covers the flattened
    /// layout, the RID-less layout and a self-contained publish alike.
    /// </summary>
    private static void EnsureBundledModulesOnPath()
    {
        string? modules = BundledModulePath ??= ResolveBundledModulePath();
        if (modules is null)
            return;

        string current = Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty;

        if (current.Contains(modules, StringComparison.OrdinalIgnoreCase))
            return;

        // Prepended, so the engine's own version wins over anything the machine has
        // installed. A Windows PowerShell 5.1 copy of the same module on the path
        // would otherwise shadow it and fail to load into PowerShell 7.
        Environment.SetEnvironmentVariable(
            "PSModulePath",
            current.Length == 0 ? modules : modules + Path.PathSeparator + current);
    }

    private static string? ResolveBundledModulePath()
    {
        string root = AppContext.BaseDirectory;

        // The flattened layout first: that is what a published app looks like.
        string flat = Path.Combine(root, "Modules");
        if (Directory.Exists(Path.Combine(flat, "Microsoft.PowerShell.Utility")))
            return flat;

        // Then the runtimes layout. Globbing rather than hard-coding the RID and TFM,
        // because both change with the build configuration.
        string runtimes = Path.Combine(root, "runtimes");
        if (!Directory.Exists(runtimes))
            return null;

        foreach (string candidate in Directory.EnumerateDirectories(
            runtimes, "Modules", SearchOption.AllDirectories))
        {
            if (Directory.Exists(Path.Combine(candidate, "Microsoft.PowerShell.Utility")))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Run a script and return its output.
    /// </summary>
    /// <param name="script">PowerShell to execute.</param>
    /// <param name="timeout">
    /// Wall clock budget. A hung pipeline would otherwise block the whole session,
    /// since the runspace serves one pipeline at a time.
    /// </param>
    public async Task<ShellResult> RunAsync(
        string script,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        HashSet<string> before = _knownModules;

        using PowerShell shell = PowerShell.Create();
        shell.Runspace = _runspace;

        // Out-String is what turns PowerShell objects into the table and list layouts
        // a human recognises. Serializing the raw objects instead would produce
        // something technically complete and practically unreadable.
        shell.AddScript(script)
             .AddCommand("Out-String")
             .AddParameter("Width", FormatWidth);

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(2);
        using var timeoutSource = new CancellationTokenSource(effectiveTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);

        string output;
        bool timedOut = false;
        string? terminating = null;

        try
        {
            PSDataCollection<PSObject> results = await InvokeAsync(shell, linked.Token)
                .ConfigureAwait(false);

            output = string.Concat(results.Select(r => r?.ToString() ?? string.Empty));
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            // A timeout is a result, not an exception: the model needs to know the
            // command was cut short rather than that it produced nothing.
            timedOut = true;
            output = string.Empty;
        }
        catch (Exception ex) when (ex is RuntimeException or PSInvalidOperationException)
        {
            // A TERMINATING PowerShell error, which is a result too.
            //
            // Only the timeout was caught here before, so a script containing `throw` or
            // any cmdlet with -ErrorAction Stop left this method as an exception and escaped
            // the tool layer entirely -- breaking the convention the whole tool design rests
            // on, that a failure comes back as text the model reads and corrects itself
            // from. Models write -ErrorAction Stop constantly, so this was not an edge case;
            // it surfaced the first time a remoting probe pointed at a name that does not
            // resolve, and the harness died instead of being told the host was unreachable.
            //
            // Recorded as an error rather than as output, so it is labelled ERROR: in the
            // rendering and cannot be mistaken for something the command returned.
            terminating = ex.Message;
            output = string.Empty;
        }

        clock.Stop();

        HashSet<string> after = ReadLoadedModules();
        _knownModules = after;

        List<string> newModules = after.Except(before, StringComparer.OrdinalIgnoreCase).ToList();

        var errors = shell.Streams.Error
            .Select(e => e.ToString())
            .ToList();

        if (timedOut)
        {
            errors.Add(
                $"the command was still running after {effectiveTimeout.TotalSeconds:F0}s and was stopped");
        }

        // Prepended: a terminating error is the reason everything after it did not happen,
        // so it belongs above any errors the pipeline had managed to record first.
        if (terminating is not null)
            errors.Insert(0, terminating);

        return new ShellResult(
            Output: output,
            Errors: errors,
            Warnings: shell.Streams.Warning.Select(w => w.Message).ToList(),
            HadErrors: errors.Count > 0,
            Duration: clock.Elapsed,
            NewModules: newModules);
    }

    /// <summary>
    /// Bridge PowerShell's async invocation onto a cancellable Task.
    ///
    /// PowerShell exposes Begin/EndInvoke rather than a Task, and its own Stop is the
    /// only way to abort a running pipeline, so cancellation has to be wired through
    /// explicitly instead of relying on the token alone.
    /// </summary>
    private static async Task<PSDataCollection<PSObject>> InvokeAsync(
        PowerShell shell, CancellationToken cancellationToken)
    {
        var output = new PSDataCollection<PSObject>();
        var completion = new TaskCompletionSource<PSDataCollection<PSObject>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // The completion handle comes from the callback argument rather than the
        // BeginInvoke return value: the callback can fire before the assignment
        // completes, so closing over the variable would race.
        shell.BeginInvoke<PSObject, PSObject>(
            input: null,
            output: output,
            settings: null,
            callback: asyncResult =>
            {
                try
                {
                    shell.EndInvoke(asyncResult);
                    completion.TrySetResult(output);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            },
            state: null);

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            // Stop unwinds the pipeline; the callback still fires, so the completion
            // source is resolved as cancelled first to win the race.
            completion.TrySetCanceled(cancellationToken);
            try
            {
                shell.Stop();
            }
            catch (InvalidOperationException)
            {
                // Already finished between the check and the stop.
            }
        });

        return await completion.Task.ConfigureAwait(false);
    }

    private HashSet<string> ReadLoadedModules()
    {
        try
        {
            using PowerShell shell = PowerShell.Create();
            shell.Runspace = _runspace;
            shell.AddCommand("Get-Module").AddParameter("ErrorAction", "SilentlyContinue");

            Collection<PSObject> modules = shell.Invoke();

            return modules
                .Select(m => m.Properties["Name"]?.Value?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is RuntimeException or PSInvalidOperationException)
        {
            // Never let bookkeeping break the actual command that the user asked for.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Access the live runspace, for callers that need to build their own pipeline.</summary>
    internal Runspace Runspace => _runspace;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _runspace.Dispose();
    }
}
