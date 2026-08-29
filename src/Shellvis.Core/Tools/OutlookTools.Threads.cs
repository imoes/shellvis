using Shellvis.Core.Office;

namespace Shellvis.Core.Tools;

/// <summary>
/// Mail in context: the thread it belongs to, and the history with the person who sent it.
///
/// Both read-only, both silent. Reading the user's own mailbox to answer a question about
/// it changes nothing, and prompting for it would train the user to click through prompts
/// on the calls that do matter.
///
/// The rule that shapes the whole mail surface is untouched here and stated again because
/// this is the tool that will tempt it: <b>nothing is ever sent.</b> These two exist so a
/// suggested reply is written in the right register, and the reply itself is still a draft
/// a person decides to send.
/// </summary>
public sealed partial class OutlookTools
{
    [ShellvisTool(
        "mail_thread",
        SideEffect.ReadOnly,
        Description =
            "Read the whole conversation a message belongs to, oldest first, from the inbox "
            + "and sent items both. Use this BEFORE drafting a reply: it shows what was "
            + "already said and, importantly, how the user themselves answered this person "
            + "before. A reply written without the thread lands in the wrong register.",
        PreviewParameter = "messageId",
        Glyph = "mail")]
    public async Task<string> ReadThread(
        string messageId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(messageId))
            return "error: a message id is required. Get one from mail_list.";

        try
        {
            IReadOnlyList<MailSummary> thread = await _outlook
                .ReadThreadAsync(messageId.Trim(), Math.Clamp(limit, 1, 50), cancellationToken)
                .ConfigureAwait(false);

            return OutlookClient.Render(thread, "in this conversation") + StartNotice();
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }

    [ShellvisTool(
        "mail_history",
        SideEffect.ReadOnly,
        Description =
            "Recent correspondence with one person, newest first, in both directions. Give "
            + "a name or an address. Use this to answer 'what is going on between us' and "
            + "to match the register the user already uses with that person. A first mail "
            + "from someone has no thread, and then this is the only context there is.",
        PreviewParameter = "person",
        Glyph = "mail")]
    public async Task<string> ReadHistory(
        string person,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        if (!OutlookClient.IsAvailable)
            return Unavailable;

        if (string.IsNullOrWhiteSpace(person))
            return "error: a name or an address is required.";

        try
        {
            IReadOnlyList<MailSummary> history = await _outlook
                .ReadHistoryAsync(person.Trim(), Math.Clamp(limit, 1, 50), cancellationToken)
                .ConfigureAwait(false);

            if (history.Count == 0)
            {
                // An empty result is an answer, and saying so plainly is the correction to
                // the worst thing that has happened in this application: a calendar of six
                // invented appointments, produced from a query that legitimately found none.
                return $"no recent messages to or from '{person.Trim()}' in the inbox or sent "
                    + "items. That is the answer; do not fill it in from memory."
                    + StartNotice();
            }

            return OutlookClient.Render(history, $"with '{person.Trim()}'") + StartNotice();
        }
        catch (Exception ex)
        {
            return Failure(ex);
        }
    }
}
