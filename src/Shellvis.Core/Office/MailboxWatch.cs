using System.Globalization;
using System.Text;

namespace Shellvis.Core.Office;

/// <summary>What kind of arriving message this is, which decides how it should be handled.</summary>
public enum ArrivalKind
{
    /// <summary>A mail from a person.</summary>
    Ordinary,

    /// <summary>An invitation. The interesting question is whether it collides with something.</summary>
    MeetingRequest,

    /// <summary>
    /// An automated notification from Jira or the service desk. Its content is template;
    /// the ticket it names is where the substance is.
    /// </summary>
    TicketNotification,
}

/// <param name="Kind">How it should be handled, decided before any model sees it.</param>
/// <param name="TicketKey">The ticket a notification is about, when it is one.</param>
public sealed record Arrival(
    string EntryId,
    string From,
    string Subject,
    DateTime Received,
    ArrivalKind Kind,
    string? TicketKey);

/// <param name="MinutesAway">How long until it starts. Negative means it has begun.</param>
public sealed record Upcoming(
    string EntryId,
    string Subject,
    DateTime Start,
    string Location,
    bool HasTeamsLink,
    int MinutesAway);

/// <summary>
/// What one look at the mailbox turned up.
/// </summary>
public sealed record WatchFindings(
    IReadOnlyList<Upcoming> Appointments,
    IReadOnlyList<Arrival> Arrivals)
{
    public static WatchFindings Nothing { get; } = new([], []);

    public bool Any => Appointments.Count > 0 || Arrivals.Count > 0;

    /// <summary>
    /// The facts, laid out for a model to judge.
    ///
    /// <b>Facts and not a conclusion.</b> The watcher's job is to notice; deciding whether
    /// something is worth interrupting somebody for is the judgement, and that is what the
    /// model is asked. So this says what arrived and no more -- no "important", no "urgent",
    /// nothing that would put the answer in the question.
    /// </summary>
    public string Describe()
    {
        var sb = new StringBuilder();

        if (Appointments.Count > 0)
        {
            sb.AppendLine("Appointments starting soon:");

            foreach (Upcoming item in Appointments)
            {
                sb.Append("  in ").Append(item.MinutesAway).Append(" min: ")
                  .Append(item.Start.ToString("ddd HH:mm", CultureInfo.InvariantCulture))
                  .Append("  \"").Append(item.Subject).Append('"');

                if (item.Location.Length > 0)
                    sb.Append("  @ ").Append(item.Location);

                if (item.HasTeamsLink)
                    sb.Append("  [Teams]");

                sb.AppendLine();
            }
        }

        if (Arrivals.Count > 0)
        {
            if (sb.Length > 0)
                sb.AppendLine();

            sb.AppendLine("Mail that arrived since the last look:");

            foreach (Arrival mail in Arrivals)
            {
                sb.Append("  ").Append(mail.Received.ToString("HH:mm", CultureInfo.InvariantCulture))
                  .Append("  ").Append(mail.From)
                  .Append("  \"").Append(mail.Subject).Append('"');

                sb.Append(mail.Kind switch
                {
                    ArrivalKind.MeetingRequest => "  [meeting request]",
                    ArrivalKind.TicketNotification => $"  [ticket notification: {mail.TicketKey}]",
                    _ => string.Empty,
                });

                sb.Append("  id: ").AppendLine(mail.EntryId);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// What the watcher has already seen, so it does not say the same thing twice.
///
/// <b>Persisted, because a restart is not news.</b> Without this, starting Shellvis would
/// announce every mail of the last hour and every meeting of the afternoon, which is the
/// behaviour that teaches somebody to ignore the alert entirely. It lives beside jobs.json in
/// the profile.
/// </summary>
public sealed class WatchState
{
    /// <summary>Received times at or before this have been considered. Null on a first run.</summary>
    public DateTime? SeenUpTo { get; set; }

    /// <summary>Appointment ids already announced, so a ten-minute warning is given once.</summary>
    public List<string> AnnouncedAppointments { get; set; } = [];

    /// <summary>When the model was last asked, for the rate limit.</summary>
    public DateTime? LastAsked { get; set; }

    /// <summary>
    /// Trimmed, because this file is written every poll and a list of every appointment ever
    /// announced would grow without limit. Fifty is several days of meetings, and an id that
    /// falls off the end belongs to a meeting that started long ago.
    /// </summary>
    public void Remember(string appointmentId)
    {
        if (AnnouncedAppointments.Contains(appointmentId))
            return;

        AnnouncedAppointments.Add(appointmentId);

        if (AnnouncedAppointments.Count > 50)
            AnnouncedAppointments.RemoveRange(0, AnnouncedAppointments.Count - 50);
    }
}

/// <summary>
/// The rules about when the watcher may speak, with no Outlook and no model in them.
/// </summary>
public static class MailboxWatch
{
    /// <summary>
    /// Whether these findings are worth spending a model call on.
    ///
    /// <b>Two gates, and neither is about importance.</b> Importance is the model's judgement;
    /// these are about not asking at all. A poll that found nothing has nothing to ask about.
    /// And a poll that found something within minutes of the last question would interrupt a
    /// conversation that is still going: on this machine one turn against the local model
    /// takes between one and three minutes, so a watcher on a three-minute timer without a
    /// floor would keep it permanently busy answering itself.
    /// </summary>
    public static bool ShouldAsk(
        WatchFindings findings,
        DateTime now,
        DateTime? lastAsked,
        TimeSpan floor)
    {
        if (!findings.Any)
            return false;

        return lastAsked is not { } last || now - last >= floor;
    }

    /// <summary>
    /// The question put to the model, with the answer format it has to keep to.
    ///
    /// <b>SILENCE has to be the easy answer.</b> The default for a routine arrival is to say
    /// nothing: an alert for every mail teaches somebody to dismiss the next one unread, and
    /// then the one that mattered goes with it. So the instruction leads with that, and the
    /// examples of what does deserve an alert are specific rather than a plea to use
    /// judgement.
    /// </summary>
    public static string Prompt(WatchFindings findings) =>
        """
        You are looking at what has just arrived in Outlook. Decide whether any of it is
        worth interrupting the user with a desktop alert.

        Answer with SILENCE on its own if it is not. That is the normal answer, and it is
        the right one for routine mail, newsletters, automated reports, and anything the
        user would find on their own within the hour.

        Interrupt only for something that changes what they should do in the next hour or so:

        - an appointment starting soon that they may not be ready for, especially one with a
          Teams link or somewhere they have to walk to
        - a meeting request that collides with something already in the calendar; use
          calendar_list to check before saying so
        - a person waiting on an answer, or a deadline named in the mail
        - a ticket notification about one of THEIR tickets. Do not summarise the
          notification: take the key, call jira_issue and jira_comments (or the servicedesk
          equivalents), and say what the newest comment actually said and what it is waiting
          for. The mail is a trigger, the ticket is the source.

        If you do interrupt, answer with exactly one line of at most 140 characters, in the
        user's language, saying the thing itself rather than that something happened. Not
        "new mail from Weber" but "Weber needs the FTP password by 16:00".

        Here is what arrived:

        """ + Environment.NewLine + findings.Describe();

    /// <summary>Whether the model's answer means "say nothing".</summary>
    /// <remarks>
    /// Generous on purpose. A model that has been told to answer SILENCE will sometimes
    /// answer "SILENCE." or "SILENCE - nothing here matters", and treating any of those as a
    /// headline would put the word SILENCE on the user's desktop.
    /// </remarks>
    public static bool IsSilence(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return true;

        string first = answer.Trim().Split('\n')[0].Trim();

        return first.StartsWith("SILENCE", StringComparison.OrdinalIgnoreCase)
            || first.Equals("silence.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one line, cut to what a toast can hold.
    ///
    /// The alert window is one line wide. A model that answers with three sentences would
    /// otherwise have the first fifty characters shown and the rest silently dropped, so the
    /// cut is made here where it can be seen and end in an ellipsis rather than mid-word.
    /// </summary>
    public static string Headline(string answer, int limit = 140)
    {
        string line = answer.Trim().ReplaceLineEndings(" ");

        while (line.Contains("  ", StringComparison.Ordinal))
            line = line.Replace("  ", " ", StringComparison.Ordinal);

        if (line.Length <= limit)
            return line;

        int cut = line.LastIndexOf(' ', Math.Min(limit, line.Length - 1));

        return (cut > limit / 2 ? line[..cut] : line[..limit]).TrimEnd() + "...";
    }
}
