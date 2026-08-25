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
    public static async Task<int> RunAsync()
    {
        if (!OutlookClient.IsAvailable)
        {
            Console.WriteLine("Outlook is not registered for automation on this machine.");
            return 1;
        }

        bool wasRunning = IsOutlookRunning();
        Console.WriteLine($"Outlook running before the probe: {wasRunning}\n");

        int failures = 0;

        using (var apartment = new ComApartment("Shellvis probe COM"))
        {
            var tools = new OutlookTools(apartment);

            // ------------------------------------------------------------- mail
            string inbox = await tools.ListMail("inbox", limit: 5).ConfigureAwait(false);
            failures += Check("mail_list inbox", inbox);
            Console.WriteLine("       " + Summarise(inbox));

            string unread = await tools.ListMail("inbox", limit: 3, unreadOnly: true).ConfigureAwait(false);
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

            // --------------------------------------------------------- contacts
            string contacts = await tools.FindContacts("a", limit: 3).ConfigureAwait(false);
            failures += Check("contacts_find", contacts);
            Console.WriteLine("       " + Summarise(contacts));
        }

        // Give the release a moment to take effect before judging the outcome.
        await Task.Delay(2500).ConfigureAwait(false);

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
