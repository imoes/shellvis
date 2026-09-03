using Shellvis.Core.Office;

namespace Shellvis.DesktopProbe;

/// <summary>
/// When the mailbox watcher is allowed to speak, and what it says.
///
/// <b>Why this is a harness and not something to try out.</b> Every rule here is a rule
/// about NOT interrupting somebody, and a watcher that interrupts too often cannot be
/// evaluated by using it for ten minutes: it looks fine, and the cost arrives a week later
/// as somebody dismissing an alert without reading it. The interesting cases are also the
/// ones that are awkward to produce on purpose -- a model answering "SILENCE." with a full
/// stop, a restart that must not re-announce the morning, fifty stand-ups in a row.
///
/// All of it is free of Outlook and of any model, which is why it runs in the CI.
/// </summary>
internal static class WatchProbe
{
    public static int Run()
    {
        Console.WriteLine("watch: when the mailbox watcher may interrupt you\n");

        int failures = 0;
        var now = new DateTime(2026, 9, 3, 10, 0, 0);

        // ------------------------------------------------------------------ asking at all
        Console.WriteLine("-- whether a look is worth a question --");

        WatchFindings nothing = WatchFindings.Nothing;

        failures += Check(
            "a look that found nothing asks nothing",
            !MailboxWatch.ShouldAsk(nothing, now, lastAsked: null, TimeSpan.FromMinutes(10)));

        WatchFindings something = new(
            [],
            [new Arrival("1", "Weber", "FTP-Zugang", now.AddMinutes(-1), ArrivalKind.Ordinary, null)]);

        failures += Check(
            "something new, and nothing asked yet, asks",
            MailboxWatch.ShouldAsk(something, now, lastAsked: null, TimeSpan.FromMinutes(10)));

        failures += Check(
            "something new two minutes after the last question stays quiet",
            !MailboxWatch.ShouldAsk(something, now, now.AddMinutes(-2), TimeSpan.FromMinutes(10)));

        failures += Check(
            "and asks again once the floor has passed",
            MailboxWatch.ShouldAsk(something, now, now.AddMinutes(-11), TimeSpan.FromMinutes(10)));

        // A floor of zero is a legitimate setting and must not mean "never".
        failures += Check(
            "a floor of zero means no floor rather than no questions",
            MailboxWatch.ShouldAsk(something, now, now, TimeSpan.Zero));

        // ------------------------------------------------------------------ saying nothing
        Console.WriteLine("\n-- silence, which is the normal answer --");

        foreach (string quiet in new[]
        {
            "SILENCE",
            "SILENCE.",
            "silence",
            "SILENCE - nothing here needs attention",
            "  SILENCE  ",
            "",
        })
        {
            failures += Check($"'{quiet}' is silence", MailboxWatch.IsSilence(quiet));
        }

        failures += Check("so is nothing at all", MailboxWatch.IsSilence(null));

        failures += Check(
            "but a real line is not",
            !MailboxWatch.IsSilence("Weber braucht das FTP-Passwort bis 16:00."));

        // ------------------------------------------------------------------ the one line
        Console.WriteLine("\n-- the line that reaches the desktop --");

        failures += Check(
            "a short answer is left alone",
            MailboxWatch.Headline("Weber braucht das FTP-Passwort bis 16:00.")
                == "Weber braucht das FTP-Passwort bis 16:00.");

        string wordy = new string('a', 60) + " " + new string('b', 60) + " " + new string('c', 60);
        string cut = MailboxWatch.Headline(wordy, 140);

        failures += Check($"a long one is cut ({cut.Length} chars)", cut.Length <= 143);
        failures += Check("and says that it was", cut.EndsWith("...", StringComparison.Ordinal));

        failures += Check(
            "it is cut at a space rather than mid-word",
            !cut[..^3].EndsWith('a') || !cut.Contains('b', StringComparison.Ordinal));

        failures += Check(
            "several lines become one, because the alert is one line",
            !MailboxWatch.Headline("erste Zeile\nzweite Zeile")
                .Contains('\n', StringComparison.Ordinal));

        // ------------------------------------------------------------ not twice in a row
        Console.WriteLine("\n-- not saying the same thing twice --");

        var state = new WatchState();
        state.Remember("meeting-1");
        state.Remember("meeting-1");

        failures += Check(
            "the same appointment remembered twice is remembered once",
            state.AnnouncedAppointments.Count == 1);

        for (int i = 0; i < 80; i++)
            state.Remember($"daily-{i}");

        failures += Check(
            $"the list is trimmed rather than growing for ever ({state.AnnouncedAppointments.Count})",
            state.AnnouncedAppointments.Count == 50);

        failures += Check(
            "and it keeps the NEWEST, because an id that fell off belongs to a past meeting",
            state.AnnouncedAppointments.Contains("daily-79")
            && !state.AnnouncedAppointments.Contains("daily-0"));

        // ------------------------------------------------------- what the model is shown
        Console.WriteLine("\n-- the facts put to the model --");

        var findings = new WatchFindings(
            [new Upcoming("a1", "Backlog Grooming", now.AddMinutes(12), "Raum 2", true, 12)],
            [
                new Arrival("m1", "Jira", "[JCUE-5915] kommentiert", now, ArrivalKind.TicketNotification, "JCUE-5915"),
                new Arrival("m2", "Meier, Anna", "Termin?", now, ArrivalKind.MeetingRequest, null),
                new Arrival("m3", "Newsletter", "Angebote", now, ArrivalKind.Ordinary, null),
            ]);

        string described = findings.Describe();
        Console.WriteLine(described.TrimEnd());

        failures += Check(
            "the minutes until an appointment are there, because that is what makes it urgent",
            described.Contains("in 12 min", StringComparison.Ordinal));

        failures += Check(
            "a Teams link is marked, because it changes whether you have to walk anywhere",
            described.Contains("[Teams]", StringComparison.Ordinal));

        failures += Check(
            "a ticket notification carries its key, so the model can go to the ticket",
            described.Contains("ticket notification: JCUE-5915", StringComparison.Ordinal));

        failures += Check(
            "a meeting request is marked as one",
            described.Contains("[meeting request]", StringComparison.Ordinal));

        failures += Check(
            "every arrival carries its id, so the model can open or answer it",
            described.Contains("id: m1", StringComparison.Ordinal)
            && described.Contains("id: m3", StringComparison.Ordinal));

        // The prompt must not put the answer in the question.
        string prompt = MailboxWatch.Prompt(findings);

        failures += Check(
            "the prompt leads with SILENCE being the normal answer",
            prompt.Contains("SILENCE", StringComparison.Ordinal)
            && prompt.Contains("normal answer", StringComparison.Ordinal));

        failures += Check(
            "and tells it to read the TICKET rather than summarise the notification",
            prompt.Contains("jira_comments", StringComparison.Ordinal)
            && prompt.Contains("trigger", StringComparison.Ordinal));

        failures += Check(
            "the facts carry no judgement of their own",
            !described.Contains("important", StringComparison.OrdinalIgnoreCase)
            && !described.Contains("urgent", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: the watcher stays quiet unless something is new, does not ask twice\n"
                + "within the floor, treats a model's 'SILENCE.' as silence, and shows the model\n"
                + "facts rather than conclusions."
            : $"{failures} check(s) failed.");

        Console.WriteLine();
        Console.WriteLine("NOT covered here: the look at Outlook itself, which needs a mailbox,");
        Console.WriteLine("and whether the model's judgement is any good, which needs a person.");

        return failures == 0 ? 0 : 1;
    }

    private static int Check(string what, bool condition)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }
}
