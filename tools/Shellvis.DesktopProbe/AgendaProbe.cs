using Shellvis.Core.Assist;
using Shellvis.Core.Cron;
using Shellvis.Core.Notes;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Reminders, and the promise that the same thing is not said twice.
///
/// <b>The debounce is the whole feature.</b> A job every five minutes sees the same 11:00
/// meeting on every tick for the hour before it, and each run is a fresh session with no
/// memory of the last one: there is nothing for the model to remember with even in
/// principle. So the suppression is in the tool, and this is where that is proved.
///
/// The failure being guarded against is the quiet one. A reminder said twelve times is
/// obvious and annoying; a reminder silently suppressed costs someone a meeting. The checks
/// below pin both directions.
/// </summary>
internal static class AgendaProbe
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;

        void Check(string what, bool passed, string detail = "")
        {
            if (!passed)
                failures++;

            Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        // A home of its own: this writes reminded.json and jobs.json, and neither belongs
        // in the user's.
        string home = Path.Combine(Path.GetTempPath(), $"shellvis-agenda-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);

        string? previous = Environment.GetEnvironmentVariable("SHELLVIS_HOME");
        Environment.SetEnvironmentVariable("SHELLVIS_HOME", home);

        try
        {
            Console.WriteLine("agenda: saying it once\n");

            var log = new ReminderLog(Path.Combine(home, "reminded.json"));

            string[] first = [.. log.Fresh(["a", "b", "c"], x => x)];

            Check("everything is fresh the first time", first.Length == 3);

            string[] second = [.. log.Fresh(["a", "b", "c"], x => x)];

            Check("and nothing is the second time", second.Length == 0, string.Join(",", second));

            string[] mixed = [.. log.Fresh(["b", "d"], x => x)];

            Check("a new item among old ones still gets through",
                mixed.Length == 1 && mixed[0] == "d", string.Join(",", mixed));

            Check("asking does not record", !log.AlreadySaid("never-seen"));
            Check("and what was said is remembered", log.AlreadySaid("a"));

            // An empty key is not an identity. Suppressing everything that happens to have
            // no id would be the quiet failure this guards against.
            Check("an item with no key is skipped rather than suppressing others",
                log.Fresh(["", "e"], x => x).Count == 1);

            log.Clear();

            Check("clearing means it can be said again", log.Fresh(["a"], x => x).Count == 1);

            // The log survives the process: a scheduled run is a new session every time, so
            // a purely in-memory record would suppress nothing at all.
            var reopened = new ReminderLog(Path.Combine(home, "reminded.json"));

            Check("a fresh instance still knows what was said",
                reopened.Fresh(["a"], x => x).Count == 0);

            Console.WriteLine("\nthrough the tool:");

            using var notes = new NoteStore(Path.Combine(home, "notes.db"));

            notes.Add("buy the roses", person: "Weber", due: DateTime.Today);
            notes.Add("nothing due", person: "Schulz");

            // No Outlook client: the harness must not depend on a mailbox, and a machine
            // without Outlook is a real configuration.
            var tools = new AgendaTools(outlook: null, notes: notes, reminders: reopened);

            string due = await tools.Due().ConfigureAwait(false);

            Check("a note falling due today is reported",
                due.Contains("buy the roses", StringComparison.Ordinal), Summarise(due));
            Check("and one with no date is not",
                !due.Contains("nothing due", StringComparison.Ordinal));

            string again = await tools.Due().ConfigureAwait(false);

            Check("calling again reports nothing",
                again.Contains("nothing new to report", StringComparison.Ordinal), Summarise(again));

            // The instruction has to be unmistakable. A scheduled job that speaks every five
            // minutes to confirm it is still running is a job the user switches off, and
            // then the reminders that mattered go with it.
            Check("and says so in a way that means SAY NOTHING",
                again.Contains("Say nothing", StringComparison.Ordinal), Summarise(again));

            Console.WriteLine("\nthe daily summary repeats on purpose:");

            string today = await tools.Today().ConfigureAwait(false);

            Check("the summary includes what the reminder already said",
                today.Contains("buy the roses", StringComparison.Ordinal), Summarise(today));

            string twice = await tools.Today().ConfigureAwait(false);

            Check("and says it again when asked again",
                twice.Contains("buy the roses", StringComparison.Ordinal));

            Check("without Outlook it says so rather than reporting an empty calendar",
                today.Contains("Outlook is not available", StringComparison.Ordinal), Summarise(today));

            Check("an unreadable date is refused rather than guessed",
                (await tools.Today("01.09.2026").ConfigureAwait(false))
                    .StartsWith("error:", StringComparison.Ordinal));

            Console.WriteLine("\nin the catalog:");

            var registry = new ToolRegistry();
            registry.RegisterFrom(tools);

            foreach (string name in new[] { "agenda_due", "agenda_today" })
            {
                ToolEntry? entry = registry.Tools.FirstOrDefault(t => t.Name == name);

                Check($"{name} is registered", entry is not null);

                // Read-only is what makes it usable unattended: a scheduled run denies every
                // approval, so anything mutating would silently never fire.
                Check($"{name} is read-only, so a scheduled run can use it",
                    entry?.SideEffect == SideEffect.ReadOnly);
            }

            Console.WriteLine("\nthe starter jobs:");

            string jobsFile = Path.Combine(home, "jobs.json");
            var store = new CronStore(jobsFile);

            IReadOnlyList<CronJob> loaded = store.Load();

            Check("a missing file still loads as no jobs", loaded.Count == 0);
            Check("but the starter file is written", File.Exists(jobsFile));

            IReadOnlyList<CronJob> written = new CronStore(jobsFile).Load();

            Check("and it holds the two secretarial jobs", written.Count == 2, written.Count.ToString());
            Check("both switched OFF, because a timer on the mailbox is opted into",
                written.All(j => !j.Enabled));
            Check("with schedules that parse",
                written.All(j => j.Parsed is not null),
                string.Join(", ", written.Select(j => j.Schedule)));
            Check("and neither is due while disabled",
                written.All(j => j.NextDue(DateTimeOffset.Now) is null));

            // Enabling one must be all it takes. A template that needs editing before it
            // works is a template that gets deleted.
            var enabled = written[0] with { Enabled = true };

            Check("enabling one makes it due",
                enabled.NextDue(DateTimeOffset.Now) is not null);

            Console.WriteLine(failures == 0
                ? "\nVERIFIED: a reminder is said once and then suppressed across processes, the\n"
                    + "daily summary repeats on purpose, both tools are read-only so a scheduled\n"
                    + "run can actually use them, and the starter jobs arrive switched off."
                : $"\n{failures} check(s) failed.");

            return failures == 0 ? 0 : 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHELLVIS_HOME", previous);

            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (IOException)
            {
                // A file still held open is not worth failing a green run over.
            }
        }
    }

    private static string Summarise(string text)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 90 ? flat : flat[..90] + "...";
    }
}
