using Shellvis.Core.Desktop;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Checks what <see cref="ProgramLauncher"/> refuses, and that it never dresses a guess
/// up as a result.
///
/// This harness exists because of a live failure. A model asked to open the calculator
/// guessed "calc://"; ShellExecute on a scheme with no handler does not fail, it opens a
/// modal system dialog, so the launch blocked for the full fifteen-second window budget
/// and the wait then returned an unrelated Snipping Tool window that happened to appear in
/// the meantime. The transcript read "started 'calc://' (handed off to SnippingTool)" --
/// a failed launch reported as a success, which is the worst outcome available.
///
/// Both halves are checked here, and the refusal half is checked WITHOUT launching
/// anything: the whole point of the fix is that nothing reaches ShellExecute.
/// </summary>
internal static class LaunchProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("-- schemes that would raise a dialog --");

        foreach (string bogus in new[] { "notarealscheme://x", "zzz:something" })
        {
            failures += Expect(
                ProgramLauncher.WouldRefuse(bogus, out string? why)
                    && why!.Contains("no handler registered", StringComparison.Ordinal),
                $"'{bogus}' is refused before the shell sees it");

            // The refusal has to name the way out, or the model spends its next round
            // guessing again -- which is how three rounds went missing in the live run.
            failures += Expect(
                why!.Contains("command name", StringComparison.Ordinal),
                "  and the refusal says what to pass instead");
        }

        failures += Expect(
            !ProgramLauncher.WouldRefuse("calc://", out _)
                && ProgramLauncher.Resolve("calc://") == "calc.exe",
            "'calc://' is understood as the calculator rather than refused");

        Console.WriteLine();
        Console.WriteLine("-- things that must still be allowed through --");

        foreach (string ok in new[]
        {
            @"C:\Windows\System32\notepad.exe",
            "https://example.org",
            @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            @"D:\some\report.docx",
        })
        {
            failures += Expect(
                !ProgramLauncher.WouldRefuse(ok, out _),
                $"'{ok}' is not mistaken for an unusable URI");
        }

        Console.WriteLine();
        Console.WriteLine("-- known names --");

        foreach ((string name, string expect) in new[]
        {
            ("calc", "calc.exe"),
            ("Rechner", "calc.exe"),
            ("notepad", "notepad.exe"),
            ("einstellungen", "ms-settings:"),
        })
        {
            failures += Expect(
                ProgramLauncher.Resolve(name) == expect,
                $"'{name}' resolves to {expect}");
        }

        // A name that is not in the table must pass through untouched rather than being
        // guessed at: the table is a shortcut for the common case, not a filter.
        failures += Expect(
            ProgramLauncher.Resolve("someinternaltool") == "someinternaltool",
            "an unknown name is passed through unchanged");

        Console.WriteLine();
        Console.WriteLine("-- attributing a window to a launch --");

        // Synthetic window lists, not a real launch. An earlier version of this harness
        // launched cmd.exe and asserted that nothing could be attributed; it passed alone
        // and failed inside a full sweep, because another harness had left a cmd window
        // open and the already-running branch matched it -- correctly. The ranking is
        // deterministic, the desktop is not, so the ranking is what gets tested.
        WindowInfo popup = Fake(1, "", "TooltipWindow", 900);
        WindowInfo frame = Fake(2, "Rechner", "ApplicationFrameHost", 44836);
        WindowInfo notepad = Fake(3, "Editor", "Notepad", 27204);
        WindowInfo stranger = Fake(4, "Terminal", "WindowsTerminal", 700);

        failures += Expect(
            ProgramLauncher.Attribute([notepad], [notepad], 27204, "notepad") == notepad,
            "a new window from the launched process is attributed");

        failures += Expect(
            ProgramLauncher.Attribute([frame], [frame], 48348, "calc") == frame,
            "a new frame-host window is attributed to a packaged app");

        failures += Expect(
            ProgramLauncher.Attribute([notepad], [notepad], 999, "notepad") == notepad,
            "a hand-off to a differently-numbered process is attributed by name");

        failures += Expect(
            ProgramLauncher.Attribute([], [notepad], 999, "notepad") == notepad,
            "an already-running instance is attributed when no window appears");

        // The defect this whole harness exists for: an unrelated window opening during
        // the wait must NOT become the answer.
        failures += Expect(
            ProgramLauncher.Attribute([popup, stranger], [popup, stranger], 999, "calc") is null,
            "an unrelated window that merely appeared is NOT attributed");

        // A pid of 0 means the shell would not say which process took the request, so it
        // must not match a window that happens to report no owner.
        failures += Expect(
            ProgramLauncher.Attribute([Fake(5, "", "Something", 0)], [], 0, "calc") is null,
            "a pid of zero does not match a window with no owner");

        Console.WriteLine();
        Console.WriteLine("-- and the wording when nothing can be attributed --");

        // waitForWindow: false never attributes anything, which makes this deterministic
        // regardless of what is on screen.
        LaunchResult quiet = await ProgramLauncher
            .LaunchAsync("cmd.exe", "/c exit", waitForWindow: false)
            .ConfigureAwait(false);

        Console.WriteLine($"    {quiet.Detail}");

        failures += Expect(
            quiet.MainWindow is null
                && !quiet.Detail.Contains("handed off to", StringComparison.Ordinal),
            "no window is claimed, and no hand-off that did not happen");

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: unusable URIs are refused before the shell, and a window that "
              + "cannot be attributed is reported as one."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>A window that never existed, for testing the ranking without a desktop.</summary>
    private static WindowInfo Fake(nint handle, string title, string process, int pid) =>
        new(handle, title, "Fake", pid, process, 0, 0, 100, 100, WindowDisplayState.Normal, false);

    private static int Expect(bool condition, string what)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }
}
