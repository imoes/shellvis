using System.Globalization;
using System.Text;

namespace Shellvis.Core.Office;

/// <summary>A mail item, flattened to what an agent needs to decide what to do.</summary>
/// <param name="ConversationId">
/// Outlook own thread key. It is what makes "what is this about between us" answerable at
/// all: subjects get edited, prefixes accumulate, and matching on them finds the wrong
/// thread as often as the right one.
/// </param>
/// <param name="SenderAddress">
/// The address, which is the stable identity. Display names repeat across an organisation
/// and change when someone marries; an address does neither.
/// </param>
public sealed record MailSummary(
    string EntryId,
    string Subject,
    string From,
    DateTime Received,
    bool IsUnread,
    bool HasAttachments,
    string Preview,
    string ConversationId = "",
    string SenderAddress = "")
{
    public override string ToString()
    {
        string flag = IsUnread ? "UNREAD " : string.Empty;
        string clip = HasAttachments ? " [attachment]" : string.Empty;
        return $"{flag}{Received:yyyy-MM-dd HH:mm}  {From}  \"{Subject}\"{clip}";
    }
}

/// <summary>A calendar entry.</summary>
/// <param name="EntryId">
/// Outlook's id for this entry, so a later call can act on it. Empty for an occurrence of a
/// recurring series that Outlook expanded rather than stored.
/// </param>
/// <param name="JoinUrl">
/// The Teams join link, when the body carries one. Detected on the way out rather than left
/// for a caller to find: the body is tens of thousands of characters and would sit in the
/// context of every later round, while the link is the one line anybody wants from it.
/// </param>
public sealed record AppointmentSummary(
    string Subject,
    DateTime Start,
    DateTime End,
    string Location,
    bool IsAllDay,
    string EntryId = "",
    string JoinUrl = "")
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

    private string Where()
    {
        string place = Location.Length > 0 ? $"  @ {Location}" : string.Empty;

        // Said out loud, because "is this one online?" is the question a calendar line has
        // to answer before anyone can decide whether to walk anywhere.
        return JoinUrl.Length > 0 ? place + "  [Teams]" : place;
    }
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
public sealed partial class OutlookClient(ComApartment apartment)
{
    /// <summary>Outlook folder constants, so the numbers are not scattered as magic values.</summary>
    private const int FolderInbox = 6;
    private const int FolderCalendar = 9;
    private const int FolderContacts = 10;
    private const int FolderSentMail = 5;
    private const int FolderDrafts = 16;

    /// <summary>Whether Outlook is installed and automatable.</summary>
    public static bool IsAvailable => Com.IsAvailable("Outlook.Application");

    /// <summary>
    /// Whether answering a request had to launch Outlook, because it was not running.
    ///
    /// Surfaced so the tool result can say it. COM activation starts Outlook on demand, which
    /// is convenient and is also a visible act on the user's machine: a profile opens, mail
    /// begins synchronising, reminders appear. Doing that in answer to "what is on today" is
    /// defensible; doing it without a word is not.
    ///
    /// Latched rather than per-call: once it has been started it stays started, and repeating
    /// the notice on every subsequent call would be noise.
    /// </summary>
    public bool WasStarted { get; private set; }

    /// <summary>Whether Outlook is running right now, without starting it to find out.</summary>
    public static bool IsRunning
    {
        get
        {
            try
            {
                return Com.TryGetActive("Outlook.Application") is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// List messages from a folder, newest first, saying what the page left out.
    ///
    /// <b>Returns a page rather than a list, and that is the fix.</b> This method used to
    /// read <c>items.Count</c> into a local and then throw it away, so a caller was handed
    /// twenty messages with no way to learn that three thousand matched. Asked what happened
    /// last week, the model summarised the newest twenty and called it the week. See
    /// <see cref="MailPage"/>.
    ///
    /// <b>The window is applied in Outlook, not here.</b> A folder can hold tens of
    /// thousands of items; fetching them to filter in C# takes minutes, which is the same
    /// reason the sort happens there.
    /// </summary>
    /// <param name="since">Inclusive lower bound on the received time.</param>
    /// <param name="until">Exclusive upper bound, so a day range does not swallow the next day.</param>
    public Task<MailPage> ListMailAsync(
        string folder = "inbox",
        int limit = 20,
        bool unreadOnly = false,
        DateTime? since = null,
        DateTime? until = null,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync<MailPage>(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? mapiFolder = null;
            dynamic? items = null;

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;
                session = outlook.Session;
                mapiFolder = session.GetDefaultFolder(FolderId(folder));
                items = mapiFolder.Items;

                // How much is here BEFORE any filter, so that "nothing in this window" can
                // be told apart from "nothing here". An empty result that cannot say which
                // of the two it is reads as an answer, and this project has already had a
                // model answer from one.
                int inFolder = items.Count;

                string filter = ListFilter(unreadOnly, since, until);

                if (filter.Length > 0)
                    items = items.Restrict(filter);

                // Sorted AFTER restricting, because Restrict returns a new collection and
                // the order of the old one does not necessarily come with it. The previous
                // order -- sort, then restrict -- happened to look right for the unread
                // filter and was never anything but luck.
                //
                // In Outlook rather than in C# either way: the folder may hold tens of
                // thousands of items, and enumerating all of them to sort locally would
                // take minutes.
                items.Sort("[ReceivedTime]", true);

                var results = new List<MailSummary>();
                int matching = items.Count;
                int take = Math.Min(limit, matching);

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

                // The span the page actually covers, taken from what was returned rather
                // than from what was asked for. Asked for a week and given three days, the
                // caller can now see the three days.
                return new MailPage(
                    results,
                    matching,
                    inFolder,
                    results.Count > 0 ? results[0].Received : null,
                    results.Count > 0 ? results[^1].Received : null);
            }
            finally
            {
                Com.ReleaseAll(outlook, session, mapiFolder, items);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Build the Restrict filter for a listing.
    ///
    /// <b>CURRENT culture on the dates, and this is the opposite of the rule everywhere
    /// else.</b> Outlook's bracket syntax parses a date in the USER's short-date format, not
    /// in any fixed one -- the calendar filter below carries the full account of how that was
    /// found, including that an invariant <c>09/01/2026</c> was read as 9 January on this
    /// German machine and that the bug was invisible for roughly half the days of any month
    /// because a day number above 12 makes the month field invalid and Outlook then guesses
    /// right.
    ///
    /// Note that <see cref="MailWindow.TryParse"/> does the opposite, on purpose: reading a
    /// date the model wrote prefers a fixed format, writing one for Outlook must not. The two
    /// must not be "unified".
    /// </summary>
    public static string ListFilter(bool unreadOnly, DateTime? since, DateTime? until)
    {
        var clauses = new List<string>(3);

        if (unreadOnly)
            clauses.Add("[UnRead] = True");

        if (since is { } from)
        {
            clauses.Add(string.Format(
                CultureInfo.CurrentCulture, "[ReceivedTime] >= '{0:g}'", from));
        }

        if (until is { } to)
        {
            clauses.Add(string.Format(
                CultureInfo.CurrentCulture, "[ReceivedTime] < '{0:g}'", to));
        }

        return string.Join(" AND ", clauses);
    }

    /// <summary>
    /// Put one message in front of the user, in Outlook.
    ///
    /// <b>Why this exists rather than a summary being enough.</b> An answer that says "X
    /// wrote to you about Y" is a claim the user has to take on trust unless they can get
    /// to the message itself. The link back is what makes the claim checkable, and it is
    /// also what a secretary hands over: the mail, not a retelling of it.
    ///
    /// Outlook is started if it is not running, which is the case this was asked for. The
    /// notice that it had to be started is the one already used elsewhere, so the user
    /// learns it once rather than per call.
    /// </summary>
    public Task<string> OpenMailAsync(string entryId, CancellationToken cancellationToken = default)
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

                // Modal: false, so the window belongs to the user rather than blocking the
                // apartment thread until they close it. A modal Display would hold the one
                // STA thread every Office call in this application shares.
                item.Display(false);

                return $"opened \"{subject}\" in Outlook.";
            }
            finally
            {
                Com.ReleaseAll(outlook, session, item);
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
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;
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
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;
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
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

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
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;
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
                                IsAllDay: Flag(() => appointment.AllDayEvent),
                                EntryId: Str(() => appointment.EntryID),
                                JoinUrl: Teams.TeamsLinks.JoinUrlIn(Str(() => appointment.Body)) ?? string.Empty));
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
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;
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
            Preview: body.Length <= 160 ? body.ReplaceLineEndings(" ") : body[..160].ReplaceLineEndings(" ") + "...",
            ConversationId: Str(() => item.ConversationID),

            // SMTP where Exchange will give it, and the raw address otherwise. Inside an
            // Exchange organisation SenderEmailAddress is an X500 distinguished name, which
            // is useless for matching against anything the user would recognise or type.
            SenderAddress: SmtpOf(item));
    }


    /// <summary>
    /// The sender SMTP address, whatever Outlook is willing to give.
    ///
    /// Three attempts in descending order of usefulness, because Exchange does not hand it
    /// over the obvious way. SenderEmailType says EX for an internal sender and then
    /// SenderEmailAddress is an X500 path like /O=.../CN=RECIPIENTS/CN=..., which matches
    /// nothing a user would recognise. The Exchange user object carries the real one, and
    /// PropertyAccessor is the fallback when it does not.
    /// </summary>
    private static string SmtpOf(dynamic item)
    {
        string address = Str(() => item.SenderEmailAddress);

        if (!Str(() => item.SenderEmailType).Equals("EX", StringComparison.OrdinalIgnoreCase))
            return address;

        string resolved = Str(() =>
        {
            dynamic? sender = null;
            dynamic? exchange = null;

            try
            {
                sender = item.Sender;
                exchange = sender?.GetExchangeUser();
                return exchange?.PrimarySmtpAddress;
            }
            finally
            {
                Com.ReleaseAll(sender, exchange);
            }
        });

        if (resolved.Length > 0)
            return resolved;

        // PR_SENT_REPRESENTING_SMTP_ADDRESS. Named rather than left as a bare string,
        // because a MAPI property tag is unreadable and looks like a typo.
        const string SmtpProperty =
            "http://schemas.microsoft.com/mapi/proptag/0x5D01001F";

        string viaProperty = Str(() =>
        {
            dynamic? accessor = null;

            try
            {
                accessor = item.PropertyAccessor;
                return accessor?.GetProperty(SmtpProperty);
            }
            finally
            {
                Com.Release(accessor);
            }
        });

        return viaProperty.Length > 0 ? viaProperty : address;
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
