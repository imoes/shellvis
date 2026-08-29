using Shellvis.Core.Notes;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// The note database, without a model.
///
/// Three groups of check, and the third is the one that matters most.
///
/// <b>It works:</b> notes go in, come back by word and by person, fall due when they should.
///
/// <b>It survives human input:</b> the same seven queries that broke the session search
/// before it was sanitised. FTS5 reads an apostrophe as an operator, so "printer's" arrives
/// as a syntax error rather than as a search, and a search that fails on ordinary
/// punctuation is worse than no search.
///
/// <b>It stays out of the prompt:</b> this store holds what someone's wife likes and how a
/// colleague has been performing. That must be produced when it is relevant and never
/// carried in the header of every request, including requests about something else entirely.
/// The plan states it as a requirement, so it is asserted rather than assumed.
/// </summary>
internal static class NoteProbe
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

        // A database of its own, so a regression run never touches the user's notes.
        string file = Path.Combine(Path.GetTempPath(), $"shellvis-notes-{Guid.NewGuid():N}.db");

        try
        {
            using var store = new NoteStore(file);
            var tools = new NoteTools(store);

            Console.WriteLine("notes: writing things down\n");

            Check("a new database starts empty", store.Count() == 0);

            long roses = store.Add(
                "his wife likes roses; the anniversary is 12 May",
                person: "Weber, Dr. Klaus",
                topic: "personal",
                due: DateTime.Today.AddDays(3),
                sourceKind: "mail",
                sourceId: "000000012A");

            store.Add(
                "wants the Q3 figures before the board meeting",
                person: "Weber, Dr. Klaus",
                topic: "deadline",
                due: DateTime.Today.AddDays(1),
                sourceKind: "appointment",
                sourceId: "0000000ABC");

            store.Add(
                "handled the escalation with the Meier account well",
                person: "Schulz, Anna",
                topic: "review");

            store.Add("the printer's driver needs the 64-bit package", topic: "machine");

            Check("four notes are held", store.Count() == 4, store.Count().ToString());

            Console.WriteLine("\nfinding them again:");

            Check("a word finds its note",
                store.Search("anniversary").Any(n => n.Id == roses));
            Check("a word from the person column finds it too",
                store.Search("Weber").Count == 2, store.Search("Weber").Count.ToString());

            // The lookup that runs mechanically when a mail is read: partial in both
            // directions, because the caller may know more or less than the note does.
            Check("a surname finds a note filed under the full name",
                store.About("Weber").Count == 2);
            Check("and a full sender line finds a note filed under the surname",
                store.About("Weber, Dr. Klaus <k.weber@example.com>").Count == 2);
            Check("someone with no notes returns nothing rather than everything",
                store.About("Nobody At All").Count == 0);
            Check("an empty person is not a wildcard", store.About("  ").Count == 0);

            Console.WriteLine("\nwhen they fall due:");

            IReadOnlyList<Note> soon = store.Due(DateTime.Today.AddDays(1));

            Check("due through tomorrow finds the deadline", soon.Count == 1, soon.Count.ToString());
            Check("and not the one three days out",
                soon.All(n => !n.Body.Contains("roses", StringComparison.Ordinal)));

            // The off-by-one this project already shipped once, in the calendar: a range
            // that ends at midnight excludes everything on its own last day.
            long today = store.Add("call the office", due: DateTime.Today.AddHours(16));

            Check("something due later today is included in 'through today'",
                store.Due(DateTime.Today).Any(n => n.Id == today));

            Check("a note with no due date never comes up as due",
                store.Due(DateTime.Today.AddYears(10))
                    .All(n => !n.Body.Contains("printer", StringComparison.Ordinal)));

            Console.WriteLine("\nclosing:");

            Check("closing a note reports success", store.Close(roses));
            Check("closing it again reports that there was nothing open", !store.Close(roses));
            Check("a closed note stops surfacing", !store.About("Weber").Any(n => n.Id == roses));
            Check("but it is kept, so it can still be explained",
                store.Search("anniversary", includeClosed: true).Any(n => n.Id == roses));
            Check("closing an id that never existed is false, not an exception",
                !store.Close(999_999));

            Console.WriteLine("\nhuman input:");

            // The seven that broke the session search. Each must return a result set
            // rather than throw, because a search box that fails on an apostrophe is
            // worse than none.
            foreach (string query in new[]
            {
                "printer's", "print*", "\"unclosed", "spooler AND stuck",
                "NEAR(printer)", "meierstrasse:", "()",
            })
            {
                bool survived;

                try
                {
                    store.Search(query);
                    survived = true;
                }
                catch (Exception ex)
                {
                    survived = false;
                    Console.WriteLine($"       {query} threw: {ex.Message}");
                }

                Check($"'{query}' searches instead of throwing", survived);
            }

            Check("punctuation alone matches nothing rather than everything",
                store.Search("!!!").Count == 0);

            Console.WriteLine("\nthrough the tools:");

            Check("note_add refuses an empty note",
                tools.AddNote("   ").StartsWith("error:", StringComparison.Ordinal));

            Check("note_add refuses a transcript",
                tools.AddNote(new string('x', 900)).StartsWith("error:", StringComparison.Ordinal));

            Check("note_add refuses an ambiguous date rather than guessing",
                tools.AddNote("x", dueDate: "01.09.2026").StartsWith("error:", StringComparison.Ordinal));

            string added = tools.AddNote(
                "prefers to be called before eleven",
                person: "Meier, Anna",
                dueDate: DateTime.Today.AddDays(2).ToString("yyyy-MM-dd"));

            Check("note_add accepts a good one", added.StartsWith("noted", StringComparison.Ordinal), added);

            string aboutMeier = tools.SearchNotes(person: "Meier, Anna");
            Check("note_search finds it by person",
                aboutMeier.Contains("before eleven", StringComparison.Ordinal));
            Check("and prints an id to act on",
                aboutMeier.Contains("id ", StringComparison.Ordinal));

            string nothing = tools.SearchNotes(person: "Nobody");
            Check("an empty search says so",
                nothing.Contains("nothing noted", StringComparison.Ordinal), nothing);
            Check("and tells the model not to invent one",
                nothing.Contains("do not invent", StringComparison.Ordinal));

            string due = tools.DueNotes();
            Check("note_due lists what is coming",
                due.Contains("before eleven", StringComparison.Ordinal), Summarise(due));

            Check("note_due refuses an ambiguous date",
                tools.DueNotes("01.09.2026").StartsWith("error:", StringComparison.Ordinal));

            Check("the source is shown, so 'how do you know' is answerable",
                tools.SearchNotes(person: "Weber, Dr. Klaus")
                    .Contains("[from appointment", StringComparison.Ordinal));

            Console.WriteLine("\nstuck to the desktop:");

            // The half of the Vista feature that is state rather than pixels: a note comes
            // back where it was put, in the colour it was, after a restart. A note that has
            // to be saved is a document, so every change writes immediately.
            Check("nothing is stuck to begin with", store.Stickies().Count == 0);

            Sticky yellow = store.Stick("Rosen kaufen!");

            Check("a note goes up", store.Stickies().Count == 1);
            Check("yellow by default", yellow.Colour == StickyColour.Yellow);
            Check("with a usable size, not zero",
                yellow.Width > 100 && yellow.Height > 100,
                $"{yellow.Width}x{yellow.Height}");

            Sticky blue = store.Stick("Weber anrufen", StickyColour.Blue, x: -1400, y: 260);

            Check("a colour is kept", blue.Colour == StickyColour.Blue);

            // Negative coordinates are a monitor to the left of the main one, which is where
            // this machine has one. Clamping them to zero would move every note on that
            // screen onto the middle one after a restart.
            Check("a position on a monitor left of the main one survives",
                blue.X == -1400 && blue.Y == 260, $"{blue.X},{blue.Y}");

            Check("moving it is saved without being asked",
                store.Update(blue.Id, x: -1200, y: 300));

            Check("and the new position is what comes back",
                store.Sticky(blue.Id)?.X == -1200);

            Check("editing the text keeps the colour",
                store.Update(blue.Id, text: "Weber um 14:00 anrufen")
                && store.Sticky(blue.Id)?.Colour == StickyColour.Blue);

            Check("and moving it keeps the text",
                store.Update(blue.Id, y: 320)
                && store.Sticky(blue.Id)?.Text == "Weber um 14:00 anrufen");

            Check("resizing is saved too",
                store.Update(blue.Id, width: 320, height: 260)
                && store.Sticky(blue.Id)?.Width == 320);

            Check("updating an id that does not exist is false, not an exception",
                !store.Update(999_999, text: "nowhere"));

            // A sticky written from a note keeps the link, so "where did this come from"
            // stays answerable.
            long source = store.Add("er mag Rosen", person: "Weber");
            Sticky linked = store.Stick("Rosen!", StickyColour.Pink, noteId: source);

            Check("a sticky can point back at the note it came from",
                store.Sticky(linked.Id)?.NoteId == source);

            Check("throwing one away removes it", store.Unstick(yellow.Id));
            Check("and it is gone from the desktop",
                store.Stickies().All(x => x.Id != yellow.Id));
            Check("unsticking twice is false, not an exception", !store.Unstick(yellow.Id));

            Check("the note it came from is untouched",
                store.Search("Rosen").Any(x => x.Id == source));

            // A colour that no longer parses must cost the colour, not the note.
            Check("an unknown colour name falls back to yellow",
                NoteStore.ParseColour("chartreuse") == StickyColour.Yellow);
            Check("and so does none at all",
                NoteStore.ParseColour(null) == StickyColour.Yellow);
            Check("but a real one is read whatever its case",
                NoteStore.ParseColour("PURPLE") == StickyColour.Purple);

            Console.WriteLine("\nand they survive a restart:");

            IReadOnlyList<Sticky> before = store.Stickies();

            using (var reopened = new NoteStore(file))
            {
                IReadOnlyList<Sticky> after = reopened.Stickies();

                Check("the same notes are there", after.Count == before.Count,
                    $"{before.Count} then {after.Count}");

                Check("with their positions, sizes and colours",
                    after.Zip(before).All(pair =>
                        pair.First.X == pair.Second.X
                        && pair.First.Width == pair.Second.Width
                        && pair.First.Colour == pair.Second.Colour));
            }

            Console.WriteLine("\nthrough the tools:");

            Check("note_stick refuses an empty note",
                tools.StickNote("  ").StartsWith("error:", StringComparison.Ordinal));

            Check("note_stick refuses a document",
                tools.StickNote(new string('x', 400)).StartsWith("error:", StringComparison.Ordinal));

            string stuck = tools.StickNote("Milch holen", "green");

            Check("note_stick accepts a good one",
                stuck.StartsWith("stuck a green note", StringComparison.Ordinal), stuck);

            Check("note_stickies lists what is up",
                tools.ListStickies().Contains("Milch holen", StringComparison.Ordinal));

            Console.WriteLine("\nin the catalog:");

            var registry = new ToolRegistry();
            registry.RegisterFrom(tools);

            foreach ((string name, SideEffect effect) in new[]
            {
                ("note_add", SideEffect.ReadOnly),
                ("note_search", SideEffect.ReadOnly),
                ("note_due", SideEffect.ReadOnly),
                ("note_close", SideEffect.Mutating),

                // Mutating, and the reason is not the database: it puts a window on the
                // user own desktop, on top of whatever they are looking at.
                ("note_stick", SideEffect.Mutating),
                ("note_stickies", SideEffect.ReadOnly),
            })
            {
                ToolEntry? entry = registry.Tools.FirstOrDefault(t => t.Name == name);

                Check($"{name} is registered", entry is not null);
                Check($"{name} is {effect}", entry?.SideEffect == effect);
            }

            Console.WriteLine("\nout of the prompt:");

            // The requirement the plan states, asserted rather than trusted. What this
            // store holds is the private material an assistant accumulates about people;
            // carrying it in the header of every request would send it with questions that
            // have nothing to do with anyone.
            // The one place text is injected into every request is the memory store. A
            // note written here must not appear there, and the two are separate files in
            // separate stores precisely so that cannot happen by accident.
            string prompt = new Shellvis.Core.Memory.MemoryStore().PromptSection();

            Check("the memory prompt section carries no note text",
                !prompt.Contains("roses", StringComparison.OrdinalIgnoreCase)
                && !prompt.Contains("escalation", StringComparison.OrdinalIgnoreCase)
                && !prompt.Contains("before eleven", StringComparison.OrdinalIgnoreCase),
                prompt.Length + " chars of memory");

            // Structural, not incidental: there is no member on the store or the tools that
            // produces prompt text, so there is nothing for a future caller to reach for.
            Check("the note store exposes no prompt section at all",
                typeof(NoteStore).GetMembers().All(m =>
                    !m.Name.Contains("Prompt", StringComparison.Ordinal)));

            Check("and neither do the note tools",
                typeof(NoteTools).GetMembers().All(m =>
                    !m.Name.Contains("Prompt", StringComparison.Ordinal)));

            Console.WriteLine(failures == 0
                ? "\nVERIFIED: notes go in and come back by word, by person and by date; human\n"
                    + "punctuation searches instead of throwing; a closed note is kept but stops\n"
                    + "surfacing; and nothing here can reach the system prompt."
                : $"\n{failures} check(s) failed.");

            return failures == 0 ? 0 : 1;
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
                    // A file still held open is not worth failing a green run over.
                }
            }
        }
    }

    private static string Summarise(string text)
    {
        string first = text.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return first.Length <= 90 ? first : first[..90] + "...";
    }
}
