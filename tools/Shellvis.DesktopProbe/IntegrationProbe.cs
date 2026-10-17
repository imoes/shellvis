using Microsoft.Win32;

using Shellvis.Core.Desk;
using Shellvis.Core.Desktop;
using Shellvis.Core.Office;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Shellvis in Explorer and behind its own links.
///
/// <b>Why it is checked against the registry and not only as a list.</b> The failure that
/// matters is quiet: an entry that points at a deleted executable puts "Ask Shellvis" on
/// every file and does nothing when it is clicked, and a registration that is rewritten on
/// every start would take the menu away from an installed copy each time a test build runs.
/// Both are rules about what EnsureRegistered leaves alone, so they are run against a real
/// key -- a scratch one below HKCU\Software, never the live classes.
///
/// <b>And the rule that is about safety.</b> A registered scheme lets any web page hand
/// Shellvis an address. Those addresses must reach the closed set of link actions and never
/// become a prompt; that lives in the shell, which this project cannot load, so it is read
/// from the source.
/// </summary>
internal static class IntegrationProbe
{
    public static int Run()
    {
        int failures = 0;

        void Check(string what, bool passed, string detail = "")
        {
            if (!passed)
                failures++;

            Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        Console.WriteLine("integration: Explorer's menu, the shellvis: scheme, files from outside\n");

        // ------------------------------------------------------------------ the entries

        string exe = Path.Combine(Path.GetTempPath(), "shellvis-probe-" + Guid.NewGuid().ToString("N")[..8], "Shellvis Shell.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "not a program");

        IReadOnlyList<ShellIntegration.Entry> entries = ShellIntegration.Entries(exe, german: true);

        ShellIntegration.Entry? open = entries.FirstOrDefault(e => e.Key.EndsWith(@"open\command", StringComparison.Ordinal));
        Check("the scheme opens the executable with --open and the address",
            open is not null && open.Value == $"\"{exe}\" --open \"%1\"", open?.Value ?? "missing");

        Check("a path with a space survives being read back",
            open is not null && ShellIntegration.ExecutableOf(open.Value) == exe,
            ShellIntegration.ExecutableOf(open?.Value ?? string.Empty) ?? "null");

        Check("the scheme is marked as a URL protocol",
            entries.Any(e => e.Key == ShellIntegration.Scheme && e.Name == "URL Protocol"));

        Check("the menu speaks the user's language",
            entries.Any(e => e.Name == "MUIVerb" && e.Value == "Shellvis fragen")
            && ShellIntegration.Entries(exe, german: false).Any(e => e.Name == "MUIVerb" && e.Value == "Ask Shellvis"));

        Check("filing passes --sort and the file",
            entries.Any(e => e.Key.EndsWith($@"{ShellIntegration.SortVerb}\command", StringComparison.Ordinal)
                && e.Value.Contains("--sort --file \"%1\"", StringComparison.Ordinal)));

        // ------------------------------------------------------------------ against a key

        string root = @"Software\ShellvisProbe-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            string? first = ShellIntegration.EnsureRegistered(exe, german: false, root);
            Check("an empty registry gets the entries", first is not null, first ?? "nothing written");
            Check("and the scheme points at this executable", ShellIntegration.RegisteredExecutable(root) == exe);

            string? second = ShellIntegration.EnsureRegistered(exe, german: false, root);
            Check("a working registration is left alone", second is null, second ?? string.Empty);

            string other = Path.Combine(Path.GetDirectoryName(exe)!, "other.exe");
            File.WriteAllText(other, "another copy");
            string? rival = ShellIntegration.EnsureRegistered(other, german: false, root);
            Check("another copy does not take over a working registration",
                rival is null && ShellIntegration.RegisteredExecutable(root) == exe);

            File.Delete(exe);
            string? healed = ShellIntegration.EnsureRegistered(other, german: false, root);
            Check("a registration pointing at a deleted file is repaired",
                healed is not null && ShellIntegration.RegisteredExecutable(root) == other, healed ?? "nothing written");

            using (RegistryKey? ask = Registry.CurrentUser.OpenSubKey($@"{root}\*\shell\{ShellIntegration.AskVerb}\command"))
                Check("the ask entry landed under *\\shell", ask?.GetValue(string.Empty) is string { Length: > 0 });

            ShellIntegration.Remove(root);
            Check("removing takes everything away", ShellIntegration.RegisteredExecutable(root) is null);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);

            try
            {
                Directory.Delete(Path.GetDirectoryName(exe)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        // ------------------------------------------------------------------ files from Explorer

        string scratch = Path.Combine(Path.GetTempPath(), "shellvis-probe-files-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);

        try
        {
            string log = Path.Combine(scratch, "backup.log");
            File.WriteAllText(log, "2026-10-17 03:00 backup of srv-file01 failed: disk full\n");
            DocumentContext text = DocumentContextReader.Read(log);
            Check("a log file is read as text", text.Text.Contains("disk full", StringComparison.Ordinal));

            string pdf = Path.Combine(scratch, "Angebot_4711.pdf");
            File.WriteAllBytes(pdf, [0x25, 0x50, 0x44, 0x46, 0x2D]);
            DocumentContext described = DocumentContextReader.ReadOrDescribe(pdf);
            Check("an unreadable file is described instead of refused",
                described.Text.Contains("Angebot_4711.pdf", StringComparison.Ordinal)
                && described.Text.Contains("cannot be read", StringComparison.Ordinal),
                described.Text.Split('\n')[0]);

            bool refused = false;
            try
            {
                DocumentContextReader.Read(pdf);
            }
            catch (NotSupportedException)
            {
                refused = true;
            }

            Check("the picker's strict read still refuses it", refused);

            string big = Path.Combine(scratch, "big.txt");
            File.WriteAllText(big, new string('x', DocumentContextReader.MaxChars * 3));
            DocumentContext clipped = DocumentContextReader.Read(big);
            Check("a long text is clipped and says so",
                clipped.Truncated && clipped.Text.Length == DocumentContextReader.MaxChars);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        // ------------------------------------------------------------------ coming back

        var noon = new DateTime(2026, 10, 17, 13, 0, 0);
        DeskSnapshot Desk(int unread, DateTime? next) => new(unread, 0, 0, 0, 0, 1, next, 0, unread, noon);

        DeskSnapshot morning = Desk(10, null);
        DeskSnapshot after = Desk(14, noon.AddMinutes(45));
        var three = new DeskTally(3, 0, 0, 0);

        Check("a short absence says nothing",
            Shellvis.Core.Desk.ReturnBriefing.Compose(TimeSpan.FromMinutes(10), morning, after, three, true, noon) is null);

        string? back = Shellvis.Core.Desk.ReturnBriefing.Compose(TimeSpan.FromMinutes(75), morning, after, three, true, noon);
        Check("a long one names replies, arrivals and the next appointment",
            back is not null
            && back.Contains("3 Mails brauchen eine Antwort", StringComparison.Ordinal)
            && back.Contains("4 neu", StringComparison.Ordinal)
            && back.Contains("13:45", StringComparison.Ordinal),
            back ?? "null");

        Check("nothing to say is silence, not a greeting",
            Shellvis.Core.Desk.ReturnBriefing.Compose(TimeSpan.FromHours(2), morning, Desk(10, noon.AddHours(5)), DeskTally.Nothing, true, noon) is null);

        string? one = Shellvis.Core.Desk.ReturnBriefing.Compose(TimeSpan.FromHours(1), null, Desk(3, null), new DeskTally(1, 0, 0, 0), false, noon);
        Check("one reply is singular, and an unknown before counts no arrivals",
            one == "Welcome back: 1 mail needs a reply.", one ?? "null");

        // ------------------------------------------------------------------ attention

        var attention = new AttentionLog();
        DateTime t = DateTime.Now;

        attention.Note("OUTLOOK", "Posteingang - Outlook", t.AddHours(-9));
        attention.Note("OUTLOOK", "AW: Drucker 2. OG - Nachricht (HTML)", t.AddMinutes(-20));
        attention.Note("OUTLOOK", "AW: Drucker 2. OG - Nachricht (HTML)", t.AddMinutes(-19));
        attention.Note("explorer", "Downloads", t.AddMinutes(-12));
        attention.Note("WINWORD", "Angebot_4711.docx - Word", t.AddMinutes(-12).AddSeconds(1));
        attention.Note("msedge", "Jira IMIT-4711 - Edge", t.AddMinutes(-5));

        IReadOnlyList<GlanceSpan> spans = attention.Since(t.AddHours(-10), t);
        Check("a glance older than eight hours is dropped", spans.All(s => !s.Glance.Title.StartsWith("Posteingang", StringComparison.Ordinal)));
        Check("the same title twice in a row is one glance", spans.Count(s => s.Glance.Title.StartsWith("AW: Drucker", StringComparison.Ordinal)) == 1);

        IReadOnlyList<GlanceSpan> last15 = attention.Since(t.AddMinutes(-15), t);
        Check("the window in front when the span begins is included",
            last15.Count > 0 && last15[0].Glance.Title.StartsWith("AW: Drucker", StringComparison.Ordinal),
            last15.Count > 0 ? last15[0].Glance.Title : "empty");

        string recent = new Shellvis.Core.Tools.AttentionTools(attention).Recent(30);
        Check("the tool names the opened mail by its subject",
            recent.Contains("AW: Drucker 2. OG", StringComparison.Ordinal) && recent.Contains("8 min", StringComparison.Ordinal), recent.Split('\n').Length + " lines");
        Check("a window passed through for a second is left out",
            !recent.Contains("Downloads", StringComparison.Ordinal));

        Check("an empty log says how far back it reaches",
            new Shellvis.Core.Tools.AttentionTools(new AttentionLog()).Recent(10).Contains("eight hours", StringComparison.Ordinal));

        // ------------------------------------------------------------------ the shell's side

        string? repo = FindRepositoryRoot();

        if (repo is null)
        {
            Console.WriteLine("  ..   not running from the repository; the source checks are skipped.");
        }
        else
        {
            string errands = File.ReadAllText(Path.Combine(repo, "src", "Shellvis.Shell", "Views", "PillWindow.Errands.cs"));
            string channel = File.ReadAllText(Path.Combine(repo, "src", "Shellvis.Shell", "Agent", "PromptChannel.cs"));
            string launch = File.ReadAllText(Path.Combine(repo, "src", "Shellvis.Shell", "Agent", "ScheduledRun.cs"));
            string app = File.ReadAllText(Path.Combine(repo, "src", "Shellvis.Shell", "App.xaml.cs"));

            int follow = errands.IndexOf("private async Task FollowOutsideLinkAsync", StringComparison.Ordinal);
            string followBody = follow < 0 ? string.Empty : errands[follow..errands.IndexOf("private async Task TakeFileAsync", follow, StringComparison.Ordinal)];

            Check("an outside link reaches only the internal link actions",
                followBody.Contains("RunInternalLink", StringComparison.Ordinal)
                && !followBody.Contains("SubmitPromptAsync", StringComparison.Ordinal)
                && !followBody.Contains("RunTurnAsync", StringComparison.Ordinal)
                && !followBody.Contains("OpenOutsideAsync", StringComparison.Ordinal));

            Check("and anything not shellvis: is dropped",
                followBody.Contains("StartsWith(InternalScheme", StringComparison.Ordinal));

            Check("the pipe carries open, ask and sort",
                channel.Contains("\"open \"", StringComparison.Ordinal)
                && channel.Contains("\"ask \"", StringComparison.Ordinal)
                && channel.Contains("\"sort \"", StringComparison.Ordinal));

            Check("the sender hands over the right to come forward",
                channel.Contains("AllowSetForegroundWindow", StringComparison.Ordinal));

            Check("the command line uses the registered switch names",
                launch.Contains("ShellIntegration.OpenArgument", StringComparison.Ordinal)
                && launch.Contains("ShellIntegration.FileArgument", StringComparison.Ordinal));

            Check("a click with nothing running starts the bar, not a headless run",
                app.Contains("errand.FromAClick", StringComparison.Ordinal)
                && app.Contains("TakeErrand", StringComparison.Ordinal));
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "All integration checks passed." : $"{failures} integration check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static string? FindRepositoryRoot()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);

        while (here is not null && !File.Exists(Path.Combine(here.FullName, "Shellvis.slnx")))
            here = here.Parent;

        return here?.FullName;
    }
}

/// <summary>
/// What Ctrl+Alt+Q would take from the program in front, after a pause to switch to it.
/// Live and not gated: it needs a person's desktop with something selected on it.
/// </summary>
internal static class SelectionPeek
{
    public static int Run(int seconds)
    {
        Console.WriteLine($"selection: switch to a program and select something; reading in {seconds}s");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));

        nint foreground = WindowInspector.Foreground()?.Handle ?? 0;
        SelectionCapture? capture = SelectionReader.Read(foreground);

        if (capture is null)
        {
            Console.WriteLine("  nothing: the program in front offers no text pattern.");
            return 1;
        }

        Console.WriteLine($"  from {capture.Window.ProcessName}: {capture.Window.Title}");
        Console.WriteLine($"  {(capture.Selected ? "selection" : "whole document")}, {capture.Text.Length} characters{(capture.Truncated ? ", clipped" : "")}");
        Console.WriteLine("  " + capture.Text[..Math.Min(300, capture.Text.Length)].ReplaceLineEndings(" / "));
        return 0;
    }
}
