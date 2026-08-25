using System.Globalization;
using System.Text;

namespace Shellvis.Core.Office;

/// <summary>A mail item, flattened to what an agent needs to decide what to do.</summary>
public sealed record MailSummary(
    string EntryId,
    string Subject,
    string From,
    DateTime Received,
    bool IsUnread,
    bool HasAttachments,
    string Preview)
{
    public override string ToString()
    {
        string flag = IsUnread ? "UNREAD " : string.Empty;
        string clip = HasAttachments ? " [attachment]" : string.Empty;
        return $"{flag}{Received:yyyy-MM-dd HH:mm}  {From}  \"{Subject}\"{clip}";
    }
}

/// <summary>A calendar entry.</summary>
public sealed record AppointmentSummary(
    string Subject,
    DateTime Start,
    DateTime End,
    string Location,
    bool IsAllDay)
{
    /// <summary>
    /// The weekday is spelled out, and that is not decoration.
    ///
    /// Asked for appointments "with the weekday", the model derived them from the dates and
    /// got them wrong twice -- naming 2026-08-24 a Sunday and 2026-08-28 a Thursday when
    /// they are a Monday and a Friday. Telling it today's date and the week's boundaries
    /// helped once and not the next time: date arithmetic is simply not something to rely
    /// on it for. The tool already holds a DateTime and knows the answer for nothing, so it
    /// says it. Same move as taking the skill writing out of the model's hands: what must be
    /// right belongs in code.
    ///
    /// English weekday names, invariant, like every other date this project prints. The
    /// model translates them into the user's language along with the rest of the answer.
    /// </summary>
    public override string ToString() =>
        // Explicitly invariant. A plain interpolation uses the current culture, which on
        // this machine is German -- so the weekday would arrive as "Mo" and the model would
        // be translating a translation.
        IsAllDay
            ? string.Create(CultureInfo.InvariantCulture,
                $"{Start:ddd yyyy-MM-dd}  (all day)  \"{Subject}\"{Where()}")
            : string.Create(CultureInfo.InvariantCulture,
                $"{Start:ddd yyyy-MM-dd HH:mm}-{End:HH:mm}  \"{Subject}\"{Where()}");

    private string Where() => Location.Length > 0 ? $"  @ {Location}" : string.Empty;
}

/// <summary>
/// Outlook through late-bound COM.
///
/// Late binding rather than a typed interop assembly, on purpose. Office here is 365
/// ProPlus with rolling updates (16.0.20228 at the time of writing), and a version-
/// pinned PIA breaks when the build moves. <c>dynamic</c> costs a little speed and
/// compile-time checking, and buys immunity to the version churn.
///
/// Every method runs on the shared <see cref="ComApartment"/>, and every reference is
/// released explicitly. Both are load-bearing: without the apartment the calls fail
/// unpredictably, and without the releases OUTLOOK.EXE outlives the agent.
///
/// Classic Outlook only. The "New Outlook" store app exposes no COM surface at all;
/// this machine has the classic one, which is what makes this path viable.
/// </summary>
public sealed class OutlookClient(ComApartment apartment)
{
    /// <summary>Outlook folder constants, so the numbers are not scattered as magic values.</summary>
    private const int FolderInbox = 6;
    private const int FolderCalendar = 9;
    private const int FolderContacts = 10;
    private const int FolderSentMail = 5;
    private const int FolderDrafts = 16;

    /// <summary>Whether Outlook is installed and automatable.</summary>
    public static bool IsAvailable => Com.IsAvailable("Outlook.Application");

    /// <summary>List messages from a folder, newest first.</summary>
    public Task<IReadOnlyList<MailSummary>> ListMailAsync(
        string folder = "inbox",
        int limit = 20,
        bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync<IReadOnlyList<MailSummary>>(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? mapiFolder = null;
            dynamic? items = null;

            try
            {
                outlook = Com.GetOrCreate("Outlook.Application");
                session = outlook.Session;
                mapiFolder = session.GetDefaultFolder(FolderId(folder));
                items = mapiFolder.Items;

                // Sorting in Outlook rather than in C# matters: the folder may hold
                // tens of thousands of items and enumerating all of them to sort
                // locally would take minutes.
                items.Sort("[ReceivedTime]", true);

                if (unreadOnly)
                    items = items.Restrict("[UnRead] = True");

                var results = new List<MailSummary>();
                int count = items.Count;
                int take = Math.Min(limit, count);

                for (int i = 1; i <= take; i++)
                {
                    dynamic? item = null;
                    try
                    {
                        item = items[i];
                        results.Add(ReadMail(item));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A single unreadable item (a meeting response, a corrupt
                        // message, a report) must not abort the whole listing.
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
                Com.ReleaseAll(outlook, session, mapiFolder, items);
            }
        }, cancellationToken);
    }

    /// <summary>Full text of one message, addressed by its Outlook entry id.</summary>
    public Task<string> ReadMailAsync(string entryId, CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? item = null;

            try
            {
                outlook = Com.GetOrCreate("Outlook.Application");
                session = outlook.Session;
                item = session.GetItemFromID(entryId);

                var sb = new StringBuilder();
                sb.Append("From:    ").AppendLine(Str(() => item.SenderName));
                sb.Append("To:      ").AppendLine(Str(() => item.To));
                sb.Append("Sent:    ").AppendLine(Str(() => item.SentOn?.ToString("yyyy-MM-dd HH:mm")));
                sb.Append("Subject: ").AppendLine(Str(() => item.Subject));

                int attachments = Num(() => item.Attachments.Count);
                if (attachments > 0)
                    sb.Append("Attachments: ").Append(attachments).AppendLine();

                sb.AppendLine().AppendLine(Str(() => item.Body));

                return sb.ToString();
            }
            finally
            {
                Com.ReleaseAll(outlook, session, item);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Create a reply as a DRAFT rather than sending it.
    ///
    /// Deliberate: a wrong reply that sits in Drafts is an inconvenience, a wrong reply
    /// that has been sent cannot be recalled. Sending is a separate, explicit act.
    /// </summary>
    public Task<string> ReplyDraftAsync(
        string entryId,
        string body,
        bool replyAll = false,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? original = null;
            dynamic? reply = null;

            try
            {
                outlook = Com.GetOrCreate("Outlook.Application");
                session = outlook.Session;
                original = session.GetItemFromID(entryId);

                reply = replyAll ? original.ReplyAll() : original.Reply();

                // Prepended, so the quoted original that Outlook already put in the
                // body is preserved below the new text.
                reply.Body = body + Environment.NewLine + Environment.NewLine + reply.Body;
                reply.Save();

                return $"saved a draft reply to \"{Str(() => original.Subject)}\" "
                    + "in the Drafts folder. It has NOT been sent.";
            }
            finally
            {
                Com.ReleaseAll(outlook, session, original, reply);
            }
        }, cancellationToken);
    }

    /// <summary>Compose a new message as a draft.</summary>
    public Task<string> ComposeDraftAsync(
        string to,
        string subject,
        string body,
        string? cc = null,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? mail = null;

            try
            {
                outlook = Com.GetOrCreate("Outlook.Application");

                // 0 is olMailItem.
                mail = outlook.CreateItem(0);
                mail.To = to;
                mail.Subject = subject;
                mail.Body = body;

                if (!string.IsNullOrWhiteSpace(cc))
                    mail.CC = cc;

                mail.Save();

                return $"saved a draft to {to} with subject \"{subject}\". It has NOT been sent.";
            }
            finally
            {
                Com.ReleaseAll(outlook, mail);
            }
        }, cancellationToken);
    }

    /// <summary>Appointments overlapping a date range.</summary>
    public Task<IReadOnlyList<AppointmentSummary>> ListAppointmentsAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync<IReadOnlyList<AppointmentSummary>>(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? calendar = null;
            dynamic? items = null;

            try
            {
                outlook = Com.GetOrCreate("Outlook.Application");
                session = outlook.Session;
                calendar = session.GetDefaultFolder(FolderCalendar);
                items = calendar.Items;

                // IncludeRecurrences only works when the collection is sorted by
                // Start; without both, recurring meetings simply do not appear, which
                // silently loses most of a working calendar.
                items.Sort("[Start]");
                items.IncludeRecurrences = true;

                // CURRENT culture, not invariant, and this is the opposite of the rule
                // everywhere else in this project.
                //
                // Outlook's Restrict parses the date in the USER's short-date format, not
                // in any fixed one. Written invariant it came out as MM/dd/yyyy and Outlook
                // read it as dd.MM.yyyy on this German machine -- so '09/01/2026' became
                // 9 January and the filter matched nothing. It only appeared to work at
                // all because a day number above 12 makes the month field invalid and
                // Outlook then falls back to the right reading: a query for the 24th to the
                // 31st returned six appointments while the same week asked as the 25th to
                // the 1st returned none. Roughly half the days of any month were affected,
                // and the symptom was an empty calendar rather than an error.
                //
                // Verified against the running Outlook both ways before changing it.
                string filter = string.Format(
                    CultureInfo.CurrentCulture,
                    "[Start] < '{0:g}' AND [End] > '{1:g}'",
                    to, from);

                dynamic restricted = items.Restrict(filter);

                var results = new List<AppointmentSummary>();

                try
                {
                    // Recurrence expansion can produce an unbounded series, so the
                    // loop is capped rather than trusting the filter alone.
                    int guard = 0;
                    foreach (dynamic appointment in restricted)
                    {
                        try
                        {
                            if (guard++ > 500)
                                break;

                            results.Add(new AppointmentSummary(
                                Subject: Str(() => appointment.Subject),
                                Start: Date(() => appointment.Start),
                                End: Date(() => appointment.End),
                                Location: Str(() => appointment.Location),
                                IsAllDay: Flag(() => appointment.AllDayEvent)));
                        }
                        finally
                        {
                            Com.Release(appointment);
                        }
                    }
                }
                finally
                {
                    Com.Release(restricted);
                }

                return results.OrderBy(a => a.Start).ToList();
            }
            finally
            {
                Com.ReleaseAll(outlook, session, calendar, items);
            }
        }, cancellationToken);
    }

    /// <summary>Search contacts by name or address.</summary>
    public Task<string> FindContactsAsync(
        string query, int limit = 15, CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? contacts = null;
            dynamic? items = null;

            try
            {
                outlook = Com.GetOrCreate("Outlook.Application");
                session = outlook.Session;
                contacts = session.GetDefaultFolder(FolderContacts);
                items = contacts.Items;

                var sb = new StringBuilder();
                int found = 0;

                foreach (dynamic contact in items)
                {
                    try
                    {
                        if (found >= limit)
                            break;

                        string name = Str(() => contact.FullName);
                        string email = Str(() => contact.Email1Address);
                        string company = Str(() => contact.CompanyName);

                        if (!Matches(query, name, email, company))
                            continue;

                        found++;
                        sb.Append("  ").Append(name);

                        if (email.Length > 0)
                            sb.Append("  <").Append(email).Append('>');

                        if (company.Length > 0)
                            sb.Append("  ").Append(company);

                        sb.AppendLine();
                    }
                    finally
                    {
                        Com.Release(contact);
                    }
                }

                return found == 0
                    ? $"no contact matches '{query}'."
                    : $"{found} contact(s) matching '{query}':\n{sb}";
            }
            finally
            {
                Com.ReleaseAll(outlook, session, contacts, items);
            }
        }, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    private static bool Matches(string query, params string[] fields) =>
        fields.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static MailSummary ReadMail(dynamic item)
    {
        string body = Str(() => item.Body);

        return new MailSummary(
            EntryId: Str(() => item.EntryID),
            Subject: Str(() => item.Subject),
            From: Str(() => item.SenderName),
            Received: Date(() => item.ReceivedTime),
            IsUnread: Flag(() => item.UnRead),
            HasAttachments: Num(() => item.Attachments.Count) > 0,
            Preview: body.Length <= 160 ? body.ReplaceLineEndings(" ") : body[..160].ReplaceLineEndings(" ") + "...");
    }

    private static int FolderId(string name) => name.Trim().ToLowerInvariant() switch
    {
        "inbox" => FolderInbox,
        "sent" or "sentmail" or "sent items" => FolderSentMail,
        "drafts" => FolderDrafts,
        "calendar" => FolderCalendar,
        "contacts" => FolderContacts,
        _ => FolderInbox,
    };

    // Late-bound property reads throw for items that do not have the property at all
    // -- a meeting request has no SenderName, a report has no Body. Each read degrades
    // to a default so one odd item cannot abort a listing.

    private static string Str(Func<object?> read)
    {
        try
        {
            return read()?.ToString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    private static DateTime Date(Func<object?> read)
    {
        try
        {
            return read() is DateTime value ? value : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DateTime.MinValue;
        }
    }

    private static bool Flag(Func<object?> read)
    {
        try
        {
            return read() is bool value && value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static int Num(Func<object?> read)
    {
        try
        {
            return read() is int value ? value : 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0;
        }
    }
}
