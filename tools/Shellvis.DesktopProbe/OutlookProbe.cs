using System.Globalization;
using Shellvis.Core.Office;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Exercises the Outlook COM path against the real, running Outlook.
///
/// Two things are being verified, and the second matters more than the first.
///
/// That the automation works at all: folders enumerate, the calendar expands recurring
/// meetings, contacts are searchable.
///
/// And that it leaves nothing behind. The characteristic failure of Office automation
/// is a surviving OUTLOOK.EXE holding the user's profile open, which then blocks the
/// real Outlook from starting. So the probe records whether Outlook was already running
/// before it started, and checks afterwards that the situation is unchanged.
///
/// Output is kept deliberately thin. This reads a real mailbox, and proving the
/// mechanism works needs counts and one metadata line, not the contents of anyone's
/// correspondence.
/// </summary>
internal static class OutlookProbe
{
    /// <summary>
    /// The date arithmetic behind calendar_list, checked without Outlook.
    ///
    /// This exists because "welche Termine liegen heute an" answered "no appointments" on a
    /// day that had a meeting at 11:00. Nothing was wrong with the COM call: asking for one
    /// day produced from == to, the range Outlook is given is half-open, and a range of zero
    /// length matches nothing. The old harness could not see it -- it asked for a week and got
    /// a week, and the empty answer for a single day looked like an empty calendar.
    ///
    /// Pure arithmetic, so it needs no live calendar and no known appointment. That is the
    /// point: a check that depends on the user having a meeting today is a check that passes
    /// or fails for the wrong reason.
    /// </summary>
    private static int RangeChecks()
    {
        Console.WriteLine("-- the date range calendar_list asks for --");
        int failures = 0;

        // The reported case: one day.
        (DateTime start, DateTime lastDay, DateTime endExclusive) =
            OutlookTools.ResolveRange("2026-08-27", "2026-08-27");

        Console.WriteLine(
            $"    today only      -> [{start:yyyy-MM-dd HH:mm}, {endExclusive:yyyy-MM-dd HH:mm})"
            + $" reported as {lastDay:yyyy-MM-dd}");

        failures += Check2(
            "a single day covers that whole day, not zero length",
            endExclusive == start.AddDays(1));

        failures += Check2(
            "and an appointment at 11:00 that day falls inside it",
            new DateTime(2026, 8, 27, 11, 0, 0) < endExclusive
            && new DateTime(2026, 8, 27, 11, 30, 0) > start);

        failures += Check2(
            "while the answer still names the day that was asked for",
            lastDay.Date == new DateTime(2026, 8, 27));

        // The same defect, one step less obvious: the last day of any range was dropped.
        (start, lastDay, endExclusive) = OutlookTools.ResolveRange("2026-08-24", "2026-08-30");

        failures += Check2(
            "the last day of a range is included too",
            endExclusive == new DateTime(2026, 8, 31));

        // A time, when given, is an instant and must not be widened -- otherwise "from 14:00
        // to 16:00" would silently mean the rest of the day.
        (start, lastDay, endExclusive) = OutlookTools.ResolveRange("2026-08-27 14:00", "2026-08-27 16:00");

        failures += Check2(
            "an explicit time is honoured rather than rounded to a day",
            endExclusive == new DateTime(2026, 8, 27, 16, 0, 0));

        // Reversed input is a plausible model mistake and must not produce an empty range.
        (start, lastDay, endExclusive) = OutlookTools.ResolveRange("2026-08-30", "2026-08-24");

        failures += Check2(
            "reversed dates are swapped rather than returning nothing",
            start.Date == new DateTime(2026, 8, 24) && endExclusive == new DateTime(2026, 8, 31));

        // The default, which is what an unqualified "what is coming up" hits.
        (start, lastDay, endExclusive) = OutlookTools.ResolveRange(null, null);

        failures += Check2(
            "the default spans seven whole days from today",
            start.Date == DateTime.Today && endExclusive == DateTime.Today.AddDays(7));

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// The window mail_list asks for, and the filter it turns into. Checked without Outlook.
    ///
    /// This exists because of a subtler version of the calendar defect. Asked "what were the
    /// highlights last week", the model called mail_list, got the newest twenty messages and
    /// summarised them -- and there was no window to ask for, so twenty was the only lever it
    /// had. In a busy folder that covers two days, the answer still says "last week", and
    /// nothing in the result contradicted it: the count of matching messages was read from
    /// Outlook and then discarded.
    ///
    /// Two things are pinned here. The parsing, which is the arithmetic. And the direction of
    /// the culture rule, which is the trap: reading a date the model wrote prefers a FIXED
    /// format, while writing one into an Outlook filter must use the USER's -- the opposite
    /// of each other, and a "unification" of the two would resurrect a bug that took a live
    /// Outlook to find.
    /// </summary>
    private static int WindowChecks()
    {
        Console.WriteLine("-- the window mail_list asks for --");
        int failures = 0;

        // A fixed "now", so the checks do not drift with the clock.
        var now = new DateTime(2026, 9, 2, 14, 30, 0);

        failures += Parses("7d", now, now.Date.AddDays(-7));
        failures += Parses("2w", now, now.Date.AddDays(-14));
        failures += Parses("36h", now, now.AddHours(-36));
        failures += Parses("today", now, now.Date);
        failures += Parses("yesterday", now, now.Date.AddDays(-1));
        failures += Parses("2026-08-25", now, new DateTime(2026, 8, 25));

        // The user's own short format, built from the current culture rather than written
        // out -- so this check means the same thing on a German desktop and on the runner.
        //
        // THE regression, and it was live in both mail_list and calendar_list until this
        // check found it. The parser used to try the invariant culture first; the invariant
        // parser accepts a full stop as a separator and reads the month first, so a German
        // 02.09.2026 was read as 9 February. It never fails, so the local fallback was never
        // reached -- and only the days above the twelfth escaped, because a month above
        // twelve is finally invalid. A deliberately ambiguous day is used here: one whose
        // day and month can be swapped without either becoming invalid.
        var ambiguous = new DateTime(2026, 9, 2);
        string local = ambiguous.ToString("d", CultureInfo.CurrentCulture);

        failures += Parses(local, now, ambiguous);

        failures += Check2(
            "and the calendar reads it the same way, from the same code",
            OutlookTools.ResolveRange(local, local).Start.Date == ambiguous);

        // The other half of the shape rule: a leading four-digit year is ISO whatever the
        // machine's culture is, so a model writing ISO is never reinterpreted.
        failures += Parses("2026-09-02", now, ambiguous);

        // Refusals. Each one would otherwise become a silent window: a zero offset means
        // "now", which matches nothing, and prose means whatever DateTime.TryParse decides.
        failures += Refuses("", now);
        failures += Refuses("0d", now);
        failures += Refuses("-3d", now);
        failures += Refuses("letzte Woche", now);
        failures += Refuses("soon", now);

        MailWindow.TryParse("nonsense", now, out _, out string? problem);

        failures += Check2(
            "and the refusal names the forms that do work",
            problem is not null && problem.Contains("7d", StringComparison.Ordinal));

        // ------------------------------------------------ the filter, and the culture trap
        Console.WriteLine();

        string none = OutlookClient.ListFilter(false, null, null);
        failures += Check2("no criteria means no filter at all", none.Length == 0);

        string unread = OutlookClient.ListFilter(true, null, null);
        failures += Check2("unread alone is the unread clause", unread == "[UnRead] = True");

        var since = new DateTime(2026, 8, 26, 8, 0, 0);
        string window = OutlookClient.ListFilter(false, since, null);

        Console.WriteLine($"    since {since:yyyy-MM-dd HH:mm} -> {window}");

        failures += Check2(
            "a lower bound is inclusive, so the first day of the window counts",
            window.Contains("[ReceivedTime] >= '", StringComparison.Ordinal));

        // THE regression. Outlook's bracket syntax reads the date in the user's short-date
        // format; an invariant MM/dd/yyyy was read as dd.MM.yyyy on this German machine and
        // the filter matched nothing -- invisibly, for about half the days of any month.
        failures += Check2(
            "the date is written in the CURRENT culture, not the invariant one",
            window.Contains(
                since.ToString("g", CultureInfo.CurrentCulture),
                StringComparison.Ordinal));

        // And the other direction, stated as a check so the pair cannot be collapsed: what
        // the model writes is read with a fixed format first.
        failures += Parses("2026-01-09", now, new DateTime(2026, 1, 9));

        string both = OutlookClient.ListFilter(true, since, new DateTime(2026, 9, 2));

        failures += Check2(
            "criteria combine with AND rather than replacing each other",
            both.Contains("[UnRead] = True", StringComparison.Ordinal)
            && both.Contains(">=", StringComparison.Ordinal)
            && both.Contains("<", StringComparison.Ordinal)
            && both.Contains(" AND ", StringComparison.Ordinal));

        failures += Check2(
            "the upper bound is exclusive, so a day range does not swallow the next day",
            both.Contains("[ReceivedTime] < '", StringComparison.Ordinal));

        // ------------------------------------------------------- the search filter, and DASL
        Console.WriteLine();

        failures += Check2("two words are two words", OutlookClient.Words("ftp zugang").Length == 2);
        failures += Check2("commas separate too", OutlookClient.Words("ftp, zugang").Length == 2);

        // A single letter would match most of a mailbox. A search that returns everything is
        // worse than one that returns nothing, because it looks as though it worked.
        failures += Check2("single letters are dropped", OutlookClient.Words("a ftp b").Length == 1);
        failures += Check2("and nothing searchable is no words", OutlookClient.Words("a b").Length == 0);
        failures += Check2("as is an empty query", OutlookClient.Words(null).Length == 0);

        string search = OutlookClient.SearchFilter(["ftp"], null, null);
        Console.WriteLine($"    ftp -> {search}");

        failures += Check2(
            "a content search is a DASL query",
            search.StartsWith("@SQL=", StringComparison.Ordinal));

        failures += Check2(
            "and looks in the subject AND the body, so a word in either counts",
            search.Contains("httpmail:subject", StringComparison.Ordinal)
            && search.Contains("httpmail:textdescription", StringComparison.Ordinal)
            && search.Contains(" OR ", StringComparison.Ordinal));

        failures += Check2(
            "two words are required together rather than as a phrase",
            OutlookClient.SearchFilter(["ftp", "kunde"], null, null)
                .Contains(") AND (", StringComparison.Ordinal));

        // O'Brien. Unescaped it closes the string early and the filter becomes something
        // else entirely -- at best an error, at worst a different search.
        failures += Check2(
            "an apostrophe is escaped rather than closing the string",
            OutlookClient.SearchFilter(["o'brien"], null, null)
                .Contains("o''brien", StringComparison.Ordinal));

        // THE pair. These two filters are built by two methods on the same class, and the
        // date rule is deliberately OPPOSITE in each: Outlook's bracket syntax reads a date
        // in the user's culture, a DASL comparison does not. Checked side by side so that
        // "unifying" them is a failing build rather than a silent empty result.
        string dasl = OutlookClient.SearchFilter(["ftp"], since, null);
        string bracket = OutlookClient.ListFilter(false, since, null);

        Console.WriteLine($"    DASL    -> ...{dasl[^28..]}");
        Console.WriteLine($"    bracket -> {bracket}");

        failures += Check2(
            "the DASL date is invariant",
            dasl.Contains(
                since.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                StringComparison.Ordinal));

        failures += Check2(
            "while the bracket date is in the user's culture -- the two must stay different",
            bracket.Contains(
                since.ToString("g", CultureInfo.CurrentCulture),
                StringComparison.Ordinal));

        // ------------------------------------------------------------- finding a ticket key
        //
        // Pure, and it has to be: the watcher decides whether an arriving mail is a ticket
        // notification before there is a model in the loop, so a wrong answer here is a
        // notification about the wrong ticket with nothing to catch it.
        Console.WriteLine();

        failures += Check2(
            "a key in a subject is found",
            TicketKeys.Primary("[JCUE-5915] Massive CUE-Performanceprobleme") == "JCUE-5915");

        failures += Check2(
            "the SUBJECT wins over the body, which is full of links and footers",
            TicketKeys.Primary("AW: [IMIT-1234] Drucker", "see also JCUE-9999 and IMIT-5") == "IMIT-1234");

        failures += Check2(
            "a hand-forwarded mail with nothing in the subject falls back to the body",
            TicketKeys.Primary("Fwd: kannst du da mal schauen", "es geht um IMIT-777") == "IMIT-777");

        failures += Check2(
            "the same key three times is one ticket, not three",
            TicketKeys.FindAll("IMIT-1 in the subject, IMIT-1 in the body, IMIT-1 in the footer")
                is [_]);

        // Each of these matched before the rules were tightened, and each would have produced
        // a notification about a ticket that does not exist.
        failures += Check2(
            "a hyphenated word is not a ticket",
            TicketKeys.FindAll("Teil-3 und Version-2 und e-1").Count == 0);

        failures += Check2(
            "lower case is not a ticket, because prose is lower case and keys are not",
            TicketKeys.FindAll("imit-1234").Count == 0);

        failures += Check2(
            "a single letter is not a project",
            TicketKeys.FindAll("A-1").Count == 0);

        failures += Check2(
            "and a bracket or a colon around it does not hide it",
            TicketKeys.FindAll("[IMIT-1] IMIT-2: done (IMIT-3)") is [_, _, _]);

        failures += Check2(
            "a notification is told apart from a person by its sender",
            TicketKeys.LooksAutomated("jira@example.com")
            && TicketKeys.LooksAutomated("no-reply@host", "IT Servicedesk")
            && !TicketKeys.LooksAutomated("anna.meier@example.com", "Meier, Anna"));

        // ------------------------------------------------------ splitting a recipient list
        //
        // This exists because getting it wrong broke a feature invisibly. Splitting on commas
        // as well as semicolons turned "Kluge, Thomas" -- which is how this address book
        // lists people -- into "Kluge" and "Thomas", so replying to one named person resolved
        // nobody and reported the tool as broken.
        Console.WriteLine();

        failures += Check2(
            "a name with a comma in it stays one recipient",
            OutlookClient.SplitRecipients("Kluge, Thomas") is [_]);

        failures += Check2(
            "two such names separated by a semicolon are two",
            OutlookClient.SplitRecipients("Kluge, Thomas; Meier, Anna") is [_, _]);

        failures += Check2(
            "a comma-separated list of ADDRESSES is split, because a comma cannot be part of one",
            OutlookClient.SplitRecipients("a@x.de, b@y.de") is [_, _]);

        failures += Check2(
            "and the two kinds mix",
            OutlookClient.SplitRecipients("a@x.de; Kluge, Thomas") is [_, _]);

        failures += Check2(
            "an empty list is no recipients rather than one empty one",
            OutlookClient.SplitRecipients("  ").Count == 0);

        // ------------------------------------------------------------ what the page withheld
        Console.WriteLine();

        var page = new MailPage([], 312, 3142, null, null);
        failures += Check2("a page knows how many it withheld", page.Withheld == 312);

        var full = new MailPage(
            [new MailSummary("1", "s", "f", now, false, false, string.Empty)],
            1,
            3142,
            now,
            now);

        failures += Check2("and withholds nothing when it returned everything matching",
            full.Withheld == 0);

        Console.WriteLine();
        return failures;
    }

    private static int Parses(string text, DateTime now, DateTime expected)
    {
        bool ok = MailWindow.TryParse(text, now, out DateTime actual, out _) && actual == expected;

        Console.WriteLine(
            $"  {(ok ? "ok  " : "FAIL")} '{text}' -> {expected:yyyy-MM-dd HH:mm}"
            + (ok ? string.Empty : $"  (got {actual:yyyy-MM-dd HH:mm})"));

        return ok ? 0 : 1;
    }

    private static int Refuses(string text, DateTime now)
    {
        bool refused = !MailWindow.TryParse(text, now, out _, out string? why);

        Console.WriteLine(
            $"  {(refused ? "ok  " : "FAIL")} '{text}' is refused"
            + (refused && why is not null ? string.Empty : "  (accepted)"));

        return refused ? 0 : 1;
    }

    private static int Check2(string what, bool condition)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }

    /// <summary>
    /// Everything here that needs no Outlook, as a harness of its own.
    ///
    /// <b>Because the CI cannot run 'outlook'.</b> That harness needs Office installed and a
    /// real mailbox, so it is deliberately absent from the gate -- and these checks, which
    /// need neither, were reachable only through it. Both defects they pin produced
    /// confidently wrong answers rather than errors, which is exactly the kind that survives
    /// an ungated harness.
    /// </summary>
    public static int RunPureChecks()
    {
        int failures = RangeChecks() + WindowChecks();

        Console.WriteLine(failures == 0
            ? "VERIFIED: the calendar range covers the days it names, a mail window is the\n"
                + "window that was asked for, and the two date formats stay opposite."
            : $"{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    public static async Task<int> RunAsync()
    {
        // Checked first, and without Outlook, because this is where the defect was.
        int rangeFailures = RangeChecks() + WindowChecks();

        if (!OutlookClient.IsAvailable)
        {
            Console.WriteLine("Outlook is not registered for automation on this machine.");
            return rangeFailures + 1;
        }

        bool wasRunning = IsOutlookRunning();
        Console.WriteLine($"Outlook running before the probe: {wasRunning}\n");

        int failures = rangeFailures;

        // ---------------------------------------------------- in the catalog
        //
        // A capability that exists in code and not in the registry does not exist for the
        // agent. That is not a hypothetical: DesktopActions.SendKeys was written, worked,
        // and went unregistered for several steps, during which the model correctly told
        // the user it could not send keystrokes. So the registration is asserted, not
        // assumed, and the side effect with it -- reading a task list must not prompt, and
        // writing to the user own list must.
        using (var catalog = new ComApartment("Shellvis probe catalog"))
        {
            var registry = new ToolRegistry();
            registry.RegisterFrom(new OutlookTools(catalog));

            foreach ((string name, SideEffect effect) in new[]
            {
                ("task_list", SideEffect.ReadOnly),
                ("task_create", SideEffect.Mutating),
                ("task_complete", SideEffect.Mutating),
                ("mail_open", SideEffect.Mutating),
                ("mail_thread", SideEffect.ReadOnly),
                ("mail_history", SideEffect.ReadOnly),
                ("mail_search", SideEffect.ReadOnly),
            })
            {
                var entry = registry.Tools.FirstOrDefault(t => t.Name == name);

                failures += Report($"{name} is registered", entry is not null);
                failures += Report($"{name} is {effect}", entry?.SideEffect == effect);
            }

            // The rule that shapes this whole surface, asserted rather than trusted.
            failures += Report(
                "no tool here can send anything",
                !registry.Tools.Any(t => t.Name.Contains("send", StringComparison.OrdinalIgnoreCase)));
        }

        Console.WriteLine();

        using (var apartment = new ComApartment("Shellvis probe COM"))
        {
            var tools = new OutlookTools(apartment);
            var client = new OutlookClient(apartment);

            // ------------------------------------------------------------- mail
            string inbox = await tools.ListMail("inbox", limit: 5).ConfigureAwait(false);
            failures += Check("mail_list inbox", inbox);
            Console.WriteLine("       " + Summarise(inbox));

            // The notice, when it applies. Launching the user's mail client to answer a
            // read-only question is defensible; doing it silently is not, and it WAS silent
            // until a harness run failed on a machine where Outlook happened to be closed and
            // the cause had to be explained from outside.
            if (!wasRunning)
            {
                failures += Check2(
                    "the answer says Outlook had to be started",
                    inbox.Contains("was not running", StringComparison.OrdinalIgnoreCase));
            }

            string unread = await tools.ListMail("inbox", limit: 3, unreadOnly: true).ConfigureAwait(false);

            // And only once. Repeating it on every call turns a useful notice into noise.
            if (!wasRunning)
            {
                failures += Check2(
                    "and says it only once",
                    !unread.Contains("was not running", StringComparison.OrdinalIgnoreCase));
            }
            failures += Check("mail_list unread", unread);
            Console.WriteLine("       " + Summarise(unread));

            // ------------------------------------------------- the window, against the store
            //
            // The one part of this that a pure check cannot reach. The filter string is built
            // correctly by WindowChecks; whether OUTLOOK agrees with it is a different
            // question, and the calendar defect proved that a filter can be syntactically
            // fine, silently match nothing, and read as an answer. So the window is asked for
            // against the real mailbox and the returned dates are checked to be inside it.
            string week = await tools
                .ListMail("inbox", limit: 100, since: "7d")
                .ConfigureAwait(false);

            failures += Check("mail_list since 7d", week);
            Console.WriteLine("       " + Summarise(week));

            failures += Check2(
                "the answer says which window was asked for",
                week.Contains("asked for since", StringComparison.Ordinal));

            DateTime cutoff = DateTime.Now.Date.AddDays(-7);
            DateTime[] stamps = Timestamps(week);

            failures += Check2(
                $"and every message returned is inside it ({stamps.Length} checked)",
                stamps.Length > 0 && stamps.All(d => d >= cutoff));

            // The proof that the UPPER bound went out in the format Outlook reads, and it has
            // to be arithmetic rather than a look at the result.
            //
            // A slice of 14 to 7 days ago came back empty on this mailbox. That is a perfectly
            // possible quiet week -- and it is also exactly what a misread date looks like, so
            // on its own it proves nothing. The three windows are therefore compared against
            // each other: a fortnight cannot hold fewer than a week, and whatever it holds
            // beyond the week must be precisely what the slice returns. If the upper bound
            // were being dropped or misparsed, that identity breaks.
            string fortnight = await tools
                .ListMail("inbox", limit: 100, since: "14d")
                .ConfigureAwait(false);

            string slice = await tools
                .ListMail("inbox", limit: 100, since: "14d", until: "7d")
                .ConfigureAwait(false);

            Console.WriteLine("       " + Summarise(fortnight));
            Console.WriteLine("       " + Summarise(slice));

            DateTime[] inFortnight = Timestamps(fortnight);
            DateTime[] inSlice = Timestamps(slice);

            failures += Check2(
                $"a fortnight ({inFortnight.Length}) is never fewer than a week ({stamps.Length})",
                inFortnight.Length >= stamps.Length);

            failures += Check2(
                $"and the slice ({inSlice.Length}) is exactly the difference, so the upper bound holds",
                inSlice.Length == inFortnight.Length - stamps.Length);

            if (inSlice.Length > 0)
            {
                failures += Check2(
                    "with every message in it inside the slice",
                    inSlice.All(d => d >= DateTime.Now.Date.AddDays(-14) && d < cutoff));
            }
            else
            {
                // A genuinely quiet week, now that the arithmetic above has said so. It must
                // still not read as "the folder is empty".
                failures += Check2(
                    "an empty slice says the folder itself is not empty",
                    slice.Contains("folder itself holds", StringComparison.Ordinal));
            }

            // A window with nothing in it, deliberately: tomorrow onwards.
            string future = await tools
                .ListMail("inbox", limit: 10, since: "2026-12-31")
                .ConfigureAwait(false);

            failures += Check2(
                "a window that matches nothing distinguishes itself from an empty folder",
                future.Contains("folder itself holds", StringComparison.Ordinal));

            // Reversed bounds are a plausible model mistake and must be refused rather than
            // silently producing an empty window.
            string reversed = await tools
                .ListMail("inbox", since: "7d", until: "14d")
                .ConfigureAwait(false);

            failures += Check2(
                "reversed bounds are refused, not answered with nothing",
                reversed.StartsWith("error:", StringComparison.Ordinal));

            string nonsense = await tools.ListMail("inbox", since: "letzte Woche").ConfigureAwait(false);

            failures += Check2(
                "and prose instead of a date is refused with the forms that work",
                nonsense.StartsWith("error:", StringComparison.Ordinal)
                && nonsense.Contains("7d", StringComparison.Ordinal));

            // -------------------------------------------------------------- mail_search
            //
            // The search is checked against a message the listing has already shown, so the
            // expected hit is known without anybody having to have a particular mail. Whether
            // the index or the walk finds it does not matter -- both are legitimate answers,
            // and which one answered is in the output.
            string? term = DistinctiveWord(inbox);
            string? expected = ExtractFirstId(inbox);

            if (term is not null && expected is not null)
            {
                string hit = await tools.SearchMail(term, limit: 50).ConfigureAwait(false);

                failures += Check($"mail_search '{term}'", hit);
                Console.WriteLine("       " + Summarise(hit));

                bool same = hit.Contains(expected, StringComparison.Ordinal);

                failures += Check2(
                    "a word from a message in the inbox finds that message again",
                    same);

                // An id that does not match is worth showing rather than merely counting: the
                // two sides come from different Outlook interfaces -- Items and Table -- and
                // whether they agree on the shape of an entry id is exactly the question.
                if (!same)
                {
                    string? got = ExtractFirstId(hit);

                    Console.WriteLine($"       expected {Head(expected)}");
                    Console.WriteLine($"       got      {Head(got)}");
                }

                failures += Check2(
                    "and the answer says which way found it",
                    hit.Contains("via the Windows Search index", StringComparison.Ordinal)
                    || hit.Contains("via a walk", StringComparison.Ordinal));
            }
            else
            {
                Console.WriteLine("  ..   mail_search        skipped, no usable word in the newest subjects");
            }

            // An umlaut has to survive the round trip into a DASL string literal, which means
            // this one check makes the file UTF-8 rather than ASCII. Deliberate: a search term
            // the user would actually type is worth more than a uniform encoding, and the
            // compiler reads UTF-8 without being asked.
            string umlaut = await tools
                .SearchMail("Grüße", limit: 5)
                .ConfigureAwait(false);

            failures += Check2(
                "a search term with an umlaut is answered rather than failing",
                !umlaut.StartsWith("error:", StringComparison.Ordinal));

            // The empty result, which is the one that must not read as a silent failure.
            string missing = await tools
                .SearchMail("zzqqxx nichtvorhanden", limit: 5)
                .ConfigureAwait(false);

            Console.WriteLine("       " + Summarise(missing));

            failures += Check2(
                "nothing found says that BOTH ways looked",
                missing.Contains("Both looked", StringComparison.Ordinal));

            string tooShort = await tools.SearchMail("a b").ConfigureAwait(false);

            failures += Check2(
                "a query with nothing searchable in it is refused, not run",
                tooShort.StartsWith("error:", StringComparison.Ordinal));

            // ------------------------------------------- forwarding and answering one person
            //
            // Everything created here is removed again at the end. A harness that leaves
            // three drafts and a meeting in somebody's mailbox every run is a harness they
            // stop running, and these are the operations where "it looked like it worked" is
            // the failure: a draft addressed to an unresolved name is indistinguishable from
            // a finished one until somebody presses Send.
            var toRemove = new List<string>();

            if (expected is not null)
            {
                OutlookClient.Mailbox mine = await client.OwnMailboxAsync().ConfigureAwait(false);
                string me = mine.Address;

                // The DISPLAY name, not Environment.UserName. The address book knows
                // "Kluge, Thomas"; it has never heard of "mutkluge", and the first run of
                // this check failed for that reason rather than for a fault in the tool.
                string myName = mine.Name;

                string forwarded = await tools
                    .ForwardDraft(expected, me, "Testkommentar vom Prüfstand.")
                    .ConfigureAwait(false);

                failures += Check("mail_forward_draft", forwarded);
                Console.WriteLine("       " + Summarise(forwarded));

                failures += Check2(
                    "a forward says who it resolved the recipient to",
                    forwarded.Contains('<', StringComparison.Ordinal)
                    || forwarded.Contains(me, StringComparison.OrdinalIgnoreCase));

                failures += Check2(
                    "and says it was not sent",
                    forwarded.Contains("NOT been sent", StringComparison.Ordinal));

                if (ExtractFirstId(forwarded) is { } forwardId)
                    toRemove.Add(forwardId);

                // A NAME rather than an address, which is the half of the requirement that
                // needs the address book rather than a string assignment.
                string byName = await tools
                    .ReplyDraft(expected, "Antwort nur an eine Person.", to: myName)
                    .ConfigureAwait(false);

                failures += Check($"mail_reply_draft to a name ({myName})", byName);
                Console.WriteLine("       " + Summarise(byName));

                failures += Check2(
                    "a reply to one person quotes the original underneath",
                    byName.Contains("quoted below", StringComparison.Ordinal));

                if (ExtractFirstId(byName) is { } replyId)
                    toRemove.Add(replyId);

                // The two ways of addressing contradict each other and must not be guessed at.
                string both = await tools
                    .ReplyDraft(expected, "x", replyAll: true, to: me)
                    .ConfigureAwait(false);

                failures += Check2(
                    "replyAll together with 'to' is refused rather than resolved by guessing",
                    both.StartsWith("error:", StringComparison.Ordinal));

                string nobody = await tools
                    .ForwardDraft(expected, "Zzqq Nichtvorhanden", "x")
                    .ConfigureAwait(false);

                failures += Check2(
                    "an unresolvable recipient saves nothing and says so",
                    nobody.StartsWith("error:", StringComparison.Ordinal));
            }

            // ------------------------------------------------------------ creating a meeting
            string when = DateTime.Now.Date.AddDays(1).AddHours(15)
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            string made = await tools
                .CreateAppointment($"Shellvis probe {DateTime.Now:HHmmss}", when, durationMinutes: 30,
                    body: "Angelegt vom Prüfstand, wird gleich entfernt.")
                .ConfigureAwait(false);

            failures += Check("calendar_create", made);
            Console.WriteLine("       " + Summarise(made));

            failures += Check2(
                "an appointment without attendees says nobody was told",
                made.Contains("nobody else was told", StringComparison.Ordinal));

            failures += Check2(
                "and repeats the date with its weekday, so a wrong one is visible",
                made.Contains(DateTime.Now.Date.AddDays(1).ToString("ddd yyyy-MM-dd", CultureInfo.InvariantCulture),
                    StringComparison.Ordinal));

            if (ExtractFirstId(made) is { } madeId)
                toRemove.Add(madeId);

            // A date with no time would be midnight, which is never what anybody meant.
            string midnight = await tools.CreateAppointment("x", "2026-12-24").ConfigureAwait(false);

            failures += Check2(
                "a date without a time is refused rather than booked at midnight",
                midnight.StartsWith("error:", StringComparison.Ordinal)
                && midnight.Contains("midnight", StringComparison.Ordinal));

            string backwards = await tools
                .CreateAppointment("x", "tomorrow 15:00", end: "tomorrow 14:00")
                .ConfigureAwait(false);

            failures += Check2(
                "an end before the start is refused",
                backwards.StartsWith("error:", StringComparison.Ordinal));

            // ------------------------------------------------------- the watcher's own look
            //
            // Read-only, and the one part of the watcher a harness cannot fake: whether the
            // calendar restriction and the inbox scan actually find anything in a real
            // mailbox, and whether the classification holds up against real senders.
            var pretend = new WatchState { SeenUpTo = DateTime.Now.AddHours(-24) };

            WatchFindings look = await client
                .LookAsync(pretend, DateTime.Now, TimeSpan.FromHours(12))
                .ConfigureAwait(false);

            Console.WriteLine(
                $"       found {look.Appointments.Count} appointment(s) in the next 12h and "
                + $"{look.Arrivals.Count} arrival(s) in the last 24h");

            failures += Check2(
                "a look at a real mailbox returns without throwing",
                true);

            foreach (Arrival arrival in look.Arrivals.Take(4))
            {
                Console.WriteLine(
                    $"       {arrival.Received:HH:mm}  {arrival.Kind}"
                    + (arrival.TicketKey is { } k ? $" {k}" : string.Empty));
            }

            failures += Check2(
                "every arrival carries an id, which is what any follow-up needs",
                look.Arrivals.All(a => a.EntryId.Length > 0));

            failures += Check2(
                "every arrival is newer than the mark, so nothing already seen comes back",
                look.Arrivals.All(a => a.Received > pretend.SeenUpTo));

            failures += Check2(
                "a ticket notification, if any arrived, carries a key",
                look.Arrivals.All(a => a.Kind != ArrivalKind.TicketNotification || a.TicketKey is not null));

            // Printed, not merely counted. The first version of the calendar restriction
            // returned appointments from outside the window and there was no way to tell
            // which ones without seeing them; a check that only says "some are wrong" costs
            // another run to learn anything.
            foreach (Upcoming outside in look.Appointments
                .Where(a => a.MinutesAway < -1 || a.MinutesAway > 12 * 60 + 1)
                .Take(4))
            {
                Console.WriteLine(
                    $"       OUTSIDE: {outside.Start:ddd yyyy-MM-dd HH:mm} "
                    + $"({outside.MinutesAway} min) \"{outside.Subject}\"");
            }

            failures += Check2(
                "appointments are inside the window that was asked for",
                look.Appointments.All(a => a.MinutesAway >= -1 && a.MinutesAway <= 12 * 60 + 1));

            // A FIRST look must be silent. This is what stops Shellvis greeting somebody
            // with an hour of history every time it starts.
            WatchFindings first = await client
                .LookAsync(new WatchState(), DateTime.Now, TimeSpan.FromHours(12))
                .ConfigureAwait(false);

            failures += Check2(
                "a first look reports no mail at all, only sets the mark",
                first.Arrivals.Count == 0);

            // -------------------------------------------------------------------- cleaning up
            int removed = 0;

            foreach (string id in toRemove)
            {
                if (await client.DeleteItemAsync(id).ConfigureAwait(false))
                    removed++;
            }

            failures += Check2(
                $"the probe removed everything it created ({removed} of {toRemove.Count})",
                removed == toRemove.Count);

            // Reading one message end to end proves the id round-trips, which is what
            // every other mail operation depends on.
            string? firstId = ExtractFirstId(inbox);
            if (firstId is not null)
            {
                string body = await tools.ReadMail(firstId).ConfigureAwait(false);
                failures += Check("mail_read", body);
                Console.WriteLine($"       returned {body.Length:N0} characters");

                // ------------------------------------------------- thread and history
                //
                // The thread must contain the message it was asked about. That sounds
                // trivial and is the check that catches the real failure: a conversation
                // key that matches nothing comes back as an empty list, which reads
                // exactly like an answer. The calendar filter failed that way for half the
                // days of a month and nobody noticed.
                string thread = await tools.ReadThread(firstId).ConfigureAwait(false);
                failures += Check("mail_thread", thread);
                Console.WriteLine("       " + Summarise(thread));

                failures += Report("the thread contains the message it was asked about",
                    thread.Contains(firstId, StringComparison.Ordinal));

                // Oldest first: a conversation is read in the order it was said, unlike an
                // inbox listing.
                failures += Report("the thread is ordered oldest first", IsAscending(thread));

                // A thread of one proves nothing about collecting a thread: the anchor is
                // always in it. So the inbox is sampled for a conversation that really has
                // several messages, and if the mailbox holds none the probe SAYS so rather
                // than letting the trivial case stand in for the real one.
                int widest = 1;
                string widestThread = thread;

                foreach (string candidate in AllIds(inbox).Take(5))
                {
                    string other = await tools.ReadThread(candidate).ConfigureAwait(false);
                    int count = MessageCount(other);

                    if (count > widest)
                    {
                        widest = count;
                        widestThread = other;
                    }
                }

                if (widest > 1)
                {
                    Console.WriteLine($"       widest conversation found: {widest} messages");
                    failures += Report("a multi-message conversation is collected", true);
                    failures += Report("and it too runs oldest first", IsAscending(widestThread));
                }
                else
                {
                    Console.WriteLine(
                        "  ..   no conversation with more than one message in the sampled inbox;");
                    Console.WriteLine(
                        "       collecting across folders is therefore NOT proven by this run.");
                }

                string sender = SenderOf(inbox);
                Console.WriteLine($"       history for: {sender}");

                if (sender.Length > 0)
                {
                    string history = await tools.ReadHistory(sender).ConfigureAwait(false);
                    failures += Check("mail_history", history);
                    Console.WriteLine("       " + Summarise(history));

                    failures += Report("the history names the person asked for",
                        history.Contains(sender, StringComparison.OrdinalIgnoreCase));
                }

                string nobody = await tools.ReadHistory("zzz-nobody-by-this-name-zzz").ConfigureAwait(false);
                failures += Report("an empty history says so instead of inventing one",
                    nobody.Contains("no recent messages", StringComparison.Ordinal));
                failures += Report("and tells the model not to fill it in",
                    nobody.Contains("do not fill it in", StringComparison.Ordinal));

                failures += Report("an empty person is refused",
                    (await tools.ReadHistory("  ").ConfigureAwait(false))
                        .StartsWith("error:", StringComparison.Ordinal));
                failures += Report("an empty message id is refused",
                    (await tools.ReadThread("").ConfigureAwait(false))
                        .StartsWith("error:", StringComparison.Ordinal));
            }
            else
            {
                Console.WriteLine("  ok   mail_read            skipped, no message id available");
            }

            // --------------------------------------------------------- calendar
            string calendar = await tools.ListAppointments().ConfigureAwait(false);
            failures += Check("calendar_list", calendar);
            Console.WriteLine("       " + Summarise(calendar));

            // Today alone, which is the question that was answered wrongly: "welche Termine
            // liegen heute an" reached the tool as from == to and could never match anything.
            // Asked against the real calendar, because the arithmetic is checked above and
            // what remains to confirm is that Outlook agrees.
            string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string singleDay = await tools.ListAppointments(today, today).ConfigureAwait(false);

            failures += Check("calendar_list for one day", singleDay);
            Console.WriteLine("       " + Summarise(singleDay));

            // Cross-checked against the surrounding week rather than against a hard-coded
            // expectation: a day that shows nothing while the week shows something on that
            // same day is the exact shape of the reported defect, and an empty calendar is a
            // legitimate state that must not be reported as a failure.
            bool weekMentionsToday = calendar.Contains(
                DateTime.Today.ToString("ddd", CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase);

            if (weekMentionsToday)
            {
                failures += Check2(
                    "a day the week-long query lists is not empty when asked alone",
                    !singleDay.StartsWith("no appointments", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Console.WriteLine("  ok   single-day cross-check  skipped, the week shows nothing today");
            }

            // --------------------------------------------------------- contacts
            string contacts = await tools.FindContacts("a", limit: 3).ConfigureAwait(false);
            failures += Check("contacts_find", contacts);
            Console.WriteLine("       " + Summarise(contacts));

            // ------------------------------------------------------------ tasks
            //
            // A full round trip: list, create, find the created one, close it, and confirm
            // it left the open list. Written this way because each half alone proves
            // nothing -- a create that is never read back may have written to the wrong
            // folder, and a complete that is never re-listed may have set a flag Outlook
            // ignores. The probe cleans up after itself: it deletes the task it made, so a
            // regression run does not gradually fill the user own task list.
            string before = await tools.ListTasks().ConfigureAwait(false);
            failures += Check("task_list", before);
            Console.WriteLine("       " + Summarise(before));

            string subject = $"Shellvis probe {DateTime.Now:HHmmss}";
            string due = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            string created = await tools.CreateTask(subject, due, "written by probe outlook")
                .ConfigureAwait(false);

            failures += Check("task_create", created);
            Console.WriteLine("       " + Summarise(created));

            string listed = await tools.ListTasks().ConfigureAwait(false);
            failures += Report("the created task is in the list",
                listed.Contains(subject, StringComparison.Ordinal));

            string? taskId = IdOfTask(listed, subject);
            failures += Report("and it carries an id to act on", taskId is not null);

            // A bad date must be refused, not guessed at. Reading "01.09.2026" by the
            // machine culture is exactly the confusion that made the calendar filter
            // return an empty week for half the days of a month.
            string badDate = await tools.CreateTask("should not exist", "01.09.2026").ConfigureAwait(false);
            failures += Report("an ambiguous due date is refused rather than guessed",
                badDate.StartsWith("error:", StringComparison.Ordinal));

            string noSubject = await tools.CreateTask("  ").ConfigureAwait(false);
            failures += Report("a task with no subject is refused",
                noSubject.StartsWith("error:", StringComparison.Ordinal));

            if (taskId is not null)
            {
                string closed = await tools.CompleteTask(taskId).ConfigureAwait(false);
                failures += Check("task_complete", closed);
                Console.WriteLine("       " + Summarise(closed));

                string after = await tools.ListTasks().ConfigureAwait(false);
                failures += Report("a completed task leaves the open list",
                    !after.Contains(subject, StringComparison.Ordinal));

                string withDone = await tools.ListTasks(includeComplete: true).ConfigureAwait(false);
                failures += Report("and is still there when completed ones are asked for",
                    withDone.Contains(subject, StringComparison.Ordinal));

                // Closing something already closed is a no-op that says so, not a failure.
                string again = await tools.CompleteTask(taskId).ConfigureAwait(false);
                failures += Report("closing it twice says so instead of failing",
                    again.Contains("already", StringComparison.Ordinal));

                await DeleteTaskAsync(apartment, taskId).ConfigureAwait(false);

                string cleaned = await tools.ListTasks(includeComplete: true).ConfigureAwait(false);
                failures += Report("the probe removes the task it created",
                    !cleaned.Contains(subject, StringComparison.Ordinal));
            }

            failures += Report("an unknown task id is an error, not a crash",
                (await tools.CompleteTask("not-an-entry-id").ConfigureAwait(false)).Length > 0);
        }

        // Started by us because it was closed, so closed again by us.
        //
        // This is the correction to a real failure: on a machine where Outlook was not running,
        // the probe's own calls launched it -- COM activation does that on demand -- and the
        // leak check then reported the instance it had itself caused as a leak. The check was
        // right that something was left behind and wrong about what to do: the answer is to put
        // the machine back as it was found, not to call a legitimate launch a defect.
        //
        // Only ever an instance the probe caused. An Outlook the user had open is never touched,
        // for the obvious reason.
        if (!wasRunning && IsOutlookRunning())
        {
            Console.WriteLine();
            Console.WriteLine("  ..   Outlook was not running and these calls started it; closing it again.");

            using (var closer = new ComApartment("Shellvis probe teardown"))
            {
                await closer.InvokeAsync(() =>
                {
                    dynamic? outlook = null;

                    try
                    {
                        outlook = Shellvis.Core.Office.Com.TryGetActive("Outlook.Application");
                        outlook?.Quit();
                    }
                    catch (Exception)
                    {
                        // A refusal to quit is reported by the check below rather than thrown:
                        // Outlook declines when it has an unsaved item open, which is a state
                        // and not a fault in this code.
                    }
                    finally
                    {
                        Shellvis.Core.Office.Com.Release(outlook);
                    }
                }).ConfigureAwait(false);
            }
        }

        // Polled, not slept. Outlook's Quit returns immediately and the process then spends
        // seconds closing stores and flushing a profile -- a fixed 2.5 second wait declared a
        // shutdown that was simply still in progress to be a leaked process. A fixed window for
        // an asynchronous teardown is always a wager, and this project already lost it once on
        // the Excel export path.
        // Only for an instance the probe started. Waiting for the user's own Outlook to exit
        // would be waiting for something that must not happen, for twenty seconds.
        if (!wasRunning)
        {
            for (int waited = 0; waited < 20000 && IsOutlookRunning(); waited += 500)
                await Task.Delay(500).ConfigureAwait(false);
        }
        else
        {
            await Task.Delay(2500).ConfigureAwait(false);
        }

        bool stillRunning = IsOutlookRunning();
        bool leaked = stillRunning && !wasRunning;

        Console.WriteLine();
        Console.WriteLine(leaked
            ? "  FAIL zombie             OUTLOOK.EXE was started by the probe and is still running"
            : $"  ok   no leak            Outlook running: {stillRunning} (unchanged from {wasRunning})");

        if (leaked)
            failures++;

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: Outlook automation works and releases cleanly."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    private static bool IsOutlookRunning() =>
        System.Diagnostics.Process.GetProcessesByName("OUTLOOK").Length > 0;

    /// <summary>
    /// Whether the timestamps in a rendered thread run forwards.
    ///
    /// Read off the rendered text rather than off the list, deliberately. The rendering is
    /// what the model sees, and this project has already had a defect that lived entirely
    /// between a correct data structure and what went out on the wire.
    /// </summary>
    private static bool IsAscending(string rendered)
    {
        var stamps = new List<DateTime>();

        foreach (string line in rendered.ReplaceLineEndings("\n").Split("\n"))
        {
            string[] parts = line.Trim().Split("  ", StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2)
                continue;

            // The rendered form is "Mon 2026-08-24 09:15  Sender", so the date sits after
            // the weekday and before the two spaces that start the name.
            string[] fields = parts[0].Split(" ", StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length >= 3 && DateTime.TryParse(
                    $"{fields[1]} {fields[2]}",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime when))
            {
                stamps.Add(when);
            }
        }

        // One message is trivially in order, and an unthreaded message is a real case.
        return stamps.Count < 2 || stamps.Zip(stamps.Skip(1)).All(pair => pair.First <= pair.Second);
    }

    /// <summary>Every message id in a listing.</summary>
    private static IEnumerable<string> AllIds(string listing)
    {
        foreach (string line in listing.ReplaceLineEndings("\n").Split("\n"))
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("id: ", StringComparison.Ordinal))
                yield return trimmed[4..].Trim();
        }
    }

    /// <summary>How many messages a rendered thread reports.</summary>
    private static int MessageCount(string rendered)
    {
        string first = rendered.ReplaceLineEndings("\n").Split("\n")[0].Trim();
        string[] parts = first.Split(" ", StringSplitOptions.RemoveEmptyEntries);

        return parts.Length > 0 && int.TryParse(parts[0], out int count) ? count : 0;
    }

    /// <summary>The display name on the first message of a listing, to ask a history about.</summary>
    private static string SenderOf(string listing)
    {
        foreach (string line in listing.ReplaceLineEndings("\n").Split("\n"))
        {
            // A listing line is "  [UNREAD ]yyyy-MM-dd HH:mm  Sender  "subject"".
            string[] parts = line.Split("  ", StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2 && parts[0].Any(char.IsAsciiDigit) && !parts[1].StartsWith("id ", StringComparison.Ordinal))
                return parts[1].Trim();
        }

        return string.Empty;
    }

    /// <summary>A plain assertion, in the same shape as Check so the output reads as one list.</summary>
    private static int Report(string what, bool passed)
    {
        Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}");
        return passed ? 0 : 1;
    }

    /// <summary>The id printed beside a task with this subject, if it is listed.</summary>
    private static string? IdOfTask(string listing, string subject)
    {
        foreach (string line in listing.ReplaceLineEndings("\n").Split("\n"))
        {
            if (!line.Contains(subject, StringComparison.Ordinal))
                continue;

            int marker = line.LastIndexOf("   id ", StringComparison.Ordinal);

            if (marker >= 0)
                return line[(marker + 6)..].Trim();
        }

        return null;
    }

    /// <summary>
    /// Remove the task the probe created.
    ///
    /// Direct COM rather than a tool, and deliberately: there is no delete tool, and there
    /// should not be one. Deleting from the user own task list is not something an agent
    /// needs to do, and this is a harness cleaning up after itself, which is a different
    /// thing from a capability being offered to a model.
    /// </summary>
    private static Task DeleteTaskAsync(ComApartment apartment, string entryId) =>
        apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? item = null;

            try
            {
                outlook = Shellvis.Core.Office.Com.TryGetActive("Outlook.Application");

                if (outlook is null)
                    return;

                session = outlook.Session;
                item = session.GetItemFromID(entryId);
                item.Delete();
            }
            catch (Exception)
            {
                // Cleanup that fails is worth knowing about but not worth failing the run:
                // the check that follows reports the leftover task by name.
            }
            finally
            {
                Shellvis.Core.Office.Com.ReleaseAll(outlook, session, item);
            }
        });

    private static int Check(string label, string result)
    {
        bool failed = result.StartsWith("error:", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  {(failed ? "FAIL" : "ok  ")} {label,-20} {(failed ? result : "returned data")}");
        return failed ? 1 : 0;
    }

    /// <summary>
    /// Report the shape of a result rather than its content: the first line carries
    /// the count, which is what proves the call worked.
    /// </summary>
    /// <summary>
    /// A word from the newest message's subject, long enough to be worth searching for.
    ///
    /// <b>Taken from the mailbox rather than written here.</b> A search check that looks for a
    /// fixed word passes or fails on whether the user happens to have such a mail, which is
    /// the wrong reason either way. Taking a word out of a message the listing has just shown
    /// makes the expected hit knowable: that message must come back.
    ///
    /// Letters only, at least five of them, so the word is distinctive and needs no escaping
    /// to be a fair test of the ordinary path -- the apostrophe case is checked separately and
    /// without Outlook.
    /// </summary>
    private static string? DistinctiveWord(string listing)
    {
        foreach (string raw in listing.ReplaceLineEndings("\n").Split('\n'))
        {
            int open = raw.IndexOf('"');
            int close = raw.LastIndexOf('"');

            if (open < 0 || close <= open + 1)
                continue;

            foreach (string word in raw[(open + 1)..close].Split(
                [' ', '\t', ':', ',', '.', '-', '/', '(', ')', '[', ']'],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 5 && word.All(char.IsLetter))
                    return word;
            }
        }

        return null;
    }

    /// <summary>
    /// The received times out of a rendered listing, read back from the text.
    ///
    /// Read from the OUTPUT on purpose, rather than from a second call that returns objects.
    /// What has to be true is that the answer the model sees stays inside the window it asked
    /// for, and that is a property of the text. The header carries timestamps too, so only
    /// entry lines are considered -- an entry is indented by two spaces and begins with its
    /// date, optionally behind the UNREAD flag.
    /// </summary>
    private static DateTime[] Timestamps(string listing)
    {
        var found = new List<DateTime>();

        foreach (string raw in listing.ReplaceLineEndings("\n").Split('\n'))
        {
            if (!raw.StartsWith("  ", StringComparison.Ordinal)
                || raw.StartsWith("  ...", StringComparison.Ordinal))
            {
                continue;
            }

            string line = raw.Trim();

            if (line.StartsWith("UNREAD ", StringComparison.Ordinal))
                line = line[7..];

            if (line.Length >= 16
                && DateTime.TryParseExact(
                    line[..16],
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime stamp))
            {
                found.Add(stamp);
            }
        }

        return [.. found];
    }

    /// <summary>An id shortened for the console: enough to compare, not enough to fill a line.</summary>
    private static string Head(string? id) => id switch
    {
        null => "(none)",
        { Length: 0 } => "(empty)",
        { Length: <= 24 } => $"{id}  ({id.Length} chars)",
        _ => $"{id[..24]}...  ({id.Length} chars)",
    };

    private static string Summarise(string result)
    {
        string first = result.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return first.Length <= 100 ? first : first[..100] + "...";
    }

    private static string? ExtractFirstId(string listing)
    {
        foreach (string line in listing.ReplaceLineEndings("\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("id: ", StringComparison.Ordinal))
                return trimmed[4..].Trim();
        }

        return null;
    }
}
