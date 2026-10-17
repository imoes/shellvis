using Shellvis.Core.Office;

namespace Shellvis.Core.Desk;

/// <summary>
/// One line for somebody coming back to the machine: what needs them, and what is next.
///
/// <b>Why at the unlock and not on a timer.</b> The moment a person sits down again is the one
/// moment they want a summary and are not in the middle of something. A morning briefing
/// covers the start of the day; this covers lunch, a meeting, an afternoon in the server room.
///
/// <b>Why no model call.</b> The endpoint takes minutes for a paragraph, and a briefing that
/// arrives ten minutes after somebody sat down is a notification about the past. Everything
/// in the line is already counted: the sorting pass has judged what needs an answer, and the
/// desk walk knows the next appointment.
///
/// <b>When it says nothing.</b> After a short absence, and when nothing needs an answer, nothing
/// new arrived and no appointment is near. "Welcome back, nothing happened" is exactly the
/// kind of alert that teaches somebody to dismiss the next one.
/// </summary>
public static class ReturnBriefing
{
    /// <summary>
    /// How long somebody has to have been away for a briefing.
    ///
    /// Half an hour: shorter, and fetching a coffee earns a notification; longer, and a
    /// meeting that ran an hour would not.
    /// </summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    /// <summary>How near an appointment has to be to be worth naming on its own.</summary>
    public static readonly TimeSpan Soon = TimeSpan.FromHours(2);

    /// <summary>The line, or null when there is nothing worth saying.</summary>
    /// <param name="away">How long the session was locked.</param>
    /// <param name="before">The desk as it was counted when the session was locked, if it was.</param>
    /// <param name="now">The desk as counted after the unlock.</param>
    /// <param name="tally">What the sorting pass has judged.</param>
    /// <param name="german">Whether to say it in German.</param>
    /// <param name="at">The time now; a parameter so the rule can be tested.</param>
    public static string? Compose(
        TimeSpan away, DeskSnapshot? before, DeskSnapshot now, DeskTally tally, bool german, DateTime at)
    {
        if (away < Threshold)
            return null;

        var parts = new List<string>();

        if (tally.Answer > 0)
        {
            parts.Add(german
                ? $"{tally.Answer} {(tally.Answer == 1 ? "Mail braucht" : "Mails brauchen")} eine Antwort"
                : $"{tally.Answer} {(tally.Answer == 1 ? "mail needs" : "mails need")} a reply");
        }

        int arrived = before is null ? 0 : Math.Max(0, now.Unread - before.Unread);

        if (arrived > 0)
            parts.Add(german ? $"{arrived} neu, seit du weg warst" : $"{arrived} new while you were away");

        if (now.NextAppointment is { } next && next > at && next - at <= Soon)
            parts.Add(german ? $"nächster Termin um {next:HH:mm}" : $"next appointment at {next:HH:mm}");

        if (parts.Count == 0)
            return null;

        string hello = german ? "Willkommen zurück" : "Welcome back";

        return $"{hello}: {string.Join(", ", parts)}.";
    }
}
