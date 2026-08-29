using System.Globalization;

namespace Shellvis.Core.Office;

/// <summary>One Outlook task, flattened to what an agent needs to decide what to do.</summary>
/// <param name="Due">
/// Outlook stores "no date" as 1 January 4501 rather than as null, so this is nullable here
/// and that sentinel is translated on the way in. Left alone it would sort every undated
/// task to the far future and print as a date nobody wrote.
/// </param>
public sealed record TaskSummary(
    string EntryId,
    string Subject,
    DateTime? Due,
    bool Complete,
    int Importance,
    int PercentComplete)
{
    /// <summary>Outlook's importance values.</summary>
    private static string Weight(int importance) => importance switch
    {
        2 => "high",
        0 => "low",
        _ => "normal",
    };

    /// <summary>
    /// One line, with the state a decision needs.
    ///
    /// Overdue is spelled out rather than left for the reader to work out from a date. The
    /// point of a task list in a secretary's hands is which ones are late, and this project
    /// has already learned twice that date arithmetic is not something to hand to the model:
    /// it named a Monday a Sunday even with today's date in its prompt. What must be right
    /// belongs in code.
    /// </summary>
    public override string ToString()
    {
        string state = Complete ? "done" : PercentComplete > 0 ? $"{PercentComplete}%" : "open";

        string due = Due is { } date
            ? string.Create(CultureInfo.InvariantCulture, $"  due {date:ddd yyyy-MM-dd}")
            : string.Empty;

        string late = !Complete && Due is { } deadline && deadline.Date < DateTime.Today
            ? "  OVERDUE"
            : string.Empty;

        string weight = Importance == 1 ? string.Empty : $"  ({Weight(Importance)})";

        return $"[{state}]{due}{late}  \"{Subject}\"{weight}";
    }
}

/// <summary>
/// Outlook tasks.
///
/// <b>Why tasks and not a list of our own.</b> The user already keeps one, in the same
/// application their mail and calendar live in, and it syncs to their phone. A second list
/// inside Shellvis would be a list nobody looks at, and a secretary who keeps her own
/// private list of your commitments has not helped you.
///
/// Folder 13 is olFolderTasks. It was the one default folder this client never reached.
/// </summary>
public sealed partial class OutlookClient
{
    private const int FolderTasks = 13;

    /// <summary>
    /// Outlook's "no due date".
    ///
    /// Not a sentinel anyone would guess: 1 January 4501. Compared by date rather than by
    /// exact equality, because the time component varies with the item's origin.
    /// </summary>
    private static readonly DateTime NoDate = new(4501, 1, 1);

    /// <summary>Open tasks, and optionally the finished ones too.</summary>
    public Task<IReadOnlyList<TaskSummary>> ListTasksAsync(
        bool includeComplete = false,
        int limit = 40,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync<IReadOnlyList<TaskSummary>>(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? folder = null;
            dynamic? items = null;

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                session = outlook.Session;
                folder = session.GetDefaultFolder(FolderTasks);
                items = folder.Items;

                // Sorted IN Outlook, not in C#. A task folder can hold thousands of items,
                // and pulling them all across the COM boundary to sort them here is the
                // mistake this client already avoided for mail.
                items.Sort("[DueDate]");

                var results = new List<TaskSummary>();
                int guard = 0;

                foreach (dynamic item in items)
                {
                    try
                    {
                        if (guard++ > 2000 || results.Count >= limit)
                            break;

                        bool complete = Flag(() => item.Complete);

                        if (complete && !includeComplete)
                            continue;

                        DateTime due = Date(() => item.DueDate);

                        results.Add(new TaskSummary(
                            EntryId: Str(() => item.EntryID),
                            Subject: Str(() => item.Subject),
                            Due: due.Date == NoDate.Date ? null : due,
                            Complete: complete,
                            Importance: Num(() => item.Importance),
                            PercentComplete: Num(() => item.PercentComplete)));
                    }
                    finally
                    {
                        Com.Release(item);
                    }
                }

                return results;
            }
            finally
            {
                Com.ReleaseAll(outlook, session, folder, items);
            }
        }, cancellationToken);
    }

    /// <summary>Add a task to the user's own list.</summary>
    public Task<string> CreateTaskAsync(
        string subject,
        DateTime? due,
        string? body,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? item = null;

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                // 3 is olTaskItem.
                item = outlook.CreateItem(3);
                item.Subject = subject;

                if (due is { } date)
                    item.DueDate = date;

                if (!string.IsNullOrWhiteSpace(body))
                    item.Body = body;

                item.Save();

                string when = due is { } d
                    ? string.Create(CultureInfo.InvariantCulture, $" due {d:ddd yyyy-MM-dd}")
                    : string.Empty;

                return $"task created:{when} \"{subject}\" (id {Str(() => item.EntryID)})";
            }
            finally
            {
                Com.ReleaseAll(outlook, item);
            }
        }, cancellationToken);
    }

    /// <summary>Mark one task finished.</summary>
    public Task<string> CompleteTaskAsync(string entryId, CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? item = null;

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                session = outlook.Session;
                item = session.GetItemFromID(entryId);

                string subject = Str(() => item.Subject);

                if (Flag(() => item.Complete))
                    return $"\"{subject}\" was already marked complete; nothing changed.";

                // Complete rather than PercentComplete = 100: setting the flag is what makes
                // Outlook stamp the completion date and drop the task off the open list.
                item.Complete = true;
                item.Save();

                return $"marked \"{subject}\" complete.";
            }
            finally
            {
                Com.ReleaseAll(outlook, session, item);
            }
        }, cancellationToken);
    }
}
