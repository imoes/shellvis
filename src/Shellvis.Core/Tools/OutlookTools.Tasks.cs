using System.Globalization;
using System.Text;

using Shellvis.Core.Office;

namespace Shellvis.Core.Tools;

/// <summary>
/// The user's Outlook task list.
///
/// <b>Reading is silent, writing asks.</b> Same rule as the rest of this surface: listing
/// tasks changes nothing, while creating one and closing one both put something into the
/// user's own list. Closing in particular is not reversible from here in any obvious way,
/// and a task marked done by mistake is a task that stops being done.
/// </summary>
public sealed partial class OutlookTools
{
    [ShellvisTool(
        "task_list",
        SideEffect.ReadOnly,
        Description =
            "List the user's Outlook tasks: what is open, when it is due, and what is "
            + "overdue. Use this before saying anything about what they still owe someone. "
            + "Set includeComplete to also see finished ones.",
        Glyph = "task")]
    public async Task<string> ListTasks(
        bool includeComplete = false,
        int limit = 40,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        try
        {
            IReadOnlyList<TaskSummary> tasks = await _outlook
                .ListTasksAsync(includeComplete, Math.Clamp(limit, 1, 200), cancellationToken)
                .ConfigureAwait(false);

            if (tasks.Count == 0)
            {
                // An empty list is an answer. Saying so plainly matters more here than
                // anywhere: this project has already produced a calendar of invented
                // appointments once, out of a query that legitimately found nothing.
                return (includeComplete
                    ? "there are no tasks in the Outlook task list."
                    : "there are no open tasks in the Outlook task list.") + StartNotice();
            }

            var sb = new StringBuilder();
            sb.Append(tasks.Count).AppendLine(includeComplete ? " task(s):" : " open task(s):");

            foreach (TaskSummary task in tasks)
                sb.Append("  ").Append(task).Append("   id ").AppendLine(task.EntryId);

            return sb.ToString() + StartNotice();
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "task_create",
        SideEffect.Mutating,
        Description =
            "Add a task to the user's Outlook task list. Give dueDate as yyyy-MM-dd. Use "
            + "this when a mail or a conversation leaves them owing something, so the "
            + "commitment lives where they already look for it rather than only in this "
            + "conversation.",
        PreviewParameter = "subject",
        Glyph = "task")]
    public async Task<string> CreateTask(
        string subject,
        string? dueDate = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(subject))
            return "error: a task needs a subject.";

        DateTime? due = null;

        if (!string.IsNullOrWhiteSpace(dueDate))
        {
            // Invariant and exact. A loose parse would read "01.09.2026" and "09/01/2026"
            // as whichever the machine's culture prefers, which is precisely the confusion
            // that made the calendar filter return an empty week for half the days of a
            // month. One written form, stated in the description.
            if (!DateTime.TryParseExact(
                    dueDate.Trim(),
                    ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsed))
            {
                return $"error: '{dueDate}' is not a date I can read. Use yyyy-MM-dd.";
            }

            due = parsed;
        }

        try
        {
            string created = await _outlook
                .CreateTaskAsync(subject.Trim(), due, notes, cancellationToken)
                .ConfigureAwait(false);

            return created + StartNotice();
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "task_complete",
        SideEffect.Mutating,
        Description =
            "Mark one Outlook task complete, by the id from task_list. Only do this when "
            + "the user has said the thing is done; finding no evidence against it is not "
            + "the same as evidence for it.",
        PreviewParameter = "taskId",
        Glyph = "task")]
    public async Task<string> CompleteTask(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(taskId))
            return "error: a task id is required. Get one from task_list.";

        try
        {
            string done = await _outlook
                .CompleteTaskAsync(taskId.Trim(), cancellationToken)
                .ConfigureAwait(false);

            return done + StartNotice();
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}
