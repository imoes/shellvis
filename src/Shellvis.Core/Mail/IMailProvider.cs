namespace Shellvis.Core.Mail;

/// <summary>
/// The mail operations both back ends can do.
///
/// Deliberately the intersection, not the union. Outlook exposes calendars, contacts and
/// tasks through COM; Thunderbird's MailExtension API covers messages and folders. Putting
/// the union behind one interface would mean half the methods throwing on one provider,
/// and a model reading a tool description cannot tell which half. So this carries what is
/// genuinely common, and the Outlook-only capabilities keep their own tools.
///
/// One rule is baked into the shape rather than left to each implementation: there is no
/// Send. Replies and new messages become DRAFTS. A wrong draft in a folder is an
/// inconvenience; a wrongly sent mail cannot be recalled, and an agent working from a
/// summarised reading of a thread will sometimes be wrong. Sending stays a human act.
/// </summary>
public interface IMailProvider
{
    /// <summary>Short name shown to the user and the model.</summary>
    string Name { get; }

    /// <summary>Whether this provider can be reached right now.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>The mail folders, with unread counts.</summary>
    Task<IReadOnlyList<MailFolder>> ListFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>Messages in a folder, newest first.</summary>
    Task<IReadOnlyList<MailMessage>> ListMessagesAsync(
        string? folder = null,
        bool unreadOnly = false,
        int limit = 20,
        CancellationToken cancellationToken = default);

    /// <summary>One message in full.</summary>
    Task<string> ReadMessageAsync(string messageId, CancellationToken cancellationToken = default);

    /// <summary>Create a reply as a draft. Never sends.</summary>
    Task<string> DraftReplyAsync(
        string messageId,
        string body,
        bool replyAll = false,
        CancellationToken cancellationToken = default);

    /// <summary>Create a new message as a draft. Never sends.</summary>
    Task<string> DraftMessageAsync(
        string to,
        string subject,
        string body,
        CancellationToken cancellationToken = default);
}

/// <summary>One mail folder.</summary>
public sealed record MailFolder(string Id, string Name, int Total, int Unread)
{
    public override string ToString() =>
        Unread > 0 ? $"{Name}  {Unread} unread of {Total}" : $"{Name}  {Total}";
}

/// <summary>
/// One message, as a listing shows it.
/// </summary>
/// <param name="Id">
/// Provider-specific identity. Opaque to the model, which passes it back verbatim -- the
/// same convention as a window handle or an element reference: never construct one,
/// only carry one.
/// </param>
public sealed record MailMessage(
    string Id,
    string Subject,
    string From,
    DateTimeOffset Received,
    bool Unread,
    string? Preview = null)
{
    /// <summary>
    /// One line per message.
    ///
    /// Sender and subject are both quoted and labelled, which is the lesson from
    /// WindowInfo applied again: an unpunctuated concatenation gets read as one field and
    /// costs a round.
    /// </summary>
    public override string ToString()
    {
        string mark = Unread ? "* " : "  ";

        return $"{mark}[{Id}] {Received.LocalDateTime:dd.MM. HH:mm}  "
            + $"from \"{From}\"  \"{Subject}\"";
    }
}
