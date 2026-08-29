using System.Globalization;
using System.Text;

namespace Shellvis.Core.Office;

/// <summary>
/// Reading a message in the light of what came before it.
///
/// <b>Why this is a separate capability and not a bigger mail_read.</b> "Understand the mail
/// from its history and suggest a reply" was asked for directly, and it had no data behind
/// it: the summary carried neither a thread key nor a sender address, so the model could
/// only ever answer from the one message in front of it. A reply written without the thread
/// is a reply in the wrong register, and the plan already records that a draft in the wrong
/// register is worse than no draft.
///
/// <b>Two different questions, two methods.</b> A thread answers "what has been said about
/// this"; a sender history answers "what is going on between us". They are not
/// interchangeable: a first mail from someone has no thread at all and the history is the
/// only context there is, while a long thread may involve people the user has never
/// otherwise written to.
/// </summary>
public sealed partial class OutlookClient
{
    /// <summary>
    /// Every message in one conversation, oldest first.
    ///
    /// <b>Why the thread key and not the subject.</b> Subjects get edited mid-thread,
    /// prefixes accumulate, and two unrelated threads called "Re: Angebot" are ordinary in
    /// any mailbox. Outlook keeps a real conversation id and it is the only honest way to
    /// ask this question.
    ///
    /// Searched across the inbox and sent items, both, and that is the point: a thread with
    /// only the incoming half is a transcript of someone talking at the user. What they
    /// themselves already replied is exactly what a suggested answer has to build on.
    /// </summary>
    public Task<IReadOnlyList<MailSummary>> ReadThreadAsync(
        string entryId,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync<IReadOnlyList<MailSummary>>(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? anchor = null;

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                session = outlook.Session;
                anchor = session.GetItemFromID(entryId);

                string conversation = Str(() => anchor.ConversationID);

                var found = new List<MailSummary>();

                if (conversation.Length == 0)
                {
                    // No conversation id at all happens: a message imported from elsewhere, a
                    // report, an item Outlook never threaded. Returning the one message is
                    // honest and useful; returning nothing would read as "no such thread".
                    found.Add(ReadMail(anchor));
                    return found;
                }

                foreach (int folder in new[] { FolderInbox, FolderSentMail })
                    Collect(session, folder, conversation, found, limit, cancellationToken);

                // Oldest first, because that is the order it was said in and the order
                // anyone reads a thread. The listings come back newest first, which is right
                // for an inbox and wrong for a conversation.
                return found
                    .GroupBy(m => m.EntryId, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .OrderBy(m => m.Received)
                    .Take(limit)
                    .ToList();
            }
            finally
            {
                Com.ReleaseAll(outlook, session, anchor);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// The recent correspondence with one person, both directions.
    ///
    /// Matched on address rather than on display name. Names repeat across an organisation
    /// and change when someone marries; an address does neither. The match is a substring so
    /// that a display name still works when that is all the caller has, which is the common
    /// case when the user says "what is going on with Meier".
    /// </summary>
    public Task<IReadOnlyList<MailSummary>> ReadHistoryAsync(
        string person,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync<IReadOnlyList<MailSummary>>(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                session = outlook.Session;

                var found = new List<MailSummary>();

                foreach (int folder in new[] { FolderInbox, FolderSentMail })
                    CollectByPerson(session, folder, person, found, limit, cancellationToken);

                // Newest first here, unlike a thread: the question is what is going on now,
                // and the most recent exchange is the answer to it.
                return found
                    .GroupBy(m => m.EntryId, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .OrderByDescending(m => m.Received)
                    .Take(limit)
                    .ToList();
            }
            finally
            {
                Com.ReleaseAll(outlook, session);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Walk one folder, newest first, keeping what matches the conversation.
    ///
    /// Scanned rather than filtered with Restrict, and that is a considered choice.
    /// ConversationID is not a filterable field in Outlook's DASL for every store, and a
    /// Restrict that silently matches nothing is the failure mode this project already met
    /// in the calendar: an empty result that looks exactly like an answer. A bounded scan is
    /// slower and cannot fail quietly.
    /// </summary>
    private static void Collect(
        object session,
        int folder,
        string conversation,
        List<MailSummary> into,
        int limit,
        CancellationToken cancellationToken)
    {
        Walk(session, folder, cancellationToken, item =>
        {
            if (!string.Equals(Str(() => item.ConversationID), conversation, StringComparison.Ordinal))
                return into.Count >= limit ? Walking.Stop : Walking.Continue;

            into.Add(ReadMail(item));

            return into.Count >= limit ? Walking.Stop : Walking.Continue;
        });
    }

    private static void CollectByPerson(
        object session,
        int folder,
        string person,
        List<MailSummary> into,
        int limit,
        CancellationToken cancellationToken)
    {
        int before = into.Count;

        Walk(session, folder, cancellationToken, item =>
        {
            MailSummary mail = ReadMail(item);

            bool match =
                mail.SenderAddress.Contains(person, StringComparison.OrdinalIgnoreCase)
                || mail.From.Contains(person, StringComparison.OrdinalIgnoreCase)
                || Str(() => item.To).Contains(person, StringComparison.OrdinalIgnoreCase);

            if (match)
                into.Add(mail);

            return into.Count - before >= limit ? Walking.Stop : Walking.Continue;
        });
    }

    private enum Walking
    {
        Continue,
        Stop,
    }

    /// <summary>
    /// Newest first through one folder, with a hard cap.
    ///
    /// <b>The cap is the whole design.</b> A mailbox holds tens of thousands of items and a
    /// COM property read is a cross-process call; walking all of them would take minutes and
    /// hold the one STA thread every Office call in this application shares. Recent history
    /// is what the question is about anyway, so the scan stops after a few hundred and the
    /// answer is about the recent past rather than about everything.
    /// </summary>
    private static void Walk(
        object session,
        int folder,
        CancellationToken cancellationToken,
        Func<dynamic, Walking> visit)
    {
        const int Scan = 400;

        dynamic? mapiFolder = null;
        dynamic? items = null;

        // Typed as object and widened here rather than taken as dynamic. A dynamic
        // parameter makes the whole call dynamic, and a dynamically bound call cannot
        // take a lambda -- which is exactly what the visitor is.
        dynamic store = session;

        try
        {
            mapiFolder = store.GetDefaultFolder(folder);
            items = mapiFolder.Items;
            items.Sort("[ReceivedTime]", true);

            int take = Math.Min(Scan, (int)items.Count);

            for (int i = 1; i <= take; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                dynamic? item = null;

                try
                {
                    item = items[i];

                    if (visit(item) == Walking.Stop)
                        return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unreadable item must not abort the walk. A meeting response, a
                    // delivery report and a corrupt message all throw on properties an
                    // ordinary mail has.
                }
                finally
                {
                    Com.Release(item);
                }
            }
        }
        finally
        {
            Com.ReleaseAll(mapiFolder, items);
        }
    }

    /// <summary>Render a thread or a history as something a model can read in one go.</summary>
    /// <remarks>
    /// The body of each message is included but clipped. A thread of twenty full mails is
    /// tens of thousands of characters and would sit in the context of every later round;
    /// what a reply needs is who said what and in what tone, and the opening of each message
    /// carries that.
    /// </remarks>
    public static string Render(IReadOnlyList<MailSummary> messages, string heading)
    {
        if (messages.Count == 0)
            return heading + ": nothing found.";

        var sb = new StringBuilder();
        sb.Append(messages.Count).Append(" message(s) ").AppendLine(heading + ":");

        foreach (MailSummary mail in messages)
        {
            sb.AppendLine();
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"  {mail.Received:ddd yyyy-MM-dd HH:mm}  {mail.From}"));

            if (mail.SenderAddress.Length > 0)
                sb.Append(" <").Append(mail.SenderAddress).Append('>');

            sb.AppendLine();
            sb.Append("  \"").Append(mail.Subject).AppendLine("\"");
            sb.Append("  ").AppendLine(mail.Preview);
            sb.Append("  id ").AppendLine(mail.EntryId);
        }

        return sb.ToString();
    }
}
