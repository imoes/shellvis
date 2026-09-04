using System.Globalization;

namespace Shellvis.Core.Office;

/// <summary>
/// What is on the desk right now, as numbers.
///
/// <b>The distinction this type exists to keep.</b> The three trays on the reference page are
/// a sorting by JUDGEMENT: needs an answer today, needs to be known, everything else. Nothing
/// in code can produce that sorting -- whether a mail needs an answer today is exactly what
/// somebody has to decide by reading it. So none of these fields claims to be a tray.
///
/// What they are is what a machine can count without deciding anything: how much is unread,
/// how much of it came from a person rather than from a system, how much is a meeting request
/// or a ticket notification, what starts today, and what is already late. The page shows them
/// as counts beside the rules and says, in words, that they are counted and not sorted.
///
/// The alternative -- putting a computed number under the heading "needs an answer today" --
/// would be presenting a guess as a triage somebody performed. That is the same class of
/// failure as the six invented appointments this project produced once, and it is worse here
/// because it would be wrong quietly, every time, in a place built to be trusted at a glance.
/// </summary>
/// <param name="Unread">Unread messages in the inbox, however old.</param>
/// <param name="FromPeople">Of the recent unread, those from a human sender.</param>
/// <param name="Automated">Of the recent unread, those from a system: reports, newsletters, notifications.</param>
/// <param name="MeetingRequests">Unread meeting requests, by message class rather than by subject.</param>
/// <param name="TicketMail">Unread mail carrying a ticket key from an automated sender.</param>
/// <param name="AppointmentsToday">Appointments left today, from now on.</param>
/// <param name="NextAppointment">When the next one starts, or null if the day is clear.</param>
/// <param name="OverdueTasks">Tasks with a due date in the past that are not complete.</param>
/// <param name="Scanned">How many messages were actually looked at, so a capped scan says so.</param>
/// <param name="TakenAt">When this was measured.</param>
public sealed record DeskSnapshot(
    int Unread,
    int FromPeople,
    int Automated,
    int MeetingRequests,
    int TicketMail,
    int AppointmentsToday,
    DateTime? NextAppointment,
    int OverdueTasks,
    int Scanned,
    DateTime TakenAt)
{
    /// <summary>An empty desk, for before the first measurement.</summary>
    public static DeskSnapshot Nothing { get; } =
        new(0, 0, 0, 0, 0, 0, null, 0, 0, DateTime.MinValue);

    /// <summary>
    /// The counts as the page reads them, keyed by the element that shows each one.
    ///
    /// Hand-written rather than serialised from the record, because the keys are part of the
    /// page's markup and renaming a property should not silently empty a box on screen. A
    /// missing key leaves its box showing a dash, which is the honest rendering of "not
    /// measured" and is what the page does before the first snapshot arrives.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, int> Counts => new Dictionary<string, int>
    {
        ["unread"] = Unread,
        ["people"] = FromPeople,
        ["automated"] = Automated,
        ["requests"] = MeetingRequests,
        ["tickets"] = TicketMail,
        ["today"] = AppointmentsToday,
        ["overdue"] = OverdueTasks,
    };

    /// <summary>
    /// When the next appointment starts, as a time somebody reads rather than a timestamp.
    ///
    /// Formatted here, in code, for the reason the skills give twice over: date arithmetic
    /// is the thing this application has got wrong before, and a page that does its own
    /// would be a third place for it to go wrong.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string NextAppointmentLabel => NextAppointment is not { } next
        ? "nichts mehr heute"
        : next.ToString("HH:mm", CultureInfo.CurrentCulture)
            + " (in " + Minutes(next - TakenAt) + ")";

    private static string Minutes(TimeSpan until)
    {
        int minutes = (int)Math.Round(until.TotalMinutes);

        if (minutes <= 0)
            return "läuft";

        return minutes < 60
            ? minutes.ToString(CultureInfo.CurrentCulture) + " Min."
            : (minutes / 60).ToString(CultureInfo.CurrentCulture) + " Std. "
                + (minutes % 60).ToString(CultureInfo.CurrentCulture) + " Min.";
    }

    /// <summary>
    /// Which counts have grown since <paramref name="before"/>, and by how much.
    ///
    /// <b>Growth, not difference.</b> A count that fell -- mail read, a task finished -- is
    /// not news and must not raise a badge: a badge that appears when you have just tidied up
    /// is a badge nobody believes twice. Only an increase is new, and the badge shows the
    /// increase rather than the total, because "+3" is the thing that arrived while "12" is
    /// the thing that was already there.
    /// </summary>
    public IReadOnlyDictionary<string, int> NewSince(DeskSnapshot? before)
    {
        var grown = new Dictionary<string, int>();

        // No previous measurement means nothing is NEW -- it means nothing is known yet.
        // Badging everything on the first look is the same mistake the watcher refuses to
        // make with a first run, and for the same reason.
        if (before is null || before.TakenAt == DateTime.MinValue)
            return grown;

        foreach ((string key, int now) in Counts)
        {
            int then = before.Counts.TryGetValue(key, out int was) ? was : 0;

            if (now > then)
                grown[key] = now - then;
        }

        return grown;
    }
}
