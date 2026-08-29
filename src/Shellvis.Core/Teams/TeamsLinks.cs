using System.Text;

namespace Shellvis.Core.Teams;

/// <summary>
/// Microsoft Teams, through the deep links it registers with Windows.
///
/// <b>Why deep links and not Graph.</b> Reading chats and sending messages needs an app
/// registration in the user's own tenant plus delegated authentication, and in most
/// organisations that is something a person has to request rather than something an agent
/// can arrange. The <c>msteams:</c> scheme needs none of it: Teams registers it when it
/// installs, and a link opens the right conversation in the client the user is already
/// signed into. That covers what was actually asked for, today, without an approval process.
///
/// <b>Nothing is sent.</b> A chat deep link can carry pre-filled text, and it puts that text
/// in the compose box rather than transmitting it. That is the same rule as the mail
/// surface, and it holds for the same reason: a message that has left cannot be recalled,
/// and an agent working from a summarised understanding will occasionally be wrong. The
/// difference here is that it is not a policy we enforce but a property of the link, which
/// is better.
///
/// <b>Nothing here launches anything.</b> This class builds strings and validates arguments;
/// the launching goes through <c>ProgramLauncher</c>, which already refuses a scheme Windows
/// has no handler for instead of raising the "choose an app" dialog and hanging. That
/// protection was built for a fabricated <c>calc://</c> and it is exactly what is needed on a
/// machine without Teams.
/// </summary>
public static class TeamsLinks
{
    /// <summary>The scheme Teams registers. Checked, never assumed.</summary>
    public const string Scheme = "msteams";

    /// <summary>
    /// A link that opens a chat with one or more people.
    /// </summary>
    /// <param name="users">Addresses or UPNs. Display names do not work here.</param>
    /// <param name="message">
    /// Optional text for the compose box. It is NOT sent; Teams fills the box and waits for
    /// a person to press send.
    /// </param>
    /// <param name="topic">A name for the conversation, when there is more than one person.</param>
    public static string Chat(IEnumerable<string> users, string? message = null, string? topic = null)
    {
        string people = string.Join(
            ',',
            users.Select(u => u.Trim()).Where(u => u.Length > 0));

        var url = new StringBuilder($"{Scheme}:/l/chat/0/0?users={Escape(people)}");

        if (!string.IsNullOrWhiteSpace(topic))
            url.Append("&topicName=").Append(Escape(topic.Trim()));

        if (!string.IsNullOrWhiteSpace(message))
            url.Append("&message=").Append(Escape(message.Trim()));

        return url.ToString();
    }

    /// <summary>A link that opens a Teams meeting from its join URL.</summary>
    /// <remarks>
    /// The join URL out of a calendar entry is an https address that the browser hands to
    /// Teams. Returned as-is rather than rewritten into the deep-link form: the https link
    /// is what Microsoft generates, it works whether or not the desktop client is installed,
    /// and rewriting a URL whose exact shape is not ours to decide is how a working link
    /// becomes a broken one.
    /// </remarks>
    public static string Meeting(string joinUrl) => joinUrl.Trim();

    /// <summary>
    /// Whether an address can be used to open a chat.
    ///
    /// Teams matches on the address, not on the display name. "Meier, Anna" opens an empty
    /// search rather than a chat, which looks to the user like the feature failed. So a
    /// display name is refused here with the reason, and the model is pointed at the tool
    /// that can turn a name into an address.
    /// </summary>
    public static bool LooksAddressable(string user) =>
        user.Contains('@', StringComparison.Ordinal)
        && user.IndexOf('@', StringComparison.Ordinal) > 0
        && user.IndexOf('@', StringComparison.Ordinal) < user.Length - 1
        && !user.Contains(' ', StringComparison.Ordinal);

    /// <summary>
    /// The Teams join link inside an appointment body, if there is one.
    ///
    /// <b>Why parsed out of the body rather than read from a property.</b> Outlook exposes no
    /// "join URL" field: Teams writes the link into the body text when the meeting is
    /// created. So the body is where it is, and finding it means looking for the URL shape
    /// Microsoft uses.
    ///
    /// Matched on the host and path rather than on the surrounding words, because the body
    /// is localised: a German invitation says "Hier klicken, um an der Besprechung
    /// teilzunehmen" and an English one does not.
    /// </summary>
    public static string? JoinUrlIn(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        const string Marker = "https://teams.microsoft.com/l/meetup-join/";

        int start = body.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            // Government and some tenants use a different host with the same path.
            const string Alternate = "/l/meetup-join/";

            int path = body.IndexOf(Alternate, StringComparison.OrdinalIgnoreCase);

            if (path < 0)
                return null;

            int scheme = body.LastIndexOf("https://", path, StringComparison.OrdinalIgnoreCase);

            if (scheme < 0)
                return null;

            start = scheme;
        }

        // A URL ends at whitespace or at the markup Outlook wraps it in. Angle brackets and
        // quotes are included because the body may be the HTML form rather than plain text.
        int end = start;

        while (end < body.Length && !char.IsWhiteSpace(body[end])
               && body[end] is not ('<' or '>' or '"' or '\''))
        {
            end++;
        }

        string url = body[start..end].TrimEnd('.', ',', ')', ']');

        return url.Length > Marker.Length ? url : null;
    }

    /// <summary>
    /// Percent-encode one query value.
    ///
    /// <c>Uri.EscapeDataString</c> rather than a hand-rolled replace: a message can contain
    /// anything a person would type, and a half-escaped ampersand turns the rest of the
    /// message into query parameters Teams then ignores. That failure is silent and looks
    /// like the message was simply truncated.
    /// </summary>
    private static string Escape(string value) => Uri.EscapeDataString(value);
}
