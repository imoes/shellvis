using System.Globalization;
using System.Text;

using Shellvis.Core.Desk;

namespace Shellvis.Core.Tools;

/// <summary>
/// The remembered desk, as tools.
///
/// <b>What these are for.</b> Every mail, ticket and task the assistant has walked past in
/// the last three months is already written down with what is known about it, and with
/// whatever was worked out about it afterwards. So the second question about a thing does not
/// have to start where the first one did.
///
/// <b>Read the cache before reaching for the source.</b> That is the rule these tools exist
/// to make possible, and it is worth stating as a rule because the model's instinct is the
/// other way round. <c>desk_about</c> on a ticket key costs one local query and returns the
/// ticket, every notification about it, and every conclusion already drawn -- where
/// <c>jira_issue</c> plus <c>jira_comments</c> costs two HTTP round trips and comes back with
/// no memory at all. The source is still the source: fetch it when the cache is empty, when
/// the question is about right now, or when what is cached looks stale. Then write back what
/// was learned.
///
/// <b>Writing is silent, and only into the cache.</b> <c>desk_note</c> changes nothing
/// outside this machine: no mail moves, no ticket is commented, nothing is sent. It is the
/// assistant's own margin note on its own index, which is why it does not ask -- the same
/// judgement <c>note_add</c> makes, for the same reason.
/// </summary>
public sealed class DeskTools(DeskStore desk, DeskWindow window)
{
    /// <summary>How much of one enrichment is shown in a list, before the fetch.</summary>
    private const int InList = 220;

    [ShellvisTool(
        "desk_about",
        SideEffect.ReadOnly,
        Description =
            "Everything already known about one thing and what it is connected to. Takes "
            + "either a desk id (mail:..., ticket:IMIT-1234, task:..., appointment:...) or a "
            + "bare ticket key like IMIT-1234. Returns what the source said, what this "
            + "assistant has worked out about it before, and the related things -- the mail "
            + "that mentioned a ticket, the task it caused, the rest of the conversation. "
            + "CALL THIS FIRST when a question is about a specific mail or ticket: it is one "
            + "local query and it carries the history, where fetching the source again costs "
            + "a round trip and arrives with no memory. Fetch the source afterwards if this "
            + "is empty, if the question is about right now, or if what is here looks out of "
            + "date -- and then write back what you learned with desk_note.",
        PreviewParameter = "id",
        Glyph = "note")]
    public string About(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "error: which thing? Give a desk id or a ticket key.";

        string wanted = id.Trim();

        // A bare ticket key is the common case in a question, so it is accepted as one.
        // Requiring the prefix would mean the model has to build an id from a key it read
        // in a subject line, and building ids by hand is how they stop matching.
        if (DeskObject.KindOf(wanted) is null && LooksLikeTicket(wanted))
        {
            IReadOnlyList<DeskObject> about = desk.AboutTicket(wanted);

            return about.Count == 0
                ? $"nothing is remembered about {wanted.ToUpperInvariant()}. That is the "
                    + "answer for this cache; the ticket itself may well exist. Fetch it with "
                    + "jira_issue and jira_comments, then write back what it said."
                : Render($"About {wanted.ToUpperInvariant()}", about);
        }

        DeskObject? thing = desk.Get(wanted);

        if (thing is null)
        {
            return $"nothing is remembered under '{wanted}'. Ids look like "
                + "mail:<message-id>, ticket:IMIT-1234, task:<entry-id>. Search for it with "
                + "desk_search, or fetch the source.";
        }

        var sb = new StringBuilder();

        sb.AppendLine(One(thing));

        if (thing.Enrichment is { Length: > 0 } known)
        {
            sb.AppendLine();
            sb.AppendLine("What is already known about it:");

            foreach (string line in known.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                sb.Append("  ").AppendLine(line.Trim());
        }

        IReadOnlyList<DeskObject> related = desk.Related(thing.Id);

        if (related.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Connected to:");

            foreach (DeskObject other in related)
                sb.Append("  ").AppendLine(Line(other));
        }

        return sb.ToString().TrimEnd();
    }

    [ShellvisTool(
        "desk_search",
        SideEffect.ReadOnly,
        Description =
            "Search the remembered desk: subjects, senders and this assistant's own earlier "
            + "conclusions, over the last three months. Use it to find the thing before "
            + "asking Outlook or Jira for it -- 'what did I promise about the FTP access', "
            + "'what came in from Weber', 'which ticket was the printer one'. Leave days out "
            + "to use the period the user set on the slider, which is what they mean by "
            + "'lately'; give it only when the question names a period of its own ('im Juni', "
            + "'letzte Woche'). This searches what has been WALKED PAST, not the whole "
            + "mailbox: an empty result means it is not in the cache, not that it does not "
            + "exist.",
        PreviewParameter = "query",
        Glyph = "search")]
    public string Search(string query, int days = 0, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "error: a search needs something to look for.";

        // Zero means "use the window the user set", not "use everything". A tool that
        // ignored the slider unless asked would make the control decorative.
        DateTime since = days > 0
            ? DateTime.Now.AddDays(-Math.Clamp(days, 1, DeskWindow.Most))
            : window.Since(DateTime.Now);

        IReadOnlyList<DeskObject> found = desk.Search(query, since, Math.Clamp(limit, 1, 60));

        if (found.Count == 0)
        {
            return $"nothing remembered matches '{query}' in {window.Describe()}. The cache "
                + "holds what has been walked past, so this is not proof it does not exist -- "
                + "widen the window, or search the source if it matters.";
        }

        return Render($"{found.Count} remembered thing(s) matching '{query}'", found);
    }

    [ShellvisTool(
        "desk_note",
        SideEffect.ReadOnly,
        Description =
            "Write back what you worked out about one thing, so the next question about it "
            + "starts here instead of from scratch. One or two sentences of MEANING, not a "
            + "copy of the text: 'Weber is waiting for the FTP password, promised for "
            + "Friday', 'closed in the grooming on 23 June, a parallel ticket exists at the "
            + "vendor'. Do this after you have read a ticket or a thread and said something "
            + "useful about it. Notes accumulate with their dates; nothing is overwritten. "
            + "This writes only to the local index -- no mail moves, no ticket is commented, "
            + "nothing is sent.",
        PreviewParameter = "note",
        Glyph = "note")]
    public string Note(string id, string note)
    {
        if (string.IsNullOrWhiteSpace(id))
            return "error: which thing is this about? Give the desk id.";

        if (string.IsNullOrWhiteSpace(note))
            return "error: nothing to write.";

        string wanted = id.Trim();

        if (DeskObject.KindOf(wanted) is null && LooksLikeTicket(wanted))
            wanted = DeskObject.MakeId(DeskKind.Ticket, wanted);

        if (desk.Get(wanted) is null)
        {
            return $"nothing is remembered under '{wanted}', so there is nothing to add to. "
                + "Ids come out of desk_about and desk_search; a ticket key works too.";
        }

        desk.Enrich(wanted, note.Trim(), DateTime.Now);

        return $"noted against {wanted}.";
    }

    [ShellvisTool(
        "desk_recent",
        SideEffect.ReadOnly,
        Description =
            "The most recent things on the remembered desk, newest first, whatever kind they "
            + "are. Leave days out to use the period the user set on the slider. Useful for "
            + "'what has been going on' and for picking up where a previous session left off; "
            + "for what is unread RIGHT NOW use mail_list instead, because this holds what has "
            + "been walked past rather than the live mailbox.",
        PreviewParameter = "days",
        Glyph = "note")]
    public string Recent(int days = 0, int limit = 30)
    {
        DateTime since = days > 0
            ? DateTime.Now.AddDays(-Math.Clamp(days, 1, DeskWindow.Most))
            : window.Since(DateTime.Now);

        IReadOnlyList<DeskObject> found = desk.Recent(since, Math.Clamp(limit, 1, 60));

        if (found.Count == 0)
        {
            return $"nothing has been remembered in {window.Describe()}. The desk is filled "
                + "as Shellvis looks at the mailbox, so a fresh installation is empty until "
                + "the first look.";
        }

        return Render($"{found.Count} thing(s) since {since:yyyy-MM-dd}", found);
    }

    [ShellvisTool(
        "desk_state",
        SideEffect.ReadOnly,
        Description =
            "How much the desk remembers and how far back it goes. Answers 'do you still "
            + "have that' before a search is worth running.",
        Glyph = "note")]
    public string State()
    {
        int held = desk.Count();

        if (held == 0)
            return "the desk remembers nothing yet; it fills as Shellvis looks at the mailbox.";

        DateTime? oldest = desk.Oldest();

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{held} thing(s) remembered")
            + (oldest is { } from
                ? string.Create(CultureInfo.CurrentCulture, $", back to {from:yyyy-MM-dd}.")
                : ".")
            + $" Anything older than {DeskStore.DefaultRetention.TotalDays:F0} days is forgotten,"
            + $" and 'lately' currently means {window.Describe()}.";
    }

    /// <summary>A ticket key by shape: two or more capitals, a dash, a number.</summary>
    private static bool LooksLikeTicket(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            text,
            @"^[A-Za-z][A-Za-z0-9]{1,9}-[1-9][0-9]{0,5}$");

    private static string Render(string heading, IReadOnlyList<DeskObject> things)
    {
        var sb = new StringBuilder();

        sb.AppendLine(heading + ":");

        foreach (DeskObject thing in things)
            sb.Append("  ").AppendLine(Line(thing));

        return sb.ToString().TrimEnd();
    }

    /// <summary>One thing on one line, with its id first because that is the argument.</summary>
    private static string Line(DeskObject thing)
    {
        var parts = new List<string>
        {
            thing.Id,
            string.Create(CultureInfo.CurrentCulture, $"{thing.When:yyyy-MM-dd HH:mm}"),
        };

        if (thing.WhoName is { Length: > 0 })
            parts.Add(thing.WhoName);

        parts.Add(thing.Subject is { Length: > 0 } subject ? subject : "(no subject)");

        if (thing.State is { Length: > 0 })
            parts.Add($"[{thing.State}]");

        if (thing.Due is { } due)
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"due {due:yyyy-MM-dd}"));

        if (thing.Enrichment is { Length: > 0 } known)
            parts.Add("known: " + Clip(known.ReplaceLineEndings(" "), InList));

        return string.Join("  ", parts);
    }

    /// <summary>One thing in full, several lines.</summary>
    private static string One(DeskObject thing)
    {
        var sb = new StringBuilder();

        sb.AppendLine(thing.Id);
        sb.AppendLine($"kind: {DeskObject.Prefix(thing.Kind)}");
        sb.AppendLine($"subject: {(thing.Subject is { Length: > 0 } s ? s : "(none recorded yet)")}");

        if (thing.WhoName is { Length: > 0 } || thing.WhoAddress is { Length: > 0 })
            sb.AppendLine($"from: {thing.WhoName} {thing.WhoAddress}".TrimEnd());

        sb.AppendLine(string.Create(CultureInfo.CurrentCulture, $"when: {thing.When:yyyy-MM-dd HH:mm}"));

        if (thing.Due is { } due)
            sb.AppendLine(string.Create(CultureInfo.CurrentCulture, $"due: {due:yyyy-MM-dd}"));

        if (thing.State is { Length: > 0 })
            sb.AppendLine($"state: {thing.State}");

        if (thing.TicketKey is { Length: > 0 })
            sb.AppendLine($"ticket: {thing.TicketKey}");

        // The entry id is what opens it in Outlook, and it is the one field that goes stale.
        // Said so here rather than left to be discovered: a tool that fails on a moved mail
        // is confusing, a tool that says the handle may be old is not.
        if (thing.EntryId is { Length: > 0 } entry)
            sb.AppendLine($"open with: {entry}   (refreshed on every look; refetch if it fails)");

        sb.AppendLine(string.Create(
            CultureInfo.CurrentCulture,
            $"first seen {thing.FirstSeen:yyyy-MM-dd}, last seen {thing.LastSeen:yyyy-MM-dd HH:mm}"));

        return sb.ToString().TrimEnd();
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
