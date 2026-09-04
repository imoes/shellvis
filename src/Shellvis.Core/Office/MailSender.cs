namespace Shellvis.Core.Office;

/// <summary>
/// Did a person write this, or a system?
///
/// <b>Why this is not <see cref="TicketKeys.LooksAutomated"/>, which already existed.</b> That
/// one answers a narrower question -- is this an automated notification ABOUT A TICKET -- and
/// it is used to decide whether to go and read the ticket instead of the mail. Reusing it for
/// "is this a person" was a category error with a visible consequence: Confluence page
/// notifications were counted as mail from a human and landed under "braucht heute eine
/// Antwort". They are a system talking, and nobody answers them.
///
/// Two questions, two functions. A Confluence notification is a system and not a ticket; a
/// colleague writing "wegen IMIT-1234" is a person and not a notification. One predicate
/// cannot be right about both.
///
/// <b>Decided on the sender, never on the body.</b> A template is whatever an administrator
/// last saved and changes without warning; the address a thing sends from is configuration.
/// And on the sender rather than on the subject, because a subject with "[MTZ-CONFLUENCE]" in
/// it may equally be a colleague quoting one.
/// </summary>
public static class MailSender
{
    /// <summary>
    /// The shapes a machine's address takes.
    ///
    /// <b>Substrings, deliberately, and in one list.</b> The alternative -- a rule about
    /// local parts, or a regular expression over the domain -- reads as cleverness and then
    /// has to be debugged against real mail. This is a list of what actually arrives in a
    /// working mailbox, and it is meant to be added to.
    ///
    /// The cost of being wrong is asymmetric, which is what justifies the breadth: a system
    /// mistaken for a person puts a notification under "needs an answer today", where it
    /// wastes attention every time the page is opened. A person mistaken for a system is
    /// counted beside the newsletters, which is a smaller loss and one the count above the
    /// list still reveals.
    /// </summary>
    private static readonly string[] Machines =
    [
        // Nobody is listening at the other end. The clearest signal there is.
        "no-reply", "noreply", "no_reply", "donotreply", "do-not-reply", "do_not_reply",

        // What the sending system calls itself.
        "notification", "notifications", "notify", "benachrichtigung", "automat",
        "automated", "automailer", "mailer-daemon", "mailerdaemon", "postmaster",
        "bounce", "bounces", "daemon", "robot", "bot@", "noc@", "root@",

        // The products in this estate that write mail of their own accord. Named rather
        // than inferred: a product name in a sender is a fact, and guessing from a pattern
        // is how a colleague called Jira Nieminen becomes a notification.
        "confluence", "jira", "servicedesk", "service-desk", "service desk", "atlassian",
        "sharepoint", "onedrive", "teams@", "sonarqube", "gitlab", "github",
        "checkmk", "nagios", "icinga", "zabbix", "grafana", "awx", "jenkins",
        "netbox", "graylog", "backup", "monitoring", "alerting", "alert@",

        // Mail somebody signed up for once.
        "newsletter", "mailing", "marketing", "info@", "kontakt@", "support@",
    ];

    /// <summary>
    /// Whether this came from a system rather than from somebody who might expect an answer.
    /// </summary>
    /// <remarks>
    /// The name is searched as well as the address, because Exchange hands back an X500 path
    /// for an internal sender and the display name is then the only readable half -- which is
    /// exactly the case that let "IT-Confluence" through.
    /// </remarks>
    public static bool LooksLikeSystem(string? address, string? name = null)
    {
        string from = ((address ?? string.Empty) + " " + (name ?? string.Empty))
            .ToLowerInvariant();

        if (from.Trim().Length == 0)
            return false;

        foreach (string marker in Machines)
        {
            if (from.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
