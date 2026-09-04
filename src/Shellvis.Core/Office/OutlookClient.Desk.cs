using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Shellvis.Core.Desk;

namespace Shellvis.Core.Office;

public sealed partial class OutlookClient
{
    /// <summary>
    /// The MAPI property that carries a message's own identifier.
    ///
    /// PR_INTERNET_MESSAGE_ID. Named because a proptag is unreadable and looks like a typo,
    /// and reached through PropertyAccessor because the object model does not expose it: it
    /// is the only thing about a mail that stays the same when the mail is filed, so it is
    /// what the cache is keyed on.
    /// </summary>
    private const string MessageIdProperty =
        "http://schemas.microsoft.com/mapi/proptag/0x1035001F";

    /// <summary>
    /// How far back the classification of unread mail looks.
    /// </summary>
    /// <remarks>
    /// The total unread comes from the folder itself and costs nothing however large it is.
    /// Telling a person's mail from a system's means reading a sender off each item, and an
    /// inbox with four thousand unread messages in it would make that a minute of COM calls
    /// on a timer. Two hundred is the recent end of the pile, which is the part a desk is
    /// about; the snapshot reports how many it actually looked at so the page can say so.
    /// </remarks>
    private const int DeskScan = 200;

    /// <summary>
    /// Count what is on the desk: unread mail by kind, what starts today, what is late.
    ///
    /// <b>Counting only.</b> Nothing here decides whether a message matters -- see
    /// <see cref="DeskSnapshot"/> for why that line is drawn so firmly. Three folders, three
    /// passes, no judgement.
    ///
    /// <b>One restricted query, then a bounded scan.</b> <c>Items.Restrict</c> on
    /// <c>[UnRead] = True</c> runs inside Outlook, which is the only way this stays fast on a
    /// real mailbox; the scan over the result is capped because the classification is the
    /// expensive half and the recent end is the part that matters.
    /// </summary>
    /// <remarks>
    /// <b>The objects come back with the counts, out of the same walk.</b> The cache needs a
    /// row per thing and the page needs a number per kind, and both come from reading the
    /// same items -- so they are read once. Indexing in a second pass would double the COM
    /// traffic on the one operation that runs on a timer, and on a mailbox of four thousand
    /// messages that is the difference between a background task and a noticeable one.
    /// </remarks>
    public Task<DeskReading> TakeSnapshotAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;

            var seen = new List<DeskObject>();

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                session = outlook.Session;

                // Declared with their types rather than deconstructed. Both calls take a
                // dynamic argument, so the compiler treats the whole call as dynamic and a
                // dynamic result cannot be deconstructed -- it can only be converted into a
                // variable whose type is written out.
                (int Unread, int People, int Automated, int Requests, int Tickets, int Scanned) mail =
                    CountUnread(session, now, seen, cancellationToken);

                (int Left, DateTime? Next) day = CountToday(session, now, seen, cancellationToken);

                int overdue = CountOverdue(session, now, seen, cancellationToken);

                return new DeskReading(
                    new DeskSnapshot(
                        Unread: mail.Unread,
                        FromPeople: mail.People,
                        Automated: mail.Automated,
                        MeetingRequests: mail.Requests,
                        TicketMail: mail.Tickets,
                        AppointmentsToday: day.Left,
                        NextAppointment: day.Next,
                        OverdueTasks: overdue,
                        Scanned: mail.Scanned,
                        TakenAt: now),
                    seen);
            }
            finally
            {
                Com.ReleaseAll(outlook, session);
            }
        }, cancellationToken);
    }

    /// <summary>Unread mail, and what kind of sender it came from.</summary>
    private static (int Unread, int People, int Automated, int Requests, int Tickets, int Scanned) CountUnread(
        dynamic session,
        DateTime now,
        List<DeskObject> seen,
        CancellationToken cancellationToken)
    {
        dynamic? folder = null;
        dynamic? items = null;
        dynamic? unreadItems = null;

        try
        {
            folder = session.GetDefaultFolder(FolderInbox);

            // The folder's own tally: complete, instant, and independent of the scan below.
            // UnReadItemCount counts the folder itself rather than a query over it, so the
            // total is right even when the classification only saw the recent end.
            int unread = Num(() => folder.UnReadItemCount);

            items = folder.Items;
            unreadItems = items.Restrict("[UnRead] = True");
            unreadItems.Sort("[ReceivedTime]", true);

            int people = 0;
            int automated = 0;
            int requests = 0;
            int tickets = 0;
            int scanned = 0;

            // Enumerated rather than indexed. Indexing a restricted collection is what
            // produced twenty appointments with no subject and the year 1: the collection
            // answers an index with something, and it is not always the item you asked for.
            foreach (dynamic item in unreadItems)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (scanned >= DeskScan)
                        break;

                    scanned++;

                    string subject = Str(() => item.Subject);
                    string address = Str(() => item.SenderEmailAddress);
                    string name = Str(() => item.SenderName);
                    DateTime received = Date(() => item.ReceivedTime);

                    // The class, not the subject. "Besprechungsanfrage:" is a localised
                    // prefix; IPM.Schedule.Meeting.Request is not.
                    bool request = Str(() => item.MessageClass)
                        .StartsWith("IPM.Schedule.Meeting.Request", StringComparison.OrdinalIgnoreCase);

                    bool automatic = !request && TicketKeys.LooksAutomated(address, name);

                    // A ticket key in the subject AND an automated sender. The key alone is
                    // not enough: a colleague writing "wegen IMIT-1234" is a person with a
                    // question, not a notification, and this project already made that
                    // mistake once in the other direction.
                    string? key = automatic ? TicketKeys.Primary(subject, string.Empty) : null;

                    if (request)
                        requests++;
                    else if (automatic)
                    {
                        automated++;

                        if (key is not null)
                            tickets++;
                    }
                    else
                        people++;

                    seen.Add(new DeskObject(
                        Id: DeskObject.MakeId(DeskKind.Mail, MessageKey(item, subject, address, received)),
                        Kind: DeskKind.Mail,
                        Subject: subject,
                        WhoName: name,
                        WhoAddress: address,
                        When: received,
                        Due: null,
                        State: request ? "meeting request" : "unread",
                        TicketKey: key,
                        Thread: Str(() => item.ConversationID) is { Length: > 0 } thread ? thread : null,
                        EntryId: Str(() => item.EntryID),
                        Facts: null,
                        Enrichment: null,
                        FirstSeen: now,
                        LastSeen: now));
                }
                finally
                {
                    Com.Release(item);
                }
            }

            return (unread, people, automated, requests, tickets, scanned);
        }
        finally
        {
            Com.ReleaseAll(folder, items, unreadItems);
        }
    }

    /// <summary>
    /// A message's own identifier, or a made-up one that behaves like it.
    ///
    /// <b>The fallback matters more than it looks.</b> Not everything in an inbox has an
    /// internet message id: a meeting request, a delivery report and anything created
    /// locally may have none. Keying those on the entry id would be keying on a value that
    /// changes the moment the item is filed, so instead they get a hash of the three things
    /// that do not change -- when it arrived, who from, and what it was called. Two
    /// different messages colliding on all three is a mail sent twice in the same second by
    /// the same person with the same subject, which is one thing as far as a desk cares.
    /// </summary>
    private static string MessageKey(dynamic item, string subject, string address, DateTime received)
    {
        string own = Str(() =>
        {
            dynamic? accessor = null;

            try
            {
                accessor = item.PropertyAccessor;
                return accessor?.GetProperty(MessageIdProperty);
            }
            finally
            {
                Com.Release(accessor);
            }
        });

        if (own.Length > 0)
            return own;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{received:O}|{address}|{subject}")));

        return "shellvis-" + Convert.ToHexStringLower(hash)[..24];
    }

    /// <summary>What is still to come today, and when the next one starts.</summary>
    private static (int Left, DateTime? Next) CountToday(
        dynamic session,
        DateTime now,
        List<DeskObject> seen,
        CancellationToken cancellationToken)
    {
        dynamic? folder = null;
        dynamic? items = null;
        dynamic? restricted = null;

        try
        {
            folder = session.GetDefaultFolder(FolderCalendar);
            items = folder.Items;

            // Sort FIRST, then IncludeRecurrences. The other order silently returns
            // unexpanded masters -- measured, and it looked exactly like an empty day.
            items.Sort("[Start]");
            items.IncludeRecurrences = true;

            // CurrentCulture on purpose. Outlook's bracket syntax reads the date in the
            // user's own short format, and an invariant string is read as a different day
            // on this machine: 02.09. became 9 February, silently, in two tools.
            string from = now.ToString("g", CultureInfo.CurrentCulture);
            string to = now.Date.AddDays(1).ToString("g", CultureInfo.CurrentCulture);

            restricted = items.Restrict($"[Start] >= \"{from}\" AND [Start] < \"{to}\"");

            int left = 0;
            DateTime? next = null;
            int guard = 0;

            foreach (dynamic item in restricted)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (guard++ > 200)
                        break;

                    DateTime start = Date(() => item.Start);

                    if (start == DateTime.MinValue)
                        continue;

                    left++;
                    next ??= start;

                    // GlobalAppointmentID, not EntryID: one occurrence of a series shares it
                    // with the series, which is what makes "the Monday meeting" one thing to
                    // remember rather than fifty-two.
                    string global = Str(() => item.GlobalAppointmentID);
                    string entry = Str(() => item.EntryID);

                    seen.Add(new DeskObject(
                        Id: DeskObject.MakeId(
                            DeskKind.Appointment,
                            global.Length > 0 ? global : entry),
                        Kind: DeskKind.Appointment,
                        Subject: Str(() => item.Subject),
                        WhoName: Str(() => item.Organizer),
                        WhoAddress: string.Empty,
                        When: start,
                        Due: null,
                        State: Str(() => item.Location),
                        TicketKey: null,
                        Thread: null,
                        EntryId: entry,
                        Facts: null,
                        Enrichment: null,
                        FirstSeen: now,
                        LastSeen: now));
                }
                finally
                {
                    Com.Release(item);
                }
            }

            return (left, next);
        }
        finally
        {
            Com.ReleaseAll(folder, items, restricted);
        }
    }

    /// <summary>Tasks whose due date has passed and which are not finished.</summary>
    private static int CountOverdue(
        dynamic session,
        DateTime now,
        List<DeskObject> seen,
        CancellationToken cancellationToken)
    {
        dynamic? folder = null;
        dynamic? items = null;

        try
        {
            folder = session.GetDefaultFolder(FolderTasks);
            items = folder.Items;
            items.Sort("[DueDate]");

            int overdue = 0;
            int guard = 0;

            foreach (dynamic item in items)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (guard++ > 2000)
                        break;

                    if (Flag(() => item.Complete))
                        continue;

                    DateTime due = Date(() => item.DueDate);

                    // A task with no due date is not late, and Outlook writes "no date" as
                    // 4501-01-01 rather than as null. Comparing without that check makes
                    // every undated task overdue in the year 4501, which is not a thing to
                    // put a badge on.
                    if (due == DateTime.MinValue || due.Date == NoDate.Date)
                        continue;

                    bool late = due.Date < now.Date;

                    if (late)
                        overdue++;

                    // Only the dated, open ones are remembered. An undated task is a
                    // someday-item and filling the cache with hundreds of them would push
                    // the things that have a date out of view in every listing.
                    seen.Add(new DeskObject(
                        Id: DeskObject.MakeId(DeskKind.Task, Str(() => item.EntryID)),
                        Kind: DeskKind.Task,
                        Subject: Str(() => item.Subject),
                        WhoName: string.Empty,
                        WhoAddress: string.Empty,
                        When: Date(() => item.CreationTime) is { } made && made != DateTime.MinValue
                            ? made
                            : due,
                        Due: due,
                        State: late ? "overdue" : "open",
                        TicketKey: TicketKeys.Primary(Str(() => item.Subject), string.Empty),
                        Thread: null,
                        EntryId: Str(() => item.EntryID),
                        Facts: null,
                        Enrichment: null,
                        FirstSeen: now,
                        LastSeen: now));
                }
                finally
                {
                    Com.Release(item);
                }
            }

            return overdue;
        }
        finally
        {
            Com.ReleaseAll(folder, items);
        }
    }
}
