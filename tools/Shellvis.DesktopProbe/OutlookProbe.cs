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

    private static int Check2(string what, bool condition)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }

    public static async Task<int> RunAsync()
    {
        // Checked first, and without Outlook, because this is where the defect was.
        int rangeFailures = RangeChecks();

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

            // Reading one message end to end proves the id round-trips, which is what
            // every other mail operation depends on.
            string? firstId = ExtractFirstId(inbox);
            if (firstId is not null)
            {
                string body = await tools.ReadMail(firstId).ConfigureAwait(false);
                failures += Check("mail_read", body);
                Console.WriteLine($"       returned {body.Length:N0} characters");
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
