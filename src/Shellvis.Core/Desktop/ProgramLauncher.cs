using System.ComponentModel;
using System.Diagnostics;

namespace Shellvis.Core.Desktop;

/// <summary>Outcome of a launch attempt.</summary>
/// <param name="Succeeded">Whether the program started.</param>
/// <param name="ProcessId">The process that was started. May have exited already, see remarks on the launcher.</param>
/// <param name="MainWindow">The window that appeared, if one did within the wait.</param>
/// <param name="Detail">What happened, in plain words.</param>
public sealed record LaunchResult(
    bool Succeeded,
    int ProcessId,
    WindowInfo? MainWindow,
    string Detail)
{
    public override string ToString() => Detail;
}

/// <summary>
/// Starts programs and waits for them to become drivable.
///
/// The waiting is the hard part, for two reasons.
///
/// First, Process.Start returns as soon as the process exists, long before a window
/// exists and longer still before UI Automation can see inside it. An agent that
/// snapshots immediately gets an empty tree and concludes the app is broken, so
/// launching and "ready to drive" are one operation here.
///
/// Second, and less obvious: on modern Windows the process you start is usually NOT
/// the process that owns the window. Running notepad.exe starts a launcher stub which
/// immediately exits and hands off to the packaged Notepad app under a different pid.
/// The same is true of Calculator, Photos, Settings and Terminal. Matching windows by
/// the launched pid therefore fails for most of the applications a user actually has.
/// So this class watches for windows that are NEW relative to a pre-launch baseline,
/// and ranks the candidates rather than demanding a pid match.
/// </summary>
public static class ProgramLauncher
{
    /// <summary>How long to wait for a window before giving up and reporting what we have.</summary>
    public static readonly TimeSpan DefaultWindowTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Poll cadence. Imperceptible to a model that takes seconds to think.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Launch a program and wait for its window.
    /// </summary>
    /// <param name="target">
    /// An executable path, or anything the shell can resolve: a document, a URL, a
    /// "shell:AppsFolder\..." AUMID for a packaged app, or a bare command on PATH.
    /// </param>
    /// <param name="arguments">Command-line arguments. Ignored for shell-resolved targets.</param>
    /// <param name="workingDirectory">Working directory, or null for the current one.</param>
    /// <param name="waitForWindow">
    /// Wait for a window to appear. Turn off for launches that legitimately have no UI.
    /// </param>
    /// <param name="timeout">Window wait budget. Defaults to <see cref="DefaultWindowTimeout"/>.</param>
    public static async Task<LaunchResult> LaunchAsync(
        string target,
        string? arguments = null,
        string? workingDirectory = null,
        bool waitForWindow = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target))
            return new LaunchResult(false, 0, null, "no program was given");

        if (!TryResolveTarget(target, out string resolved, out string? note, out string? refusal))
            return new LaunchResult(false, 0, null, refusal!);

        target = resolved;

        // Baseline must be taken BEFORE the launch, otherwise a window that opens
        // during startup is indistinguishable from one that was already there.
        HashSet<nint> before = WindowInspector
            .ListWindows(includeUntitled: true)
            .Select(w => w.Handle)
            .ToHashSet();

        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            // UseShellExecute is what lets a document, URL or AUMID work at all, and
            // it makes the launch respect the user's file associations rather than
            // second-guessing them.
            UseShellExecute = true,
        };

        if (!string.IsNullOrEmpty(arguments))
            startInfo.Arguments = arguments;

        if (!string.IsNullOrEmpty(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        int launchedPid;
        try
        {
            using Process? process = Process.Start(startInfo);

            // A null process means the shell handed the request to an already-running
            // instance (a second Explorer window, a new browser tab). That is success.
            launchedPid = process?.Id ?? 0;
        }
        catch (Win32Exception ex)
        {
            // Overwhelmingly: the file does not exist, or the user dismissed a UAC
            // prompt. Both are worth reporting precisely.
            return new LaunchResult(false, 0, null, $"could not start '{target}': {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return new LaunchResult(false, 0, null, $"could not start '{target}': {ex.Message}");
        }

        string prefix = note is null ? string.Empty : note + "; ";

        if (!waitForWindow)
        {
            return new LaunchResult(
                true, launchedPid,
                null,
                launchedPid == 0
                    ? $"{prefix}'{target}' was handed to a running instance"
                    : $"{prefix}started '{target}' (pid {launchedPid})");
        }

        (WindowInfo? window, bool attributed) = await WaitForLaunchedWindowAsync(
            before, launchedPid, target, timeout ?? DefaultWindowTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (window is not null && attributed)
        {
            string via = window.ProcessId == launchedPid
                ? string.Empty
                : $" (handed off to {window.ProcessName}, pid {window.ProcessId})";

            return new LaunchResult(
                true, launchedPid, window, $"{prefix}started '{target}'{via}; window: {window}");
        }

        // A window appeared, but nothing ties it to what was launched. Handing it back as
        // THE window is how a failed launch gets reported as a success: asked for calc://,
        // this returned an unrelated Snipping Tool window that happened to open during the
        // wait, and the transcript read "started 'calc://' (handed off to SnippingTool)".
        // A guess dressed as a result is worse than no result, so the candidate is named
        // and MainWindow stays empty.
        if (window is not null)
        {
            return new LaunchResult(
                true, launchedPid, null,
                $"{prefix}started '{target}', but no window could be attributed to it. One "
                + $"unrelated window did open while waiting: {window}. Do not assume that is "
                + "the program; check with window_list.");
        }

        return new LaunchResult(
            true, launchedPid, null,
            $"{prefix}started '{target}' but no window appeared within "
            + $"{(timeout ?? DefaultWindowTimeout).TotalSeconds:F0}s");
    }

    /// <summary>
    /// What would actually be handed to the shell for this target. Exposed so the
    /// substitution can be checked without launching anything.
    /// </summary>
    public static string Resolve(string target)
    {
        TryResolveTarget(target, out string resolved, out _, out _);
        return resolved;
    }

    /// <summary>
    /// Whether this target would be refused, and why. Exposed for the same reason:
    /// verifying that a bad URI never reaches ShellExecute must not involve a launch,
    /// because a launch is the thing being prevented.
    /// </summary>
    public static bool WouldRefuse(string target, out string? reason)
    {
        bool allowed = TryResolveTarget(target, out _, out _, out reason);
        return !allowed;
    }

    /// <summary>
    /// Well-known Windows applications by the name a person would say.
    ///
    /// Not a convenience layer over the shell: a defence against the round the model
    /// otherwise spends guessing. Asked to open the calculator, a model reaches for
    /// "calc://", "Calculator.exe" and the AUMID before it tries "calc" -- observed, in
    /// that order, three failed rounds in one turn. German names are here because the
    /// user asks in German and "Rechner" is what the window is actually called.
    /// </summary>
    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calc"] = "calc.exe",
        ["calculator"] = "calc.exe",
        ["rechner"] = "calc.exe",
        ["notepad"] = "notepad.exe",
        ["editor"] = "notepad.exe",
        ["paint"] = "mspaint.exe",
        ["mspaint"] = "mspaint.exe",
        ["explorer"] = "explorer.exe",
        ["dateiexplorer"] = "explorer.exe",
        ["settings"] = "ms-settings:",
        ["einstellungen"] = "ms-settings:",
        ["terminal"] = "wt.exe",
        ["cmd"] = "cmd.exe",
        ["taskmanager"] = "taskmgr.exe",
        ["taskmgr"] = "taskmgr.exe",
        ["snippingtool"] = "snippingtool.exe",
        ["wordpad"] = "write.exe",
        ["regedit"] = "regedit.exe",
        ["controlpanel"] = "control.exe",
        ["systemsteuerung"] = "control.exe",
    };

    /// <summary>
    /// Decide what to actually hand to the shell, and refuse what would raise a dialog.
    ///
    /// The refusal is the point. ShellExecute on a URI whose scheme has no handler does
    /// not fail -- it opens a modal system dialog ("Windows cannot open this link") that
    /// nobody is necessarily there to dismiss. Observed: a model guessed "calc://", the
    /// launch blocked for fifteen seconds behind that dialog, and the window wait then
    /// latched onto an unrelated window that opened in the meantime. Two defects from one
    /// bad string, and neither of them looked like a bad string in the transcript.
    ///
    /// So an unregistered scheme is caught before the launch. If its name happens to be a
    /// known application the intent is obvious and it is opened, with the substitution
    /// stated in the result rather than performed quietly.
    /// </summary>
    /// <param name="note">What was substituted, for the result text. Null when nothing was.</param>
    /// <param name="refusal">Why the launch will not be attempted. Null when it will.</param>
    private static bool TryResolveTarget(
        string target,
        out string resolved,
        out string? note,
        out string? refusal)
    {
        resolved = target.Trim();
        note = null;
        refusal = null;

        // A bare name with no path, no extension and no scheme: the case the table is for.
        if (!resolved.Contains(':') && !resolved.Contains('\\') && !resolved.Contains('/')
            && KnownApps.TryGetValue(resolved, out string? known))
        {
            note = $"read '{resolved}' as {known}";
            resolved = known;
            return true;
        }

        if (SchemeOf(resolved) is not { } scheme)
            return true;

        if (IsRegisteredScheme(scheme))
            return true;

        // "calc://" -- obvious intent, unusable spelling.
        if (KnownApps.TryGetValue(scheme, out string? viaScheme))
        {
            note = $"'{resolved}' is not a registered URI scheme, so I opened {viaScheme} instead";
            resolved = viaScheme;
            return true;
        }

        refusal =
            $"'{resolved}' looks like a URI but Windows has no handler registered for the "
            + $"'{scheme}:' scheme, so opening it would only raise a dialog asking the user "
            + "to choose an app. Pass the program's command name instead (for example 'calc' "
            + "or 'notepad'), a full path, or a shell:AppsFolder AUMID.";

        return false;
    }

    /// <summary>
    /// The scheme of a URI-looking string, or null if this is a path or a bare command.
    ///
    /// A Windows drive letter is deliberately not a scheme: "C:\Windows\..." parses as one
    /// by the grammar, and treating it as a URI would refuse every absolute path there is.
    /// </summary>
    private static string? SchemeOf(string target)
    {
        int colon = target.IndexOf(':');
        if (colon <= 0)
            return null;

        string scheme = target[..colon];

        if (scheme.Length == 1)
            return null;

        foreach (char c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
                return null;
        }

        // The shell resolves these itself and they are not registered as protocols.
        if (scheme.Equals("shell", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return scheme;
    }

    /// <summary>
    /// Whether a scheme has a handler. A registered protocol carries a "URL Protocol"
    /// value under its HKEY_CLASSES_ROOT key; that value, not the key's existence, is what
    /// the shell keys off, and plenty of unrelated keys share a name with a scheme.
    /// </summary>
    private static bool IsRegisteredScheme(string scheme)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme);

            return key?.GetValue("URL Protocol") is not null;
        }
        catch (Exception)
        {
            // An unreadable registry is not a reason to refuse a launch outright; better
            // to try it and report whatever actually happens.
            return true;
        }
    }

    /// <summary>
    /// Poll until a plausible window for the launch shows up.
    ///
    /// Polling rather than a window hook: a hook needs a message loop on a thread this
    /// class does not own, and at this cadence the difference is imperceptible.
    /// </summary>
    private static async Task<(WindowInfo? Window, bool Attributed)> WaitForLaunchedWindowAsync(
        HashSet<nint> before,
        int launchedPid,
        string target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string hint = Path.GetFileNameWithoutExtension(target);
        DateTime deadline = DateTime.UtcNow + timeout;
        WindowInfo? weakMatch = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<WindowInfo> current = WindowInspector.ListWindows();
            List<WindowInfo> fresh = current.Where(w => !before.Contains(w.Handle)).ToList();

            if (Attribute(fresh, current, launchedPid, hint) is { } found)
                return (found, true);

            // Remember any new window as a fallback, but keep looking for something
            // better until the budget runs out. It is returned UNATTRIBUTED, so the
            // caller reports it as a coincidence rather than as the program.
            weakMatch ??= fresh.FirstOrDefault();

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return (weakMatch, false);
    }

    /// <summary>
    /// Pick the window that belongs to a launch, or null if none can be justified.
    ///
    /// A pure function over window lists, and public for that reason: this ranking is the
    /// part worth pinning down, and testing it through a real launch makes the test
    /// depend on whatever else the machine happens to have open. It did -- an earlier
    /// version of the harness passed alone and failed inside a full sweep, because
    /// another harness had left a cmd window open and the already-running branch matched
    /// it. The logic is deterministic; the desktop is not.
    /// </summary>
    /// <param name="fresh">Windows that did not exist before the launch.</param>
    /// <param name="current">All windows, for the already-running case.</param>
    /// <param name="launchedPid">The process that was started, or 0.</param>
    /// <param name="hint">The target's bare name, e.g. "calc" for "calc.exe".</param>
    public static WindowInfo? Attribute(
        IReadOnlyList<WindowInfo> fresh,
        IReadOnlyList<WindowInfo> current,
        int launchedPid,
        string hint)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(current);

        // Best case: a brand-new window from the process we started. A pid of 0 means the
        // shell handed the request to a running instance and never told us which, so it
        // must not match every window with no owner.
        if (launchedPid > 0)
        {
            WindowInfo? exact = fresh.FirstOrDefault(w => w.ProcessId == launchedPid);
            if (exact is not null)
                return exact;
        }

        // The hand-off case: a brand-new window whose process name resembles what
        // we asked for.
        WindowInfo? byName = fresh.FirstOrDefault(w => ResemblesTarget(w.ProcessName, hint));
        if (byName is not null)
            return byName;

        // Or whose TITLE resembles it. "calc" launches a window called "Rechner",
        // so this will not always help -- but "notepad" opening "Editor - notepad"
        // and "mspaint" opening "Paint" both land here.
        WindowInfo? byTitle = fresh.FirstOrDefault(w => TitleResembles(w.Title, hint));
        if (byTitle is not null)
            return byTitle;

        // PACKAGED APPS. This is the case the earlier version got wrong, and it is
        // the common one: a UWP app's top-level window belongs to
        // ApplicationFrameHost, not to the app. Calculator is the example that
        // exposed it:
        //
        //     window   "Rechner"  [ApplicationFrameHost]  pid 44836
        //     process  CalculatorApp                      pid 27204
        //     stub     calc.exe                           pid 48348
        //
        // so the pid comparison fails (stub is not the frame host) and the name
        // comparison fails ("ApplicationFrameHost" does not resemble "calc"). A
        // brand-new frame-host window seconds after asking for a packaged app IS
        // that app; there is nothing else it could be.
        WindowInfo? framed = fresh.FirstOrDefault(w => IsPackagedHost(w.ProcessName));
        if (framed is not null)
            return framed;

        // Tabbed and already-running apps: launching Notepad while Notepad is open
        // adds a TAB, so no new window ever appears, and an app that was already
        // running produces none either.
        //
        // No longer gated on there being no other new window. It was, and that made the
        // whole branch dead whenever any unrelated window had appeared in the meantime --
        // a tooltip is enough. And no longer gated on the stub having exited: a launch
        // through the shell may not yield a usable pid at all, in which case HasExited
        // can never become true and the branch never ran.
        return current.FirstOrDefault(w => ResemblesTarget(w.ProcessName, hint))
            ?? current.FirstOrDefault(w => TitleResembles(w.Title, hint));
    }

    /// <summary>
    /// Whether a window's process name plausibly belongs to the launch target.
    ///
    /// Deliberately loose in one direction only: "notepad.exe" must match the process
    /// "Notepad", and "WindowsTerminal" must match "wt". Prefix comparison in both
    /// directions covers the real naming drift without matching unrelated apps.
    /// </summary>
    /// <summary>
    /// Processes that host another app's window rather than owning one.
    ///
    /// ApplicationFrameHost is the frame every packaged (UWP) app's top-level window
    /// belongs to: Calculator, Photos, Settings, Terminal, Clock. Matching it by name
    /// against the launch target is impossible, so it is recognised as a host instead.
    /// </summary>
    private static bool IsPackagedHost(string processName) =>
        processName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a window's title plausibly belongs to the launch target.
    ///
    /// Word-boundary aware rather than a bare Contains: "Editor - notepad" should match
    /// "notepad", while a browser tab that happens to mention the word should not decide
    /// the outcome on its own. It is only ever consulted after the pid and process-name
    /// checks have failed.
    /// </summary>
    private static bool TitleResembles(string title, string hint)
    {
        if (string.IsNullOrWhiteSpace(title) || hint.Length < 3)
            return false;

        return title.Split([' ', '-', '–', ':', '\\', '/', '.'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(word => word.Equals(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ResemblesTarget(string processName, string hint)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(hint))
            return false;

        return processName.Equals(hint, StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith(hint, StringComparison.OrdinalIgnoreCase)
            || hint.StartsWith(processName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExited(int pid)
    {
        if (pid <= 0)
            return true;

        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            // Already gone.
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Wait for a visible window owned by a specific process. Useful when the caller
    /// already knows the pid and does not need the hand-off heuristics.
    /// </summary>
    public static async Task<WindowInfo?> WaitForWindowAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            WindowInfo? match = WindowInspector
                .ListWindows()
                .FirstOrDefault(w => w.ProcessId == processId);

            if (match is not null)
                return match;

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}
