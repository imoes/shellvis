using System.Text;
using Shellvis.Core.Mail;

namespace Shellvis.Core.Tools;

/// <summary>
/// Mail through whichever provider is present.
///
/// One tool surface for both back ends, which is what stops the model needing to know
/// which mail client this machine has. The tools are named <c>mail_*</c> rather than
/// <c>thunderbird_*</c> for the same reason: the model should ask for mail, not for a
/// product.
///
/// Outlook keeps its own <c>outlook_*</c> tools for calendar, contacts and tasks, because
/// those have no Thunderbird counterpart here and folding them in would mean half the
/// surface failing on one provider with no way for the model to tell in advance.
/// </summary>
public sealed class MailTools(IMailProvider provider)
{
    private readonly IMailProvider _provider = provider;

    [ShellvisTool(
        "mail_folders",
        SideEffect.ReadOnly,
        Description =
            "List the mail folders with their unread counts. Use it to find the folder "
            + "path that mail_messages takes.",
        Glyph = "mail")]
    public async Task<string> Folders(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MailFolder> folders = await _provider
            .ListFoldersAsync(cancellationToken)
            .ConfigureAwait(false);

        if (folders.Count == 0)
            return $"{_provider.Name} reported no folders.";

        var sb = new StringBuilder();
        sb.Append(_provider.Name).Append(": ").Append(folders.Count).AppendLine(" folder(s)");

        foreach (MailFolder folder in folders)
            sb.Append("  ").AppendLine(folder.ToString());

        return sb.ToString();
    }

    [ShellvisTool(
        "mail_messages",
        SideEffect.ReadOnly,
        Description =
            "List messages, newest first, optionally only unread ones or only from one "
            + "folder. Each line starts with the id that mail_read and mail_reply take.",
        PreviewParameter = "folder",
        Glyph = "mail")]
    public async Task<string> Messages(
        string? folder = null,
        bool unreadOnly = false,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MailMessage> messages = await _provider
            .ListMessagesAsync(folder, unreadOnly, limit, cancellationToken)
            .ConfigureAwait(false);

        if (messages.Count == 0)
        {
            return unreadOnly
                ? "No unread messages."
                : $"No messages{(folder is null ? string.Empty : $" in {folder}")}.";
        }

        var sb = new StringBuilder();
        sb.Append(messages.Count).AppendLine(" message(s), * = unread:");

        foreach (MailMessage message in messages)
            sb.AppendLine(message.ToString());

        return sb.ToString();
    }

    [ShellvisTool(
        "mail_read",
        SideEffect.ReadOnly,
        Description =
            "Read one message in full, headers and body. Pass an id from mail_messages.",
        PreviewParameter = "messageId",
        Glyph = "mail")]
    public async Task<string> Read(
        string messageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return "Give a message id from mail_messages.";

        return await _provider
            .ReadMessageAsync(messageId.Trim(), cancellationToken)
            .ConfigureAwait(false);
    }

    [ShellvisTool(
        "mail_reply_draft",
        SideEffect.Mutating,
        Description =
            "Write a reply and leave it in Drafts. It is never sent -- the user reviews "
            + "and sends it. Set replyAll to include everyone on the thread.",
        PreviewParameter = "messageId",
        Glyph = "mail")]
    public async Task<string> ReplyDraft(
        string messageId,
        string body,
        bool replyAll = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return "Give a message id from mail_messages.";

        if (string.IsNullOrWhiteSpace(body))
            return "Give the text of the reply.";

        return await _provider
            .DraftReplyAsync(messageId.Trim(), body, replyAll, cancellationToken)
            .ConfigureAwait(false);
    }

    [ShellvisTool(
        "mail_compose_draft",
        SideEffect.Mutating,
        Description =
            "Write a new message and leave it in Drafts. It is never sent. Separate "
            + "several recipients with commas.",
        PreviewParameter = "subject",
        Glyph = "mail")]
    public async Task<string> ComposeDraft(
        string to,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(to))
            return "Give at least one recipient.";

        return await _provider
            .DraftMessageAsync(to, subject ?? string.Empty, body ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
    }
}
