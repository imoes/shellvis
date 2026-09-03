using System.Globalization;
using System.Text;
using Shellvis.Core.Office;

namespace Shellvis.Core.Tools;

/// <summary>
/// Outlook mail, calendar and contacts.
///
/// One rule shapes the whole surface: **nothing is ever sent.** Replies and new
/// messages are saved as drafts, and the tool result says so plainly. A wrong draft in
/// the Drafts folder is an inconvenience; a wrong message that has left the building
/// cannot be recalled, and an agent working from a summarised understanding of a thread
/// will occasionally be wrong. Sending stays a human act.
///
/// Reading is classified read-only and therefore runs without prompting. Writing a
/// draft is mutating and prompts, because it puts something into the user's mailbox
/// even if it does not transmit it.
/// </summary>
public sealed partial class OutlookTools(
    ComApartment apartment,
    Shellvis.Core.Notes.NoteStore? notes = null)
{
    private readonly OutlookClient _outlook = new(apartment);

    private bool _announcedStart;

    /// <summary>
    /// Say that Outlook had to be launched, once.
    ///
    /// COM activation starts Outlook on demand, so a question about the calendar answers
    /// correctly whether or not Outlook was running. What it also does, when it was not, is
    /// open the user's mail client: a profile loads, mail begins synchronising, reminders pop
    /// up. That is a fair price for an answer and an unfair thing to do without a word, and it
    /// went unsaid until a harness run failed for exactly this reason and the cause had to be
    /// explained from outside.
    ///
    /// Once per session, not per call: the second notice would be noise, and the launch only
    /// happens once anyway.
    /// </summary>
    private string StartNotice()
    {
        if (!_outlook.WasStarted || _announcedStart)
            return string.Empty;

        _announcedStart = true;

        return Environment.NewLine
            + "(Outlook was not running, so it was started to answer this.)";
    }

    [ShellvisTool(
        "mail_list",
        SideEffect.ReadOnly,
        Description =
            "List messages from an Outlook folder, newest first. Folders: inbox, sent, "
            + "drafts. Set unreadOnly to see only what has not been read. Each entry "
            + "carries an id you can pass to mail_read and mail_reply_draft. "
            + "For a question about a PERIOD -- last week, the last few days, since Monday "
            + "-- give since (and optionally until) rather than raising the limit: 7d, 36h, "
            + "2w, today, yesterday, or a date like 2026-08-25. Without a window you get the "
            + "newest messages, which in a busy folder may cover two days rather than the "
            + "week you were asked about. Read the header line: it says how many matched and "
            + "which span the listing actually covers, and it says so when there are older "
            + "ones you have not seen.",
        PreviewParameter = "folder",
        Glyph = "mail")]
    public async Task<string> ListMail(
        string folder = "inbox",
        int limit = 20,
        bool unreadOnly = false,
        string? since = null,
        string? until = null,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        DateTime now = DateTime.Now;

        if (!Bound(since, now, out DateTime? from, out string? sinceProblem))
            return $"error: since {sinceProblem}";

        if (!Bound(until, now, out DateTime? to, out string? untilProblem))
            return $"error: until {untilProblem}";

        if (from is { } a && to is { } b && a >= b)
            return $"error: since ({a:yyyy-MM-dd HH:mm}) is not before until ({b:yyyy-MM-dd HH:mm}).";

        try
        {
            MailPage page = await _outlook
                .ListMailAsync(folder, Math.Clamp(limit, 1, 100), unreadOnly, from, to, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<MailSummary> mail = page.Messages;

            if (mail.Count == 0)
                return Nothing(folder, unreadOnly, from, to, page.InFolder) + StartNotice();

            var sb = new StringBuilder();
            sb.AppendLine(Header(folder, page, unreadOnly, from, to));

            foreach (MailSummary message in mail)
            {
                sb.Append("  ").AppendLine(message.ToString());
                sb.Append("      id: ").AppendLine(message.EntryId);

                if (message.Preview.Length > 0)
                    sb.Append("      ").AppendLine(message.Preview);
            }

            // The truncation says so. A list that stops silently looks complete, and a model
            // that believes it has seen everything answers as though it had -- which is
            // exactly how "the highlights of last week" became the highlights of Tuesday.
            if (page.Withheld > 0)
            {
                sb.Append("  ... ").Append(page.Withheld).Append(" older not shown; ").AppendLine(
                    from is null
                        ? "give since (7d, 2w, a date) to cover a period, or raise the limit."
                        : "raise the limit to see the rest of this period.");
            }

            return sb.ToString() + NotesAbout(PeopleIn(mail)) + StartNotice();
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "mail_search",
        SideEffect.ReadOnly,
        Description =
            "Find messages by what they SAY, across the inbox, its subfolders and sent mail. "
            + "Use this when the question is about a subject rather than about a period -- "
            + "\"what did I promise about the FTP access\", \"the mail with the licence key\" "
            + "-- because mail_list only ever returns the newest and cannot reach back. Give "
            + "two or three distinctive words; they are all required, and single letters are "
            + "ignored. Optionally narrow with since and until (7d, 2w, 2026-08-25). Each hit "
            + "carries an id for mail_read. The answer says whether the search index or a walk "
            + "of the newest messages produced it, so a result of nothing means both were "
            + "tried and there is nothing to find.",
        PreviewParameter = "query",
        Glyph = "mail")]
    public async Task<string> SearchMail(
        string query,
        int limit = 20,
        string? since = null,
        string? until = null,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        string[] words = OutlookClient.Words(query);

        if (words.Length == 0)
        {
            return "error: give at least one word of two or more letters to search for. "
                + "A search with nothing in it would match the whole mailbox.";
        }

        DateTime now = DateTime.Now;

        if (!Bound(since, now, out DateTime? from, out string? sinceProblem))
            return $"error: since {sinceProblem}";

        if (!Bound(until, now, out DateTime? to, out string? untilProblem))
            return $"error: until {untilProblem}";

        if (from is { } a && to is { } b && a >= b)
            return $"error: since ({a:yyyy-MM-dd HH:mm}) is not before until ({b:yyyy-MM-dd HH:mm}).";

        try
        {
            MailSearchResult found = await _outlook
                .SearchMailAsync(query, Math.Clamp(limit, 1, 100), from, to, cancellationToken)
                .ConfigureAwait(false);

            MailPage page = found.Page;
            string terms = string.Join(" ", words);

            if (page.Messages.Count == 0)
            {
                // Both ways of looking are named, and that is the point of the sentence. An
                // empty search result that does not say how hard it looked is indistinguishable
                // from a search that silently failed -- which is exactly what a content-index
                // query does on a store Windows Search has not indexed.
                return $"nothing found for \"{terms}\". The search index returned no match, and "
                    + $"the newest {found.Scanned} message(s) of the inbox and sent mail were "
                    + "then read and compared as well. Both looked; there is nothing to find "
                    + "with these words. Try fewer or different ones."
                    + StartNotice();
            }

            var sb = new StringBuilder();

            sb.Append(page.Messages.Count);

            if (page.Matching > page.Messages.Count)
                sb.Append(" of ").Append(page.Matching);

            sb.Append(" match(es) for \"").Append(terms).Append("\" in ")
              .Append(found.Folders).Append(" folder(s), via ")
              .Append(found.Path == MailSearchPath.Index
                  ? "the Windows Search index"
                  : $"a walk of the newest {found.Scanned} message(s), because the index had none");

            if (page.Newest is { } newest && page.Oldest is { } oldest)
            {
                sb.Append(", ").Append(newest.ToString("yyyy-MM-dd"))
                  .Append(" back to ").Append(oldest.ToString("yyyy-MM-dd"));
            }

            sb.AppendLine(":");

            foreach (MailSummary message in page.Messages)
            {
                sb.Append("  ").AppendLine(message.ToString());
                sb.Append("      id: ").AppendLine(message.EntryId);
            }

            if (page.Withheld > 0)
            {
                sb.Append("  ... ").Append(page.Withheld)
                  .AppendLine(" more match(es); add a word to narrow it or raise the limit.");
            }

            return sb.ToString() + StartNotice();
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "mail_open",
        SideEffect.Mutating,
        Description =
            "Show one Outlook message to the user, by the id from mail_list. Outlook is "
            + "started if it is not running. Use this when the user should look at the "
            + "message itself rather than at your summary of it; also mention the message "
            + "as a shellvis:mail/<id> link so they can open it from your answer.",
        PreviewParameter = "messageId",
        Glyph = "mail")]
    public async Task<string> OpenMail(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(messageId))
            return "error: a message id is required. Get one from mail_list.";

        try
        {
            // Mutating rather than read-only, and the reason is not the mailbox: it takes
            // the foreground. A window appearing over whatever the user is doing is a thing
            // they should have agreed to, even though nothing in the mailbox changes.
            string opened = await _outlook.OpenMailAsync(messageId, cancellationToken)
                .ConfigureAwait(false);

            return opened + StartNotice();
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "mail_read",
        SideEffect.ReadOnly,
        Description =
            "Read one Outlook message in full, by the id from mail_list: sender, "
            + "recipients, date, subject and the whole body.",
        PreviewParameter = "messageId",
        Glyph = "mail")]
    public async Task<string> ReadMail(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(messageId))
            return "error: a message id is required. Get one from mail_list.";

        try
        {
            return await _outlook.ReadMailAsync(messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "mail_reply_draft",
        SideEffect.Mutating,
        Description =
            "Write a reply to an Outlook message and save it as a DRAFT. It is never sent; "
            + "the user reviews and sends it, and the original message is quoted underneath "
            + "whatever you write. THREE ways to address it, and they are exclusive: by "
            + "default it goes to the sender; replyAll includes every recipient of the "
            + "original; and 'to' replaces them all with the people you name, which is the "
            + "one to use when a thread of nine concerns one of them. In 'to' a full name "
            + "works as well as an address -- Outlook's address book resolves it, and the "
            + "answer says who it resolved to, so 'reply to Kluge' is a legitimate "
            + "instruction. Separate several with a semicolon or a comma.",
        PreviewParameter = "messageId",
        Glyph = "mail")]
    public async Task<string> ReplyDraft(
        string messageId,
        string body,
        bool replyAll = false,
        string? to = null,
        string? cc = null,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(body))
            return "error: a message id and a reply body are required.";

        // Refused rather than silently preferring one. "Reply to everybody, but only to
        // Kluge" is not a thing, and picking a winner would mean the model's mistake becomes
        // a mail addressed to the wrong nine people.
        if (replyAll && !string.IsNullOrWhiteSpace(to))
        {
            return "error: replyAll and 'to' contradict each other. Use replyAll for every "
                + "recipient of the original, or 'to' for the people you name.";
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(to))
            {
                return await _outlook
                    .ReplyToAsync(messageId, body, to, cc, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _outlook
                .ReplyDraftAsync(messageId, body, replyAll, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "calendar_create",
        SideEffect.Mutating,
        Description =
            "Create an appointment in Outlook and OPEN it. With attendees it becomes a "
            + "meeting that is saved but NOT sent -- the invitation goes out when the user "
            + "presses Send, the same rule that makes a reply a draft. Give start as "
            + "'2026-09-04 14:00', or 'tomorrow 14:00' / 'today 09:30'; a date without a "
            + "time is refused rather than booked at midnight. Give either end or "
            + "durationMinutes (60 by default). In attendees a full name works as well as an "
            + "address and the answer says who it resolved to. Set teams for a Teams "
            + "meeting. The answer repeats the date WITH ITS WEEKDAY -- check it against what "
            + "was asked for before telling the user it is arranged.",
        PreviewParameter = "subject",
        Glyph = "calendar")]
    public async Task<string> CreateAppointment(
        string subject,
        string start,
        string? end = null,
        int durationMinutes = 60,
        string? body = null,
        string? attendees = null,
        bool teams = false,
        string? location = null,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(subject))
            return "error: a subject is required. An appointment nobody can identify is noise.";

        DateTime now = DateTime.Now;

        if (!MailWindow.TryParseMoment(start, now, out DateTime from, out string? problem))
            return $"error: start {problem}";

        DateTime to;

        if (!string.IsNullOrWhiteSpace(end))
        {
            if (!MailWindow.TryParseMoment(end, now, out to, out string? endProblem))
                return $"error: end {endProblem}";
        }
        else
        {
            if (durationMinutes <= 0)
                return "error: durationMinutes has to be more than zero.";

            to = from.AddMinutes(Math.Min(durationMinutes, 60 * 24));
        }

        if (to <= from)
        {
            return $"error: the end ({to:ddd yyyy-MM-dd HH:mm}) is not after the start "
                + $"({from:ddd yyyy-MM-dd HH:mm}).";
        }

        // Said rather than refused. A meeting in the past is occasionally deliberate -- a
        // record of something that already happened -- and guessing which case it is would
        // be worse than mentioning it.
        string note = from < now
            ? "  Note: this start is in the past."
            : string.Empty;

        try
        {
            string created = await _outlook
                .CreateAppointmentAsync(subject, from, to, body, attendees, teams, location, cancellationToken)
                .ConfigureAwait(false);

            return created + note;
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "mail_forward_draft",
        SideEffect.Mutating,
        Description =
            "Forward an Outlook message to somebody, with a comment of your own at the top, "
            + "and save it as a DRAFT. Never sent. The attachments, the quoted original and "
            + "the Fw: subject come with it, because Outlook's own forward is used rather "
            + "than a new message -- a forward that quietly loses the attachment is worse "
            + "than none, since the covering note says the file is enclosed. In 'to' a full "
            + "name works as well as an address; the answer says who it resolved to, and "
            + "names anything the address book did not recognise instead of leaving a draft "
            + "addressed to a typo.",
        PreviewParameter = "to",
        Glyph = "mail")]
    public async Task<string> ForwardDraft(
        string messageId,
        string to,
        string comment,
        string? cc = null,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(messageId))
            return "error: a message id is required. Get one from mail_list or mail_search.";

        if (string.IsNullOrWhiteSpace(to))
            return "error: somebody to forward it to is required, as a name or an address.";

        try
        {
            return await _outlook
                .ForwardDraftAsync(messageId, to, comment ?? string.Empty, cc, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "mail_compose_draft",
        SideEffect.Mutating,
        Description =
            "Compose a new Outlook message and save it as a DRAFT. It is never sent.",
        PreviewParameter = "subject",
        Glyph = "mail")]
    public async Task<string> ComposeDraft(
        string to,
        string subject,
        string body,
        string? cc = null,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(to) || string.IsNullOrWhiteSpace(subject))
            return "error: a recipient and a subject are required.";

        try
        {
            return await _outlook
                .ComposeDraftAsync(to, subject, body, cc, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "calendar_list",
        SideEffect.ReadOnly,
        Description =
            "List Outlook appointments in a date range, including recurring ones. "
            + "Dates are yyyy-MM-dd; omit them for the next seven days.",
        PreviewParameter = "from",
        Glyph = "calendar")]
    public async Task<string> ListAppointments(
        string? from = null,
        string? to = null,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        (DateTime start, DateTime lastDay, DateTime endExclusive) = ResolveRange(from, to);

        try
        {
            IReadOnlyList<AppointmentSummary> appointments = await _outlook
                .ListAppointmentsAsync(start, endExclusive, cancellationToken)
                .ConfigureAwait(false);

            if (appointments.Count == 0)
                return $"no appointments between {start:yyyy-MM-dd} and {lastDay:yyyy-MM-dd}." + StartNotice();

            var sb = new StringBuilder();
            sb.Append(appointments.Count).Append(" appointment(s) from ")
              .Append(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(" to ").Append(lastDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .AppendLine(":");

            foreach (AppointmentSummary appointment in appointments)
                sb.Append("  ").AppendLine(appointment.ToString());

            return sb.ToString() + NotesDueBy(lastDay) + StartNotice();
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "contacts_find",
        SideEffect.ReadOnly,
        Description =
            "Search Outlook contacts by name, address or company. Use it to resolve a "
            + "name into an email address before composing a draft.",
        PreviewParameter = "query",
        Glyph = "person")]
    public async Task<string> FindContacts(
        string query,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(query))
            return "error: a search term is required.";

        try
        {
            return await _outlook
                .FindContactsAsync(query, Math.Clamp(limit, 1, 100), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    private const string Unavailable =
        "error: Outlook is not available for automation on this machine. The classic "
        + "desktop Outlook is required; the New Outlook store app exposes no COM interface.";

    /// <summary>Read an optional bound, distinguishing "not given" from "not a date".</summary>
    private static bool Bound(string? text, DateTime now, out DateTime? value, out string? problem)
    {
        value = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!MailWindow.TryParse(text, now, out DateTime parsed, out problem))
            return false;

        value = parsed;
        return true;
    }

    /// <summary>
    /// The line above the listing: how many matched, and what this page covers.
    ///
    /// <b>The span is the load-bearing part.</b> The count alone would still let a model
    /// summarise three days and call them a week; the two timestamps are what make that
    /// visible without anyone having to add up the entries.
    /// </summary>
    private static string Header(
        string folder,
        MailPage page,
        bool unreadOnly,
        DateTime? since,
        DateTime? until)
    {
        var sb = new StringBuilder();

        sb.Append(page.Messages.Count);

        if (page.Matching > page.Messages.Count)
            sb.Append(" of ").Append(page.Matching);

        sb.Append(unreadOnly ? " unread message(s) in " : " message(s) in ").Append(folder);

        if (since is not null || until is not null)
            sb.Append(", asked for ").Append(Window(since, until));

        if (page.Newest is { } newest && page.Oldest is { } oldest)
        {
            sb.Append(", covering ").Append(newest.ToString("yyyy-MM-dd HH:mm"));

            if (oldest.Date != newest.Date || oldest != newest)
                sb.Append(" back to ").Append(oldest.ToString("yyyy-MM-dd HH:mm"));
        }

        return sb.Append(':').ToString();
    }

    /// <summary>
    /// Nothing found, said in a way that cannot be mistaken for nothing existing.
    ///
    /// The folder total is the whole point of this sentence. "No messages in inbox" over a
    /// mailbox holding three thousand is how a filter that matched nothing reads as an
    /// answer -- the failure this project already met in the calendar, where a wrongly
    /// formatted date returned an empty week that looked exactly like a free one.
    /// </summary>
    private static string Nothing(
        string folder,
        bool unreadOnly,
        DateTime? since,
        DateTime? until,
        int inFolder)
    {
        var sb = new StringBuilder("no ");

        if (unreadOnly)
            sb.Append("unread ");

        sb.Append("messages in ").Append(folder);

        if (since is not null || until is not null)
            sb.Append(' ').Append(Window(since, until));

        sb.Append('.');

        if (inFolder > 0 && (unreadOnly || since is not null || until is not null))
        {
            sb.Append(" The folder itself holds ").Append(inFolder)
              .Append(", so this is the filter and not an empty folder.");
        }

        return sb.ToString();
    }

    private static string Window(DateTime? since, DateTime? until) => (since, until) switch
    {
        ({ } from, { } to) => $"{from:yyyy-MM-dd HH:mm} to {to:yyyy-MM-dd HH:mm}",
        ({ } from, null) => $"since {from:yyyy-MM-dd HH:mm}",
        (null, { } to) => $"before {to:yyyy-MM-dd HH:mm}",
        _ => string.Empty,
    };

    /// <summary>
    /// Turn a COM failure into something the model can act on.
    ///
    /// The two cases worth naming are a closed Outlook, and the security prompt Outlook
    /// raises for programmatic access to addresses. Both look like opaque COM errors
    /// otherwise, and both have a clear human remedy.
    /// </summary>
    private static string Failure(Exception ex)
    {
        string message = ex.Message;

        if (message.Contains("0x80080005", StringComparison.OrdinalIgnoreCase)
            || message.Contains("RPC", StringComparison.OrdinalIgnoreCase))
        {
            return "error: Outlook could not be reached. It may be starting up, or "
                + "waiting on a dialog. Ask the user to bring Outlook to the front.";
        }

        if (message.Contains("0x80004004", StringComparison.OrdinalIgnoreCase)
            || message.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return "error: Outlook refused programmatic access. Its security settings "
                + "or the organisation's policy may be blocking automation.";
        }

        return $"error: Outlook automation failed: {message}";
    }

    /// <summary>
    /// Turn the requested dates into the half-open range Outlook has to be asked for.
    /// </summary>
    /// <returns>
    /// The first instant included, the last DAY included (for the wording of the answer), and
    /// the first instant excluded (for the filter).
    /// </returns>
    /// <remarks>
    /// <b>Why the end has to be pushed out by a day.</b> A date with no time means midnight,
    /// and Outlook is asked for appointments that overlap [from, to). So "today to today"
    /// resolved to a range of zero length and matched nothing -- "welche Termine liegen heute
    /// an" answered "no appointments" on a day with a meeting at 11:00, every single time.
    /// The same arithmetic silently dropped the last day of every multi-day range.
    ///
    /// A date names a whole day, so the day after it is the first instant that is genuinely
    /// outside. The reported range keeps naming the last INCLUDED day, because "no appointments
    /// between 27 and 28 August" would be a different and wrong statement about what was
    /// searched.
    ///
    /// A time given explicitly is honoured as an instant rather than widened, which is what
    /// makes "from 14:00 to 16:00" answerable at all.
    ///
    /// Public and pure so it can be tested without Outlook running: the defect was in this
    /// arithmetic, not in the COM call, and a harness that needs a live calendar with a known
    /// appointment in it cannot check arithmetic.
    /// </remarks>
    public static (DateTime Start, DateTime LastDay, DateTime EndExclusive) ResolveRange(
        string? from, string? to)
    {
        DateTime start = ParseDate(from) ?? DateTime.Today;
        bool startHasTime = HasTime(from);

        DateTime end = ParseDate(to) ?? start.Date.AddDays(6);
        bool endHasTime = HasTime(to);

        if (end < start)
        {
            (start, end) = (end, start);
            (startHasTime, endHasTime) = (endHasTime, startHasTime);
        }

        // A start given as a bare date means from the beginning of that day, which is what
        // midnight already is -- nothing to do. The end is the half that needs widening.
        DateTime endExclusive = endHasTime ? end : end.Date.AddDays(1);

        return (start, endHasTime ? end : end.Date, endExclusive);
    }

    /// <summary>
    /// Whether the caller named a time of day, rather than just a date.
    ///
    /// Decided on the text, not on the parsed value: midnight is a perfectly valid time to
    /// ask for, and a parsed DateTime cannot tell "2026-08-27" from "2026-08-27 00:00".
    /// </summary>
    private static bool HasTime(string? value) =>
        value is { Length: > 0 } && (value.Contains(':') || value.Contains('T'));

    /// <summary>
    /// One date, read the same way everywhere.
    ///
    /// <b>This used to have its own parser, and it was wrong.</b> It tried the invariant
    /// culture first "so an ISO date from a model is read as written", then the local one --
    /// and the invariant parser accepts a full stop as a date separator and reads the month
    /// first, so a German <c>02.09.2026</c> came back as 9 February. It never fails, so the
    /// local fallback was never reached, and the days above the twelfth escaped only because
    /// a month field above twelve is invalid. Half the days of any month, silently, in
    /// calendar_list.
    ///
    /// <see cref="MailWindow.TryParse"/> decides by shape instead and is now the only place
    /// that reads a date in this file. It also accepts <c>7d</c> and <c>today</c>, which a
    /// calendar range has no reason to refuse.
    /// </summary>
    private static DateTime? ParseDate(string? value) =>
        MailWindow.TryParse(value, DateTime.Now, out DateTime parsed, out _) ? parsed : null;
}
