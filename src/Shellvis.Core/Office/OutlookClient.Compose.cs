using System.Text;

namespace Shellvis.Core.Office;

/// <summary>
/// What was written, and who it was addressed to, for a draft that a person will send.
/// </summary>
/// <param name="Resolved">Addresses the address book recognised, as it recognised them.</param>
/// <param name="Unresolved">
/// What it did not recognise. Reported rather than dropped: a draft addressed to a typo
/// looks finished and fails the moment somebody presses Send, by which time the mistake is
/// three steps behind them.
/// </param>
public sealed record DraftAddressing(
    IReadOnlyList<string> Resolved,
    IReadOnlyList<string> Unresolved)
{
    public bool AnyResolved => Resolved.Count > 0;

    public string Describe()
    {
        var sb = new StringBuilder();

        if (Resolved.Count > 0)
            sb.Append("to ").Append(string.Join(", ", Resolved));

        if (Unresolved.Count > 0)
        {
            if (sb.Length > 0)
                sb.Append("; ");

            sb.Append("NOT recognised by the address book: ")
              .Append(string.Join(", ", Unresolved));
        }

        return sb.ToString();
    }
}

public sealed partial class OutlookClient
{
    /// <summary>
    /// Forward a message, with something of your own at the top.
    ///
    /// <b>Outlook's own Forward() rather than a new message.</b> It carries the attachments,
    /// the quoted original and the subject prefix, which a hand-built message does not: a
    /// forward that silently drops the attachment is worse than no forward, because the
    /// recipient is told the file is enclosed.
    ///
    /// A DRAFT, like everything else here. Nothing in this application sends mail.
    /// </summary>
    public Task<string> ForwardDraftAsync(
        string entryId,
        string to,
        string comment,
        string? cc = null,
        CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? original = null;
            dynamic? forward = null;

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                session = outlook.Session;
                original = session.GetItemFromID(entryId);
                forward = original.Forward();

                string subject = Str(() => original.Subject);

                DraftAddressing addressing = Address(forward, to, cc);

                if (!addressing.AnyResolved)
                {
                    // Nothing to send it to, so nothing is saved. A draft in the Drafts
                    // folder addressed to nobody is a thing somebody has to notice and
                    // delete; an error is a thing they can act on.
                    return $"error: none of '{to}' could be resolved to a recipient, so no "
                        + "forward was saved. Give a full name as it appears in the address "
                        + "book, or an email address.";
                }

                if (comment is { Length: > 0 })
                {
                    forward.Body = comment
                        + Environment.NewLine + Environment.NewLine
                        + forward.Body;
                }

                forward.Save();

                // The draft's own id comes back, so the answer can be acted on: mail_open
                // puts it in front of the user, which is what makes "I have written it" a
                // claim they can check rather than one they have to take.
                return $"saved a draft forward of \"{subject}\" {addressing.Describe()}. "
                    + "It has NOT been sent."
                    + Environment.NewLine + "      id: " + Str(() => forward.EntryID);
            }
            finally
            {
                Com.ReleaseAll(outlook, session, original, forward);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Reply to a message, optionally to somebody other than the sender or everybody.
    ///
    /// <b>Why this is a third option and not a flag.</b> Reply goes to the sender, ReplyAll
    /// goes to everybody, and the case that was missing is neither: one named person out of a
    /// thread of nine, which is how a question gets answered without copying in the eight
    /// people it does not concern. Outlook has no ReplyTo(person), so the reply is built with
    /// Reply() -- which is what carries the quoted original -- and then re-addressed.
    /// </summary>
    /// <param name="to">
    /// Names or addresses, semicolon or comma separated. When given, the reply goes to
    /// exactly these and the original recipients are dropped.
    /// </param>
    public Task<string> ReplyToAsync(
        string entryId,
        string body,
        string to,
        string? cc = null,
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

                // Reply() and not CreateItem(), because the quoted original comes with it.
                // The requirement was explicitly that the message being answered ends up in
                // the answer, and Outlook already does that better than a hand-rolled quote:
                // it keeps the formatting, the attribution line and the thread headers.
                reply = original.Reply();

                string subject = Str(() => original.Subject);

                // Emptied before adding, which is the whole difference from a plain reply.
                // Recipients.Remove takes a 1-based index and the collection shifts under
                // you, so it is walked backwards.
                dynamic recipients = reply.Recipients;
                int count = (int)recipients.Count;

                for (int i = count; i >= 1; i--)
                    recipients.Remove(i);

                DraftAddressing addressing = Address(reply, to, cc);

                if (!addressing.AnyResolved)
                {
                    return $"error: none of '{to}' could be resolved to a recipient, so no "
                        + "reply was saved. Give a full name as it appears in the address "
                        + "book, or an email address.";
                }

                reply.Body = body + Environment.NewLine + Environment.NewLine + reply.Body;
                reply.Save();

                return $"saved a draft reply to \"{subject}\" {addressing.Describe()}, with "
                    + "the original message quoted below. It has NOT been sent."
                    + Environment.NewLine + "      id: " + Str(() => reply.EntryID);
            }
            finally
            {
                Com.ReleaseAll(outlook, session, original, reply);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Split a recipient list, without cutting a name in half.
    ///
    /// <b>A comma is part of a name here, and splitting on it broke the feature.</b> This
    /// address book, like most in a German organisation, lists people as "Kluge, Thomas".
    /// The first version split on both semicolons and commas "because a comma is what a
    /// person types" -- so "Kluge, Thomas" arrived as "Kluge" and "Thomas", neither of which
    /// resolves, and the harness reported the whole reply-to-one-person feature as broken
    /// when the only thing broken was this line.
    ///
    /// So: semicolon always, because it is Outlook's own separator. A comma only when the
    /// result would be a list of ADDRESSES -- every fragment containing an @ -- which is the
    /// one case where a comma cannot be part of a name. Everything else stays whole.
    ///
    /// Public for the harness: this is pure, and it is where the feature silently becomes a
    /// search for two people who do not exist.
    /// </summary>
    public static IReadOnlyList<string> SplitRecipients(string? list)
    {
        var result = new List<string>();

        foreach (string piece in (list ?? string.Empty).Split(
            ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] byComma = piece.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (byComma.Length > 1 && byComma.All(p => p.Contains('@', StringComparison.Ordinal)))
            {
                result.AddRange(byComma);
                continue;
            }

            result.Add(piece);
        }

        return result;
    }

    /// <summary>
    /// A resolved recipient's SMTP address, not the X500 name Exchange hands over.
    ///
    /// <b>Measured, and it made the answer useless.</b> A recipient resolved inside an
    /// Exchange organisation has an <c>Address</c> of
    /// <c>/o=ExchangeLabs/ou=Exchange Administrative Group/...</c>, which is a perfectly
    /// good routing address and unreadable to a person: the first run of the forward tool
    /// reported "to Kluge, Thomas &lt;/o=ExchangeLabs/ou=Exc..." and the whole point of
    /// echoing the recipient back is that the user can recognise it. Same problem, same
    /// answer as <see cref="SmtpOf"/> for a sender: ask the Exchange user object.
    /// </summary>
    private static string SmtpOfRecipient(dynamic recipient)
    {
        string address = Str(() => recipient.Address);

        if (!address.StartsWith('/'))
            return address;

        string smtp = Str(() =>
        {
            dynamic? entry = null;
            dynamic? exchange = null;

            try
            {
                entry = recipient.AddressEntry;
                exchange = entry?.GetExchangeUser();
                return exchange?.PrimarySmtpAddress;
            }
            finally
            {
                Com.ReleaseAll(entry, exchange);
            }
        });

        // The X500 name rather than nothing when Exchange will not say: it is at least an
        // identity, and a distribution list has no Exchange user object at all.
        return smtp.Length > 0 ? smtp : address;
    }

    /// <summary>The mailbox this Outlook is signed in as.</summary>
    /// <param name="Name">The display name, which is what the address book resolves against.</param>
    /// <param name="Address">The SMTP address.</param>
    public sealed record Mailbox(string Name, string Address);

    /// <summary>
    /// The signed-in mailbox's own address, as the address book knows it.
    ///
    /// Three attempts in descending order of usefulness, for the same reason
    /// <see cref="SmtpOf"/> has them: inside an Exchange organisation the plain address of an
    /// account is an X500 distinguished name, which is useless for anything a person would
    /// recognise or type. The Exchange user object is where the SMTP address actually lives.
    /// </summary>
    public Task<Mailbox> OwnMailboxAsync(CancellationToken cancellationToken = default)
    {
        return apartment.InvokeAsync(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;
            dynamic? me = null;
            dynamic? entry = null;

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                session = outlook.Session;
                me = session.CurrentUser;
                entry = me.AddressEntry;

                string name = Str(() => me.Name);
                string smtp = Str(() => entry.GetExchangeUser().PrimarySmtpAddress);

                if (smtp.Length == 0)
                {
                    string plain = Str(() => entry.Address);
                    smtp = plain.StartsWith('/') ? string.Empty : plain;
                }

                return new Mailbox(name, smtp);
            }
            catch (Exception)
            {
                return new Mailbox(string.Empty, string.Empty);
            }
            finally
            {
                Com.ReleaseAll(outlook, session, me, entry);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Delete one item by id. <b>Deliberately not a tool.</b>
    ///
    /// This exists so the harness can remove the drafts and the test appointment it creates,
    /// and for nothing else. It is public because the harness is another assembly; it is
    /// unregistered because the tool registry is where this application's boundary actually
    /// is, and the harness asserts that no registered tool can delete mail. A capability the
    /// model cannot reach is a capability the model cannot misuse, and a harness that leaves
    /// three drafts and a meeting behind every run is one people stop running.
    ///
    /// Straight to Deleted Items, which is where Outlook's own delete puts things.
    /// </summary>
    public Task<bool> DeleteItemAsync(string entryId, CancellationToken cancellationToken = default)
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
                item.Delete();

                return true;
            }
            catch (Exception)
            {
                // An id that no longer resolves is already gone, which is the outcome the
                // caller wanted. Reported as false so a harness can say so, not thrown.
                return false;
            }
            finally
            {
                Com.ReleaseAll(outlook, session, item);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Put recipients on an item and ask the address book to confirm them.
    ///
    /// <b>Resolved explicitly rather than assigned as a string.</b> Setting <c>.To</c> to
    /// "Thomas Kluge" leaves an unresolved recipient that looks perfectly normal until
    /// somebody presses Send, and the requirement here was that a name works as well as an
    /// address. Resolve() is Outlook asking its own address book, so a name that exists
    /// becomes an address and one that does not is reported back rather than guessed at.
    /// </summary>
    private static DraftAddressing Address(dynamic item, string to, string? cc)
    {
        var resolved = new List<string>();
        var unresolved = new List<string>();

        Add(to, olTo: 1);

        if (cc is { Length: > 0 })
            Add(cc, olTo: 2);

        return new DraftAddressing(resolved, unresolved);

        void Add(string list, int olTo)
        {
            foreach (string entry in SplitRecipients(list))
            {
                dynamic? recipient = null;

                try
                {
                    recipient = item.Recipients.Add(entry);
                    recipient.Type = olTo;

                    if ((bool)recipient.Resolve())
                    {
                        // The resolved form, not what was typed: seeing "Kluge, Thomas
                        // <thomas.kluge@...>" come back is how the user knows the right
                        // Thomas was found.
                        string name = Str(() => recipient.Name);
                        string address = SmtpOfRecipient(recipient);

                        resolved.Add(name.Length > 0 && address.Length > 0 && name != address
                            ? $"{name} <{address}>"
                            : name.Length > 0 ? name : address);
                    }
                    else
                    {
                        unresolved.Add(entry);

                        // Taken off again, so a saved draft never carries a recipient that
                        // the address book has already refused.
                        try
                        {
                            recipient.Delete();
                        }
                        catch (Exception)
                        {
                            // An entry Outlook will not remove is still reported as
                            // unresolved, which is the part that matters.
                        }
                    }
                }
                catch (Exception)
                {
                    unresolved.Add(entry);
                }
                finally
                {
                    Com.Release(recipient);
                }
            }
        }
    }
}
