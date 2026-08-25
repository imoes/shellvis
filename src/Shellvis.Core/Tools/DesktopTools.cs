using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using Shellvis.Core.Desktop;

namespace Shellvis.Core.Tools;

/// <summary>
/// The desktop capabilities, exposed as tools a model can call.
///
/// Two conventions run through all of it.
///
/// Windows are addressed by a substring of their title or process name, never by a
/// handle. A handle is a meaningless number to a language model and gets transposed
/// or hallucinated; a title is what the user would say out loud. Titles are volatile
/// though (Notepad renames its window the moment the document is dirty), so the
/// process name is accepted as a stable fallback. Ambiguity is answered with the
/// candidate list rather than a guess: picking the wrong window and clicking in it is
/// far worse than asking again.
///
/// Elements are addressed by snapshot reference. Every action therefore requires an
/// analyze call first, which is not friction but the point: it forces the model to
/// look before it acts, and a reference that has gone stale fails loudly instead of
/// clicking whatever moved into that position.
///
/// Not thread-safe. UI Automation is COM and wants one consistent apartment, so a
/// single instance must be driven from a single thread.
/// </summary>
public sealed class DesktopTools : IDisposable
{
    private readonly DesktopAnalyzer _analyzer = new();

    /// <summary>The most recent snapshot, so the model may omit the id in the common case.</summary>
    private string? _lastSnapshotId;

    /// <summary>
    /// What the last capture was made from, so it can be repeated after an action.
    ///
    /// Kept because a click can now report whether the references survived it, which
    /// needs the same window and the same budgets -- a re-capture with different limits
    /// would produce a different element count and look like a structural change.
    /// </summary>
    private nint _lastWindow;
    private int _lastMaxElements = 400;
    private int _lastMaxDepth = 12;
    private bool _lastInteractiveOnly;
    private int _lastElementCount;

    [ShellvisTool(
        "window_list",
        SideEffect.ReadOnly,
        Description =
            "List the visible top-level windows on the desktop, front to back in "
            + "Z-order. Use this first to find out what is open and to get a window "
            + "title you can pass to other desktop tools.",
        Glyph = "window")]
    public string ListWindows(
        bool includeUntitled = false)
    {
        IReadOnlyList<WindowInfo> windows = WindowInspector.ListWindows(includeUntitled);

        if (windows.Count == 0)
            return "No visible windows.";

        var sb = new StringBuilder();
        sb.Append(windows.Count).AppendLine(" visible windows, front to back:");
        foreach (WindowInfo w in windows)
            sb.Append("  ").AppendLine(w.ToString());

        return sb.ToString();
    }

    [ShellvisTool(
        "desktop_analyze",
        SideEffect.ReadOnly,
        Description =
            "Read the UI structure of a window as a tree of elements, each with a "
            + "reference like @e12 and the actions it supports. This is how you find "
            + "out what is on screen and what can be clicked. Returns a snapshot id "
            + "that the ui_* action tools need.",
        PreviewParameter = "windowTitle",
        Glyph = "tree")]
    public string Analyze(
        string? windowTitle = null,
        bool interactiveOnly = false,
        int maxElements = 400,
        int maxDepth = 12)
    {
        WindowInfo? target = ResolveWindow(windowTitle, out string? problem);
        if (target is null)
            return problem!;

        DesktopSnapshot snapshot = _analyzer.Capture(
            target.Handle,
            Math.Clamp(maxElements, 20, 2000),
            Math.Clamp(maxDepth, 1, 30),
            interactiveOnly);

        _lastSnapshotId = snapshot.SnapshotId;
        _lastWindow = target.Handle;
        _lastMaxElements = Math.Clamp(maxElements, 20, 2000);
        _lastMaxDepth = Math.Clamp(maxDepth, 1, 30);
        _lastInteractiveOnly = interactiveOnly;
        _lastElementCount = snapshot.ElementCount;

        var sb = new StringBuilder();
        sb.Append("snapshot ").Append(snapshot.SnapshotId)
          .Append(", ").Append(snapshot.ElementCount).AppendLine(" elements");
        sb.Append(snapshot.ToPromptText());
        return sb.ToString();
    }

    [ShellvisTool(
        "ui_click",
        SideEffect.Mutating,
        Description =
            "Activate an element from a desktop_analyze snapshot: press a button, "
            + "tick a checkbox, choose a list item, open a menu. Pass the reference "
            + "such as @e12. Set forceMouse when an app only responds to a real click.",
        PreviewParameter = "elementRef",
        Glyph = "click")]
    public string Click(
        string elementRef,
        string? snapshotId = null,
        bool forceMouse = false,
        bool rightClick = false,
        bool doubleClick = false)
    {
        if (!TryResolve(snapshotId, elementRef, out AutomationElement? element, out string? error))
            return error!;

        ActionResult result = rightClick
            ? DesktopActions.RightClick(element!)
            : doubleClick
                ? DesktopActions.DoubleClick(element!)
                : DesktopActions.Click(element!, forceMouse);

        if (!result.Succeeded)
            return result.ToString();

        // Say whether the references still hold, instead of always demanding a fresh
        // snapshot.
        //
        // The blanket "run desktop_analyze again" was correct but expensive: it cost a
        // round trip per click, so clicking 7 x 6 = on a calculator took eleven rounds
        // and ran out of the iteration budget before reaching the answer. Most clicks --
        // a digit, a checkbox, a toggle -- leave the tree exactly as it was. Re-taking
        // the snapshot HERE costs milliseconds and lets the model click again
        // immediately; only a click that really restructured the window forces it to
        // look again.
        return result + "\n" + DescribeAftermath();
    }

    /// <summary>
    /// Re-read the window and report whether the previous references are still usable.
    /// </summary>
    private string DescribeAftermath()
    {
        if (_lastWindow == 0)
            return "Run desktop_analyze again before the next action.";

        try
        {
            DesktopSnapshot refreshed = _analyzer.Capture(
                _lastWindow, _lastMaxElements, _lastMaxDepth, _lastInteractiveOnly);

            // Same number of addressable elements in the same window means the reference
            // table still describes the screen. A count comparison is a cheap check
            // rather than a proof -- but the alternative was assuming the worst on every
            // single click, which is what made a four-click task cost eleven rounds.
            bool stable = refreshed.ElementCount == _lastElementCount;

            if (!stable)
            {
                // Only NOW does the active snapshot change, because the model is being
                // handed the new tree in the same breath.
                _lastSnapshotId = refreshed.SnapshotId;
                _lastElementCount = refreshed.ElementCount;

                return $"The window changed structurally ({refreshed.ElementCount} elements now), "
                    + $"so here is the new tree as snapshot {refreshed.SnapshotId}:\n"
                    + refreshed.ToPromptText();
            }

            // The active snapshot is deliberately NOT replaced.
            //
            // An earlier revision did replace it, and that was a correctness bug worse
            // than the cost it saved: the model kept using the references it had been
            // given, but they were then resolved against the NEW tree, where the same
            // index can be a different button. Clicking 7, 6, x, = on a calculator
            // produced "71". Equal element counts do not imply equal ordering.
            //
            // Keeping the old snapshot is right rather than merely safe: UI Automation
            // element objects stay valid as long as the element exists, so the
            // references the model holds keep pointing at exactly what it saw. One that
            // has genuinely gone fails loudly on the next use.
            return "The layout is unchanged, so your existing references still point at "
                + "the same elements. Click the next one directly; use ui_read_text when "
                + "you need to see what the clicks produced.";
        }
        catch (Exception)
        {
            // The window may have closed or stopped answering. The honest advice is then
            // the old one.
            return "Run desktop_analyze again before the next action.";
        }
    }

    [ShellvisTool(
        "ui_set_text",
        SideEffect.Mutating,
        Description =
            "Put text into an editable element from a desktop_analyze snapshot. "
            + "Set forceTyping for controls that validate each keystroke and only "
            + "behave correctly when characters arrive one at a time.",
        PreviewParameter = "text",
        Glyph = "type")]
    public string SetText(
        string elementRef,
        string text,
        string? snapshotId = null,
        bool forceTyping = false)
    {
        if (!TryResolve(snapshotId, elementRef, out AutomationElement? element, out string? error))
            return error!;

        return DesktopActions.SetText(element!, text, forceTyping).ToString();
    }

    [ShellvisTool(
        "ui_send_keys",
        SideEffect.Mutating,
        Description =
            "Send a key or key combination to the desktop, for example \"Enter\", "
            + "\"Ctrl+S\", \"Alt+F4\", \"F5\", \"Escape\". Pass elementRef to give that "
            + "element the focus first. Use this for shortcuts and for keys nothing "
            + "exposes as a clickable action; use ui_click to press a button and "
            + "ui_set_text to fill a field.",
        PreviewParameter = "keys",
        Glyph = "type")]
    public string SendKeys(
        string keys,
        string? elementRef = null,
        string? snapshotId = null)
    {
        if (!TryParseCombination(keys, out VirtualKeyShort key, out VirtualKeyShort[] modifiers, out string? parseError))
            return parseError!;

        AutomationElement? focus = null;

        if (!string.IsNullOrWhiteSpace(elementRef))
        {
            if (!TryResolve(snapshotId, elementRef, out focus, out string? error))
                return error!;
        }

        return DesktopActions.SendKeys(focus, key, modifiers).ToString();
    }

    /// <summary>
    /// Turn "Ctrl+Shift+S" into a key and its modifiers.
    ///
    /// A combination string rather than separate key and modifier parameters, because
    /// that is how a shortcut is written everywhere a model will have read about one, and
    /// a shape the model already knows is filled in correctly more often than one it has
    /// to be taught. An unknown name comes back as readable text listing what is
    /// accepted, so the next round can correct itself.
    /// </summary>
    private static bool TryParseCombination(
        string combination,
        out VirtualKeyShort key,
        out VirtualKeyShort[] modifiers,
        out string? error)
    {
        key = default;
        modifiers = [];
        error = null;

        string[] parts = (combination ?? string.Empty)
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            error = "no keys were given. Pass something like \"Enter\" or \"Ctrl+S\".";
            return false;
        }

        // The last token is the key; everything before it is a modifier held down for it.
        if (!TryParseKey(parts[^1], out key))
        {
            error = $"'{parts[^1]}' is not a key I recognise. Accepted: single letters and "
                + "digits, F1 to F24, Enter, Escape, Tab, Space, Backspace, Delete, Insert, "
                + "Home, End, PgUp, PgDn, and the four arrow keys. Combine them with '+', "
                + "for example \"Ctrl+Shift+S\".";
            return false;
        }

        var held = new List<VirtualKeyShort>(parts.Length - 1);

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!TryParseKey(parts[i], out VirtualKeyShort modifier))
            {
                error = $"'{parts[i]}' is not a modifier I recognise. Use Ctrl, Alt, Shift or Win.";
                return false;
            }

            held.Add(modifier);
        }

        modifiers = [.. held];
        return true;
    }

    private static bool TryParseKey(string name, out VirtualKeyShort key)
    {
        key = default;

        if (string.IsNullOrWhiteSpace(name))
            return false;

        string cleaned = name.Trim();

        // Spellings a model will reach for that the enum does not carry. FlaUI already
        // provides ENTER, ESC and ALT as aliases, so only the rest need translating.
        string canonical = cleaned.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => "CONTROL",
            "WIN" or "WINDOWS" or "META" or "SUPER" => "LWIN",
            "PGUP" or "PAGEUP" => "PRIOR",
            "PGDN" or "PAGEDOWN" => "NEXT",
            "BACKSPACE" => "BACK",
            "PLUS" => "ADD",
            "MINUS" => "SUBTRACT",
            _ => cleaned,
        };

        // A bare letter or digit is not the enum's own spelling, which prefixes both.
        if (canonical.Length == 1 && char.IsLetterOrDigit(canonical[0]))
            canonical = "KEY_" + char.ToUpperInvariant(canonical[0]);

        return Enum.TryParse(canonical, ignoreCase: true, out key);
    }

    [ShellvisTool(
        "ui_read_text",
        SideEffect.ReadOnly,
        Description =
            "Read the text content of an element from a desktop_analyze snapshot. "
            + "Use this to check the result of an action, or to read a document, "
            + "message or field that the snapshot truncated.",
        PreviewParameter = "elementRef",
        Glyph = "read")]
    public string ReadText(
        string elementRef,
        string? snapshotId = null)
    {
        if (!TryResolve(snapshotId, elementRef, out AutomationElement? element, out string? error))
            return error!;

        string text = DesktopActions.ReadText(element!);
        return string.IsNullOrEmpty(text) ? "(the element exposes no text)" : text;
    }

    [ShellvisTool(
        "window_focus",
        SideEffect.Mutating,
        Description =
            "Bring a window to the front and restore it if minimized. Needed before "
            + "any action that relies on real mouse or keyboard input.",
        PreviewParameter = "windowTitle",
        Glyph = "focus")]
    public string FocusWindow(
        string windowTitle)
    {
        WindowInfo? target = ResolveWindow(windowTitle, out string? problem);
        if (target is null)
            return problem!;

        // Windows refuses SetForegroundWindow from a process that does not already own
        // the foreground, so this genuinely can fail through no fault of ours. Report
        // it rather than claiming success.
        return WindowInspector.Activate(target.Handle)
            ? $"focused: {target}"
            : $"could not focus \"{target.Title}\": Windows refused the foreground change. "
              + "The user may need to click the window once.";
    }

    [ShellvisTool(
        "program_open",
        SideEffect.Mutating,
        Description =
            "Start a program and wait until its window is ready to drive. Pass the plain "
            + "command name of the program (calc, notepad, mspaint, explorer), or a full "
            + "path, or the path of a document to open it with, or a shell:AppsFolder "
            + "AUMID for a packaged app. Do not invent a URI scheme such as calc:// -- "
            + "Windows has no handler for it and asks the user to pick an app. Returns "
            + "the window that appeared.",
        PreviewParameter = "target",
        Glyph = "launch")]
    public async Task<string> OpenProgram(
        string target,
        string? arguments = null,
        int timeoutSeconds = 15,
        CancellationToken cancellationToken = default)
    {
        LaunchResult result = await ProgramLauncher.LaunchAsync(
            target,
            arguments,
            workingDirectory: null,
            waitForWindow: true,
            timeout: TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)),
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return $"error: {result.Detail}";

        // Point at the PROCESS name rather than the title. An app renames its window
        // the moment a document is dirty, so a title handed over here is frequently
        // stale by the very next tool call -- which cost a live run two extra rounds
        // before this was fixed.
        return result.MainWindow is null
            ? result.Detail
            : $"{result.Detail}\nRun desktop_analyze on \"{result.MainWindow.ProcessName}\" to see inside it.";
    }

    [ShellvisTool(
        "screen_capture",
        SideEffect.ReadOnly,
        Description =
            "Save a PNG screenshot and return its path. Prefer desktop_analyze for "
            + "deciding what to click: it is far more compact and carries element "
            + "names. Reach for a screenshot when the UI tree cannot answer the "
            + "question, such as custom-drawn surfaces, charts or rendered documents.",
        PreviewParameter = "windowTitle",
        Glyph = "camera")]
    public string Capture(
        string? windowTitle = null,
        bool allMonitors = false)
    {
        if (allMonitors)
            return ScreenCapture.CaptureAllScreens().ToString();

        if (windowTitle is null)
            return ScreenCapture.CaptureForegroundWindow().ToString();

        WindowInfo? target = ResolveWindow(windowTitle, out string? problem);
        return target is null ? problem! : ScreenCapture.CaptureWindow(target.Handle).ToString();
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// Find the window a title substring refers to.
    ///
    /// Exact matches win over partial ones, so "Notepad" reaches the editor rather
    /// than a browser tab that happens to mention it. Genuine ambiguity returns the
    /// candidates instead of picking one, because acting in the wrong window is a
    /// worse outcome than another round trip.
    /// </summary>
    private static WindowInfo? ResolveWindow(string? titleFragment, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(titleFragment))
        {
            WindowInfo? foreground = WindowInspector.Foreground();
            if (foreground is not null)
                return foreground;

            problem = "error: no window has focus, and no window title was given.";
            return null;
        }

        List<WindowInfo> windows = WindowInspector.ListWindows().ToList();

        WindowInfo? exact = windows.FirstOrDefault(
            w => w.Title.Equals(titleFragment, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        List<WindowInfo> partial = windows
            .Where(w => w.Title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Titles are volatile: Notepad renames its window the moment the document is
        // dirty, browsers rename on every tab switch. So a title captured by an
        // earlier tool call may already be wrong. Falling back to the process name
        // gives the model a stable handle on the same app.
        if (partial.Count == 0)
        {
            partial = windows
                .Where(w => w.ProcessName.Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (partial.Count == 1)
            return partial[0];

        if (partial.Count == 0)
        {
            var sb = new StringBuilder();
            sb.Append("error: no window matches \"").Append(titleFragment)
              .AppendLine("\", by title or by process name.");
            sb.AppendLine("Open windows are:");
            foreach (WindowInfo w in windows.Take(15))
                sb.Append("  ").AppendLine(w.ToString());

            problem = sb.ToString();
            return null;
        }

        var ambiguous = new StringBuilder();
        ambiguous.Append("error: \"").Append(titleFragment)
                 .AppendLine("\" matches several windows. Use a longer fragment:");
        foreach (WindowInfo w in partial)
            ambiguous.Append("  ").AppendLine(w.Title);

        problem = ambiguous.ToString();
        return null;
    }

    private bool TryResolve(
        string? snapshotId,
        string elementRef,
        out AutomationElement? element,
        out string? error)
    {
        element = null;
        error = null;

        string? id = snapshotId ?? _lastSnapshotId;
        if (id is null)
        {
            error = "error: no snapshot exists yet. Call desktop_analyze first.";
            return false;
        }

        try
        {
            element = _analyzer.Resolve(id, elementRef);
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            // These are the two conditions the model can actually recover from, and
            // the exception messages already say what to do about them.
            error = $"error: {ex.Message}";
            return false;
        }
    }

    public void Dispose() => _analyzer.Dispose();
}
