using System.Text.RegularExpressions;

namespace Shellvis.Core.Office;

/// <summary>
/// Finding the ticket a mail is about.
///
/// <b>Why this is code and not a sentence in a prompt.</b> Two reasons. The watcher that
/// notices a Jira mail arriving has no model in the loop at that moment: it has to decide by
/// itself whether this is a ticket notification and which ticket, before there is anything
/// worth asking a model about. And a key is exactly the kind of token a model gets subtly
/// wrong -- "JCUE-5915" read off a subject line that also contains "AW:" and a date, or the
/// first of three keys chosen when the last is the one the mail is about.
///
/// <b>The shape, and what it deliberately excludes.</b> A Jira key is letters, a hyphen and
/// digits: <c>IMIT-1234</c>. So is a great deal of other text. The rules below come from what
/// this mailbox actually contains rather than from the general case:
///
/// <list type="bullet">
/// <item>At least two letters, so a hyphenated word like "e-1" is not a ticket.</item>
/// <item>Upper case only. Jira keys are upper case and prose is not, and matching case
/// insensitively turned "Teil-3" and "Version-2" into tickets.</item>
/// <item>Bounded by a non-word character, so "ABC-12345678901" and the middle of a URL do
/// not match, but "[IMIT-1234]" and "IMIT-1234:" do -- which is how subjects are written.</item>
/// </list>
///
/// Order is preserved and duplicates are dropped, because a notification names the same
/// ticket in the subject, the body and the footer, and "three tickets" would be wrong.
/// </summary>
public static class TicketKeys
{
    private static readonly Regex Key = new(
        @"(?<![A-Za-z0-9])(?<key>[A-Z][A-Z0-9]{1,9}-[1-9][0-9]{0,5})(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Every distinct key in the text, in the order it appears.</summary>
    public static IReadOnlyList<string> FindAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<string>();

        foreach (Match match in Key.Matches(text))
        {
            string key = match.Groups["key"].Value;

            if (seen.Add(key))
                found.Add(key);
        }

        return found;
    }

    /// <summary>
    /// The one key a message is about, or null.
    ///
    /// <b>The subject decides.</b> A Jira notification puts the key in the subject and then
    /// again in a body full of links, footers and sometimes the keys of related issues; the
    /// subject is the one place it means "this mail is about that". Only when the subject has
    /// none is the body consulted, which covers a mail somebody forwarded by hand.
    /// </summary>
    public static string? Primary(string? subject, string? body = null)
    {
        if (FindAll(subject) is [string first, ..])
            return first;

        return FindAll(body) is [string fromBody, ..] ? fromBody : null;
    }

    /// <summary>
    /// Whether a message looks like an automated ticket notification rather than a person
    /// writing about a ticket.
    ///
    /// <b>Why the distinction matters.</b> The two want opposite handling. A notification is
    /// mostly template and its content is already in the ticket, so the right move is to go
    /// and read the ticket. A colleague mentioning a ticket number is a mail to be read as a
    /// mail -- fetching the ticket and summarising that instead would answer a question
    /// nobody asked and lose what they actually wrote.
    ///
    /// Decided on the SENDER rather than the body, because the body of a notification is
    /// whatever the administrator's template says and changes without warning, while the
    /// address it comes from is configuration.
    /// </summary>
    public static bool LooksAutomated(string? senderAddress, string? senderName = null)
    {
        string from = ((senderAddress ?? string.Empty) + " " + (senderName ?? string.Empty))
            .ToLowerInvariant();

        if (from.Length == 0)
            return false;

        // "jira@", "servicedesk@", "no-reply@jira...", and the display names those accounts
        // are usually given. Deliberately a short list of substrings rather than a clever
        // rule: this is configuration about one installation, and a mail wrongly treated as
        // a notification is only ever read as a ticket instead of as prose.
        foreach (string marker in new[]
        {
            "jira", "servicedesk", "service-desk", "service desk", "atlassian",
        })
        {
            if (from.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
