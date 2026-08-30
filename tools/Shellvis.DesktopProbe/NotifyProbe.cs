using Shellvis.Core.Cron;

namespace Shellvis.DesktopProbe;

/// <summary>
/// What decides whether something appears on the user's screen unbidden.
///
/// <b>Why this one is worth a harness.</b> Every other failure in this application costs the
/// person a retry. This one costs their attention, and it is spent whether the notice was
/// worth it or not: an alert for a routine result teaches the reader to dismiss the next one
/// unread, and then the alert that mattered is lost with the rest. The rule that prevents
/// that is a string convention -- absent by default, present only when the run says so -- and
/// a string convention is exactly the kind of thing that quietly inverts. A parser that
/// returned a headline for every report would look like a working feature.
///
/// So both directions are checked, and the negative one first: routine reports must raise
/// nothing at all.
/// </summary>
internal static class NotifyProbe
{
    public static int Run()
    {
        int failures = 0;

        Console.WriteLine("=== Notifications ===");
        Console.WriteLine();
        Console.WriteLine("-- nothing is announced unless a run asks --");

        failures += Silent("an ordinary report", "Checked the calendar. Nothing is due today.");
        failures += Silent("an empty result", "No new mail since the last run.");
        failures += Silent("a report that merely mentions the word", "There was nothing to notify you about.");
        failures += Silent("a marker with nothing after it", "All quiet.\nNOTIFY:");
        failures += Silent("a marker with only whitespace after it", "All quiet.\nNOTIFY:    ");
        failures += Silent("an empty report", string.Empty);

        Console.WriteLine();
        Console.WriteLine("-- and it is heard when it does --");

        failures += Raises(
            "a plain marker",
            "Two deadlines today.\n\nNOTIFY: IMIT-1204 is due at 16:00.",
            "IMIT-1204 is due at 16:00.");

        // A model told to write a literal marker will occasionally dress it. Refusing that
        // would make the convention brittle for no gain: the intent is unmistakable.
        failures += Raises(
            "a marker in bold",
            "Report.\n**NOTIFY:** The VPN certificate expires tomorrow.",
            "The VPN certificate expires tomorrow.");

        failures += Raises(
            "a marker in a bullet",
            "Report.\n- NOTIFY: Two invoices are overdue.",
            "Two invoices are overdue.");

        failures += Raises(
            "a lower-case marker",
            "Report.\nnotify: The build on main is red.",
            "The build on main is red.");

        Console.WriteLine();
        Console.WriteLine("-- the headline leaves the report it came from --");

        string report = "Three mails arrived, one needs an answer today.\n\nNOTIFY: Frau Weber is waiting on the quote.";
        string? headline = CronReport.TakeHeadline(ref report);

        failures += Check("the headline is taken", headline == "Frau Weber is waiting on the quote.", headline);
        failures += Check("the marker line is gone from the report",
            !report.Contains("NOTIFY", StringComparison.OrdinalIgnoreCase), report);

        failures += Check("the rest of the report survives",
            report.StartsWith("Three mails arrived", StringComparison.Ordinal), report);

        // The one case where the judgement is not the model's: a job that threw never got to
        // make one, and a scheduled task that stopped working is news by definition.
        Console.WriteLine();
        Console.WriteLine("-- a failed run announces itself --");

        var failure = new CronRunResult(
            "daily-briefing", false, "failed: the endpoint refused", TimeSpan.FromSeconds(2),
            Headline: "The scheduled job 'daily-briefing' failed.");

        failures += Check("a failure carries a headline of its own",
            failure.Headline is { Length: > 0 }, failure.Headline);

        failures += Check("a successful run carries none by default",
            new CronRunResult("x", true, "fine", TimeSpan.Zero).Headline is null);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: a routine run raises nothing, a run that asks is heard, and the\n"
                + "sentence is not said twice. The alert costs attention, so silence is the default."
            : $"{failures} notification check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static int Silent(string what, string report)
    {
        string text = report;
        string? headline = CronReport.TakeHeadline(ref text);

        return Check($"{what} raises nothing", headline is null, headline);
    }

    private static int Raises(string what, string report, string expected)
    {
        string text = report;
        string? headline = CronReport.TakeHeadline(ref text);

        return Check($"{what} is heard", headline == expected, headline);
    }

    private static int Check(string what, bool passed, string? detail = null)
    {
        Console.WriteLine($"   {(passed ? "ok  " : "FAIL")} {what}");

        if (!passed && detail is { Length: > 0 })
            Console.WriteLine($"        got: {detail}");

        return passed ? 0 : 1;
    }
}
