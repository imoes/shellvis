using Shellvis.Core.Desk;

namespace Shellvis.DesktopProbe;

/// <summary>
/// The remembered desk: identity, enrichment, links, and forgetting.
///
/// <b>Why this one is worth more than most.</b> Every failure in a cache is quiet. A key that
/// is not stable does not throw -- it writes a second row, and then the assistant "forgets"
/// something it was told yesterday while the store reports a healthy row count. An indexing
/// pass that overwrites the enrichment column erases months of understanding and looks
/// exactly like a successful sweep. Retention that deletes the wrong side of a comparison
/// takes the recent things and leaves the stale ones.
///
/// None of that needs Outlook, a mailbox or a network, so all of it is checked here and none
/// of it is left to being noticed.
/// </summary>
internal static class DeskProbe
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

        Console.WriteLine("desk: what is remembered about a mail, a ticket and a task\n");

        string file = Path.Combine(Path.GetTempPath(), $"shellvis-desk-probe-{Guid.NewGuid():N}.db");

        try
        {
            using var store = new DeskStore(file);

            var now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Unspecified);

            // ------------------------------------------------------------- identity
            Console.WriteLine("-- the key is the same key every time --");

            Check("angle brackets and case do not make a second mail",
                DeskObject.MakeId(DeskKind.Mail, "<AbC@Example.COM>")
                    == DeskObject.MakeId(DeskKind.Mail, "abc@example.com"),
                "a message id is case-insensitive and Exchange does not return one case twice");

            Check("a ticket key typed in lower case is the same ticket",
                DeskObject.MakeId(DeskKind.Ticket, "imit-1234")
                    == DeskObject.MakeId(DeskKind.Ticket, "IMIT-1234"));

            Check("two kinds sharing a key do not collide",
                DeskObject.MakeId(DeskKind.Task, "X1") != DeskObject.MakeId(DeskKind.Mail, "X1"));

            Check("an id says what kind it is without a lookup",
                DeskObject.KindOf(DeskObject.MakeId(DeskKind.Ticket, "IMIT-1")) == DeskKind.Ticket
                    && DeskObject.KindOf("nonsense") is null);

            // ------------------------------------------------------------ sightings
            Console.WriteLine("\n-- seeing a thing twice --");

            string mailId = DeskObject.MakeId(DeskKind.Mail, "msg-1@example.com");

            Check("the first sighting says it is the first",
                store.See(Mail(mailId, "Drucker geht nicht", now)));

            Check("the second says it is not",
                !store.See(Mail(mailId, "Drucker geht nicht", now)));

            Check("and the source's own fields are refreshed",
                store.See(Mail(mailId, "Drucker geht nicht (erledigt)", now)) == false
                    && store.Get(mailId)?.Subject == "Drucker geht nicht (erledigt)");

            Check("one row, not two", store.Count() == 1, $"count: {store.Count()}");

            // ----------------------------------------------------------- enrichment
            Console.WriteLine("\n-- what the assistant works out stays worked out --");

            store.Enrich(mailId, "Weber wartet auf das FTP-Passwort.", now);

            Check("an enrichment is stored",
                store.Get(mailId)?.Enrichment?.Contains("FTP", StringComparison.Ordinal) == true);

            store.See(Mail(mailId, "Drucker geht nicht", now.AddHours(1)));

            Check("AN INDEXING PASS DOES NOT ERASE IT",
                store.Get(mailId)?.Enrichment?.Contains("FTP", StringComparison.Ordinal) == true,
                "this is the check the whole design exists for");

            store.Enrich(mailId, "Passwort ist raus, wartet auf Bestätigung.", now.AddHours(2));

            string? both = store.Get(mailId)?.Enrichment;

            Check("a second enrichment is appended, not substituted",
                both?.Contains("FTP", StringComparison.Ordinal) == true
                    && both.Contains("Bestätigung", StringComparison.Ordinal),
                "understanding accumulates; replacing loses the half that explains the rest");

            Check("and each line carries its date",
                both?.Contains("2026-09-04 12:00", StringComparison.Ordinal) == true
                    && both.Contains("2026-09-04 14:00", StringComparison.Ordinal));

            Check("an empty enrichment is refused rather than stored as a blank line",
                Unchanged(store, mailId, () => store.Enrich(mailId, "   ", now)));

            // ---------------------------------------------------------------- links
            Console.WriteLine("\n-- a mail, the ticket it is about, and the task it caused --");

            string ticketId = DeskObject.MakeId(DeskKind.Ticket, "IMIT-1234");
            string taskId = DeskObject.MakeId(DeskKind.Task, "task-1");

            store.See(Ticket(ticketId, "Drucker im 2. OG", now));
            store.See(Task(taskId, "Toner bestellen", now, now.AddDays(2)));

            store.Link(mailId, ticketId, "about");
            store.Link(mailId, taskId, "caused");

            Check("the mail knows both", store.Related(mailId).Count == 2);

            Check("AND THE TICKET KNOWS THE MAIL",
                store.Related(ticketId).Any(o => o.Id == mailId),
                "the link is written from the mail; the useful question is asked of the ticket");

            Check("a link written twice is one link",
                Same(() => { store.Link(mailId, ticketId, "about"); return store.Related(mailId).Count; }, 2));

            // --------------------------------------------------------------- ticket
            Console.WriteLine("\n-- everything about one ticket --");

            store.See(Mail(
                DeskObject.MakeId(DeskKind.Mail, "msg-2@example.com"),
                "[IMIT-1234] kommentiert",
                now.AddHours(3),
                ticketKey: "IMIT-1234"));

            IReadOnlyList<DeskObject> about = store.AboutTicket("IMIT-1234");

            Check("the ticket and the mail that mentioned it come back together",
                about.Any(o => o.Kind == DeskKind.Ticket) && about.Any(o => o.Kind == DeskKind.Mail),
                $"{about.Count} thing(s)");

            Check("asked in lower case it is the same ticket",
                store.AboutTicket("imit-1234").Count == about.Count);

            Check("newest first", about.Count < 2 || about[0].When >= about[^1].When);

            // --------------------------------------------------------------- search
            Console.WriteLine("\n-- finding it again --");

            Check("by subject", store.Search("Drucker").Count > 0);
            Check("by sender", store.Search("Weber").Count > 0);

            Check("and by what the assistant wrote about it",
                store.Search("Bestätigung").Any(o => o.Id == mailId),
                "the enrichment is indexed, or it can only be found by knowing the id");

            Check("a partial word finds the whole one",
                store.Search("Druck").Count > 0, "a prefix star on the last token");

            Check("a query full of operators does not throw",
                DoesNotThrow(() => store.Search("NEAR( \"unbalanced AND * OR )")));

            Check("a one-letter query finds nothing rather than everything",
                store.Search("a").Count == 0);

            Check("an empty query finds nothing", store.Search("   ").Count == 0);

            Check("a window excludes what is older than it",
                store.Search("Drucker", since: now.AddDays(1)).Count == 0,
                "this is the window the slider sets");

            // ------------------------------------------------------------ forgetting
            Console.WriteLine("\n-- three months, then gone --");

            string oldId = DeskObject.MakeId(DeskKind.Mail, "ancient@example.com");
            store.See(Mail(oldId, "Rechnung 2025", now.AddDays(-200)));
            store.Link(oldId, ticketId, "about");

            int held = store.Count();
            int gone = store.Prune(now);

            Check("the old one is forgotten", store.Get(oldId) is null, $"pruned {gone}");
            Check("the recent ones are not", store.Get(mailId) is not null && store.Get(ticketId) is not null);
            Check("the count fell by exactly what was pruned", store.Count() == held - gone);

            Check("and its links went with it",
                !store.Related(ticketId).Any(o => o.Id == oldId),
                "a link to a thing that is gone answers a question with a ghost");

            Check("the retention is three months",
                Math.Abs(DeskStore.DefaultRetention.TotalDays - 92) < 1,
                $"{DeskStore.DefaultRetention.TotalDays} days");

            Check("and a configured retention is the one that is used",
                Retained(TimeSpan.FromDays(10)) == 10,
                "a keepDays in the config that Prune ignored would be a setting that does nothing");

            Check("a nonsense retention falls back to the default rather than deleting everything",
                Retained(TimeSpan.Zero) == 92);

            Check("nothing is pruned that is inside the window",
                store.Prune(now) == 0, "a second sweep has nothing left to do");

            // ------------------------------------------------------------- ordering
            Console.WriteLine("\n-- dates that sort as dates --");

            store.See(Mail(DeskObject.MakeId(DeskKind.Mail, "jan@example.com"), "Januar", new DateTime(2026, 9, 1, 9, 0, 0)));
            store.See(Mail(DeskObject.MakeId(DeskKind.Mail, "feb@example.com"), "Februar", new DateTime(2026, 9, 2, 9, 0, 0)));

            IReadOnlyList<DeskObject> recent = store.Recent(new DateTime(2026, 8, 1, 0, 0, 0));

            Check("recent comes back newest first",
                recent.Count > 1 && recent[0].When >= recent[1].When,
                "the stored text is round-trip format, so SQL's string compare IS a date compare");

            Check("and the oldest thing held is reported",
                store.Oldest() is not null && store.Oldest() < now);

            Console.WriteLine(failures == 0
                ? "\nVERIFIED: one key per thing however it is spelled, an indexing pass that\n"
                  + "cannot erase what was understood, links that answer from both ends, a\n"
                  + "search that survives what somebody types, and a quarter of memory that\n"
                  + "forgets on time and takes its dangling links with it.\n"
                  + "\nNOT covered here: filling it from Outlook, which needs a mailbox."
                : $"\n{failures} check(s) failed.");

            return failures;
        }
        finally
        {
            foreach (string leftover in new[] { file, file + "-wal", file + "-shm" })
            {
                try
                {
                    if (File.Exists(leftover))
                        File.Delete(leftover);
                }
                catch (IOException)
                {
                    // A probe that cannot tidy up is not a probe that failed.
                }
            }
        }
    }

    private static DeskObject Mail(string id, string subject, DateTime when, string? ticketKey = null) => new(
        Id: id,
        Kind: DeskKind.Mail,
        Subject: subject,
        WhoName: "Weber, Anna",
        WhoAddress: "anna.weber@example.com",
        When: when,
        Due: null,
        State: "unread",
        TicketKey: ticketKey,
        Thread: "conv-1",
        EntryId: "0000ENTRY",
        Facts: null,
        Enrichment: null,
        FirstSeen: when,
        LastSeen: when);

    private static DeskObject Ticket(string id, string subject, DateTime when) => new(
        Id: id,
        Kind: DeskKind.Ticket,
        Subject: subject,
        WhoName: "Service Desk",
        WhoAddress: string.Empty,
        When: when,
        Due: null,
        State: "In Arbeit",
        TicketKey: "IMIT-1234",
        Thread: null,
        EntryId: null,
        Facts: null,
        Enrichment: null,
        FirstSeen: when,
        LastSeen: when);

    private static DeskObject Task(string id, string subject, DateTime when, DateTime due) => new(
        Id: id,
        Kind: DeskKind.Task,
        Subject: subject,
        WhoName: string.Empty,
        WhoAddress: string.Empty,
        When: when,
        Due: due,
        State: "open",
        TicketKey: null,
        Thread: null,
        EntryId: "0000TASK",
        Facts: null,
        Enrichment: null,
        FirstSeen: when,
        LastSeen: when);

    /// <summary>Whether an action left the enrichment as it was.</summary>
    private static bool Unchanged(DeskStore store, string id, Action act)
    {
        string? before = store.Get(id)?.Enrichment;
        act();

        return store.Get(id)?.Enrichment == before;
    }

    private static bool Same(Func<int> measure, int expected) => measure() == expected;

    /// <summary>What retention a store actually ends up with, in days.</summary>
    private static double Retained(TimeSpan asked)
    {
        string file = Path.Combine(Path.GetTempPath(), $"shellvis-desk-keep-{Guid.NewGuid():N}.db");

        try
        {
            using var store = new DeskStore(file, asked);

            return Math.Round(store.Retention.TotalDays);
        }
        finally
        {
            foreach (string leftover in new[] { file, file + "-wal", file + "-shm" })
            {
                try
                {
                    if (File.Exists(leftover))
                        File.Delete(leftover);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static bool DoesNotThrow(Action act)
    {
        try
        {
            act();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"       threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
