using System.Globalization;
using System.Text;

using Shellvis.Core.Assist;
using Shellvis.Core.Notes;
using Shellvis.Core.Office;

namespace Shellvis.Core.Tools;

/// <summary>
/// What is coming, and what has already been said about it.
///
/// <b>Why one tool instead of letting the model assemble it.</b> A reminder is worth having
/// only if it arrives once, before the thing it is about. Both halves are the sort of
/// requirement this project has learned not to leave to an instruction: a scheduled run is a
/// fresh session with no memory of the last one, so "you already mentioned that" is not
/// something the model can know, and a job firing every five minutes would announce the same
/// 11:00 meeting twelve times.
///
/// So the suppression is in the tool. What has been announced is simply not in the result.
///
/// <b>Read-only, and that is what makes it usable unattended.</b> Scheduled runs deny every
/// approval, deliberately: nobody is there to answer. A reminder that needed one would
/// silently never fire. Recording what was said is bookkeeping in this application's own
/// state, not a change to anything of the user's.
/// </summary>
public sealed class AgendaTools(
    OutlookClient? outlook = null,
    NoteStore? notes = null,
    ReminderLog? reminders = null)
{
    private readonly ReminderLog _log = reminders ?? new ReminderLog();

    [ShellvisTool(
        "agenda_due",
        SideEffect.ReadOnly,
        Description =
            "What needs saying NOW and has not been said yet: appointments starting within "
            + "the next few minutes, notes falling due, overdue tasks. Anything already "
            + "reported is left out, so this is safe to call on a timer. Use it for "
            + "reminders; use agenda_today for a summary of the whole day.",
        Glyph = "clock")]
    public async Task<string> Due(
        int withinMinutes = 20,
        CancellationToken cancellationToken = default)
    {
        int window = Math.Clamp(withinMinutes, 1, 24 * 60);

        DateTime now = DateTime.Now;
        DateTime horizon = now.AddMinutes(window);

        var lines = new List<string>();

        // Appointments first: they have a time and everything else does not.
        if (outlook is not null && OutlookClient.IsAvailable)
        {
            try
            {
                IReadOnlyList<AppointmentSummary> soon = await outlook
                    .ListAppointmentsAsync(now, horizon, cancellationToken)
                    .ConfigureAwait(false);

                // Only what has NOT started. A meeting already under way is not a reminder,
                // it is a reproach.
                IEnumerable<AppointmentSummary> upcoming = soon
                    .Where(a => !a.IsAllDay && a.Start >= now && a.Start <= horizon);

                foreach (AppointmentSummary appointment in _log.Fresh(upcoming, KeyOf))
                {
                    int minutes = (int)Math.Round((appointment.Start - now).TotalMinutes);

                    lines.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  in {minutes} min: {appointment}"));
                }
            }
            catch (Exception ex)
            {
                // One unavailable source must not silence the others. A note that falls due
                // today should still be said when Outlook is having a bad morning.
                lines.Add($"  (the calendar could not be read: {ex.Message})");
            }
        }

        if (notes is not null)
        {
            try
            {
                IReadOnlyList<Note> due = notes.Due(DateTime.Today, 20);

                foreach (Note note in _log.Fresh(due, n => $"note:{n.Id}"))
                    lines.Add($"  noted: {note}   id {note.Id}");
            }
            catch (Exception ex)
            {
                lines.Add($"  (the notes could not be read: {ex.Message})");
            }
        }

        if (lines.Count == 0)
        {
            // The ordinary outcome, and it has to be unmistakable. A scheduled run that says
            // nothing is what the user wants most of the time, and a model that reads
            // ambiguity here will manufacture something to report.
            return "nothing new to report. Say nothing.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Worth saying now:");

        foreach (string line in lines)
            sb.AppendLine(line);

        return sb.ToString();
    }

    [ShellvisTool(
        "agenda_today",
        SideEffect.ReadOnly,
        Description =
            "The whole of today at once: appointments, open tasks and notes falling due. "
            + "Unlike agenda_due this repeats things already mentioned, because a daily "
            + "summary is meant to be complete. Use it for a morning or evening briefing.",
        Glyph = "clock")]
    public async Task<string> Today(
        string? date = null,
        CancellationToken cancellationToken = default)
    {
        DateTime day = DateTime.Today;

        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!DateTime.TryParseExact(
                    date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime parsed))
            {
                return $"error: '{date}' is not a date I can read. Use yyyy-MM-dd.";
            }

            day = parsed;
        }

        var sb = new StringBuilder();

        sb.Append(string.Create(CultureInfo.InvariantCulture, $"{day:dddd, yyyy-MM-dd}"))
          .AppendLine(":");

        if (outlook is not null && OutlookClient.IsAvailable)
        {
            try
            {
                // Half-open through the end of the day. The alternative, ending at midnight
                // of the same date, drops everything on the day being asked about, which is
                // an off-by-one this project has already shipped once in this very client.
                IReadOnlyList<AppointmentSummary> appointments = await outlook
                    .ListAppointmentsAsync(day, day.AddDays(1), cancellationToken)
                    .ConfigureAwait(false);

                sb.AppendLine().AppendLine($"Appointments ({appointments.Count}):");

                if (appointments.Count == 0)
                    sb.AppendLine("  none.");

                foreach (AppointmentSummary appointment in appointments)
                    sb.Append("  ").AppendLine(appointment.ToString());
            }
            catch (Exception ex)
            {
                sb.AppendLine().AppendLine($"Appointments: could not be read ({ex.Message}).");
            }

            try
            {
                IReadOnlyList<TaskSummary> tasks = await outlook
                    .ListTasksAsync(includeComplete: false, limit: 20, cancellationToken)
                    .ConfigureAwait(false);

                sb.AppendLine().AppendLine($"Open tasks ({tasks.Count}):");

                if (tasks.Count == 0)
                    sb.AppendLine("  none.");

                foreach (TaskSummary task in tasks)
                    sb.Append("  ").Append(task).Append("   id ").AppendLine(task.EntryId);
            }
            catch (Exception ex)
            {
                sb.AppendLine().AppendLine($"Tasks: could not be read ({ex.Message}).");
            }
        }
        else
        {
            sb.AppendLine().AppendLine("Outlook is not available, so there is no calendar or "
                + "task list to report. Say that rather than guessing at one.");
        }

        if (notes is not null)
        {
            try
            {
                IReadOnlyList<Note> due = notes.Due(day, 20);

                sb.AppendLine().AppendLine($"Notes due ({due.Count}):");

                if (due.Count == 0)
                    sb.AppendLine("  none.");

                foreach (Note note in due)
                    sb.Append("  ").Append(note).Append("   id ").AppendLine(note.Id.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                sb.AppendLine().AppendLine($"Notes: could not be read ({ex.Message}).");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// What makes one reminder the same as another.
    ///
    /// The entry id where there is one, and subject plus start time otherwise. An expanded
    /// occurrence of a recurring series can arrive without an id, and falling back to the
    /// subject alone would suppress every week of a weekly meeting after the first.
    /// </summary>
    private static string KeyOf(AppointmentSummary appointment) =>
        appointment.EntryId.Length > 0
            ? $"appt:{appointment.EntryId}:{appointment.Start:yyyyMMddHHmm}"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"appt:{appointment.Subject}:{appointment.Start:yyyyMMddHHmm}");
}
