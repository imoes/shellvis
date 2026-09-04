namespace Shellvis.Core.Desk;

/// <summary>What kind of thing is on the desk.</summary>
public enum DeskKind
{
    Mail,
    Ticket,
    Task,
    Appointment,
}

/// <summary>
/// One thing on the desk, with everything already known about it.
///
/// <b>The identifier is the whole design, so it is worth being exact about.</b> A cache is
/// only useful if the same thing gets the same key every time it is seen, and the obvious
/// choice is wrong: Outlook's <c>EntryID</c> changes when an item moves between folders,
/// which is precisely what filing a mail does. Keying on it means a mail read in the inbox
/// and the same mail found in an archive folder are two rows, both half-enriched, and the
/// second one has none of what was learned about the first.
///
/// So each kind is keyed on the thing about it that does not move:
///
/// <list type="bullet">
/// <item><b>Mail</b> — the RFC 5322 message id, which travels with the message and is the
/// same string in the sender's mailbox, in the inbox and in the archive.</item>
/// <item><b>Ticket</b> — the key, <c>IMIT-1234</c>. Already stable, already global, already
/// what a person calls it.</item>
/// <item><b>Task</b> — the entry id, and here it is the right answer rather than a
/// compromise: a task lives in one folder for its whole life.</item>
/// <item><b>Appointment</b> — the global appointment id, which one occurrence of a series
/// shares with the series and not with the other occurrences.</item>
/// </list>
///
/// The kind prefixes the key (<c>mail:</c>, <c>ticket:</c>) so two kinds cannot collide on
/// an id that happens to look the same, and so an id read out of a tool result says what it
/// refers to without being looked up.
///
/// <b>EntryId is carried anyway, and it is the one field that is expected to go stale.</b>
/// It is how the item is opened in Outlook, so it has to be here; it is refreshed on every
/// sighting and must never be treated as identity.
/// </summary>
/// <param name="Id">The stable key, prefixed with its kind.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Subject">The one-line headline: a subject, a title, a ticket summary.</param>
/// <param name="WhoName">The person or system it came from, as a display name.</param>
/// <param name="WhoAddress">Their address, resolved out of Exchange when it was an X500 name.</param>
/// <param name="When">Received, starting, or created -- whichever this kind has.</param>
/// <param name="Due">A deadline if one is known, otherwise null.</param>
/// <param name="State">Read, unread, in progress, complete: whatever the source calls it.</param>
/// <param name="TicketKey">The ticket this is about, which is what links a mail to an issue.</param>
/// <param name="Thread">The conversation, which is what links a mail to its siblings.</param>
/// <param name="EntryId">The volatile Outlook handle, for opening it. Never identity.</param>
/// <param name="Facts">Extra metadata as JSON, so a new field does not need a migration.</param>
/// <param name="Enrichment">What the assistant has worked out about this, added over time.</param>
/// <param name="FirstSeen">When it first entered the cache.</param>
/// <param name="LastSeen">When it was last confirmed to exist.</param>
public sealed record DeskObject(
    string Id,
    DeskKind Kind,
    string Subject,
    string WhoName,
    string WhoAddress,
    DateTime When,
    DateTime? Due,
    string State,
    string? TicketKey,
    string? Thread,
    string? EntryId,
    string? Facts,
    string? Enrichment,
    DateTime FirstSeen,
    DateTime LastSeen)
{
    /// <summary>The prefix for a kind, which is also the first half of every id.</summary>
    public static string Prefix(DeskKind kind) => kind switch
    {
        DeskKind.Mail => "mail",
        DeskKind.Ticket => "ticket",
        DeskKind.Task => "task",
        DeskKind.Appointment => "appointment",
        _ => "thing",
    };

    /// <summary>Build an id from a kind and the source's own stable key.</summary>
    /// <remarks>
    /// The key is trimmed and lower-cased for mail, because a message id is
    /// case-insensitive by the standard and Exchange does not always return the same case
    /// twice. Ticket keys are upper-cased for the same reason from the other direction:
    /// people type them in both.
    /// </remarks>
    public static string MakeId(DeskKind kind, string key)
    {
        string clean = key.Trim().Trim('<', '>');

        clean = kind switch
        {
            DeskKind.Mail => clean.ToLowerInvariant(),
            DeskKind.Ticket => clean.ToUpperInvariant(),
            _ => clean,
        };

        return $"{Prefix(kind)}:{clean}";
    }

    /// <summary>The kind an id belongs to, or null when it is not one of ours.</summary>
    public static DeskKind? KindOf(string? id)
    {
        if (id is null)
            return null;

        int colon = id.IndexOf(':', StringComparison.Ordinal);

        if (colon <= 0)
            return null;

        return id[..colon] switch
        {
            "mail" => DeskKind.Mail,
            "ticket" => DeskKind.Ticket,
            "task" => DeskKind.Task,
            "appointment" => DeskKind.Appointment,
            _ => null,
        };
    }
}
