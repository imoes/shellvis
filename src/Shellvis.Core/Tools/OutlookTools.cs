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
public sealed class OutlookTools(ComApartment apartment)
{
    private readonly OutlookClient _outlook = new(apartment);

    [ShellvisTool(
        "mail_list",
        SideEffect.ReadOnly,
        Description =
            "List messages from an Outlook folder, newest first. Folders: inbox, sent, "
            + "drafts. Set unreadOnly to see only what has not been read. Each entry "
            + "carries an id you can pass to mail_read and mail_reply_draft.",
        PreviewParameter = "folder",
        Glyph = "mail")]
    public async Task<string> ListMail(
        string folder = "inbox",
        int limit = 20,
        bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        try
        {
            IReadOnlyList<MailSummary> mail = await _outlook
                .ListMailAsync(folder, Math.Clamp(limit, 1, 100), unreadOnly, cancellationToken)
                .ConfigureAwait(false);

            if (mail.Count == 0)
                return unreadOnly ? $"no unread messages in {folder}." : $"no messages in {folder}.";

            var sb = new StringBuilder();
            sb.Append(mail.Count).Append(" message(s) in ").Append(folder).AppendLine(":");

            foreach (MailSummary message in mail)
            {
                sb.Append("  ").AppendLine(message.ToString());
                sb.Append("      id: ").AppendLine(message.EntryId);

                if (message.Preview.Length > 0)
                    sb.Append("      ").AppendLine(message.Preview);
            }

            return sb.ToString();
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
            "Write a reply to an Outlook message and save it as a DRAFT. It is never "
            + "sent; the user reviews and sends it. Set replyAll to include every "
            + "recipient of the original.",
        PreviewParameter = "messageId",
        Glyph = "mail")]
    public async Task<string> ReplyDraft(
        string messageId,
        string body,
        bool replyAll = false,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(body))
            return "error: a message id and a reply body are required.";

        try
        {
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

        DateTime start = ParseDate(from) ?? DateTime.Today;
        DateTime end = ParseDate(to) ?? start.AddDays(7);

        if (end < start)
            (start, end) = (end, start);

        try
        {
            IReadOnlyList<AppointmentSummary> appointments = await _outlook
                .ListAppointmentsAsync(start, end, cancellationToken)
                .ConfigureAwait(false);

            if (appointments.Count == 0)
                return $"no appointments between {start:yyyy-MM-dd} and {end:yyyy-MM-dd}.";

            var sb = new StringBuilder();
            sb.Append(appointments.Count).Append(" appointment(s) from ")
              .Append(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(" to ").Append(end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .AppendLine(":");

            foreach (AppointmentSummary appointment in appointments)
                sb.Append("  ").AppendLine(appointment.ToString());

            return sb.ToString();
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

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Invariant first so an ISO date from a model is read as written, then the
        // local culture so a user-supplied German date also works.
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            || DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsed)
                ? parsed
                : null;
    }
}
