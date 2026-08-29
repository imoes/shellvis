using Shellvis.Core.Desktop;
using Shellvis.Core.Office;
using Shellvis.Core.Teams;

namespace Shellvis.Core.Tools;

/// <summary>
/// Microsoft Teams, as far as it goes without an app registration.
///
/// <b>What this is and is not.</b> Opening a chat and joining a meeting, through the deep
/// links Teams registers with Windows. Not reading chats, not presence, not sending: those
/// need Microsoft Graph, an app registration in the user's own tenant, and in most
/// organisations an approval a person has to request. This half needs none of that and is
/// what was actually asked for.
///
/// <b>Nothing is sent.</b> A chat link can carry text, and Teams puts it in the compose box
/// and waits for a person. Same rule as the mail surface, except that here it is a property
/// of the link rather than a policy, which is stronger.
/// </summary>
public sealed class TeamsTools(ComApartment? apartment = null)
{
    private readonly OutlookClient? _outlook = apartment is null ? null : new OutlookClient(apartment);

    [ShellvisTool(
        "teams_chat_open",
        SideEffect.Mutating,
        Description =
            "Open a Teams chat with one or more people and optionally pre-fill the message "
            + "box. NOTHING IS SENT: the text lands in the box and the user presses send. "
            + "Give email addresses, not display names -- a name opens an empty search "
            + "rather than a chat. Use mail_list or mail_history to find someone's address.",
        PreviewParameter = "users",
        Glyph = "chat")]
    public async Task<string> OpenChat(
        string users,
        string? message = null,
        string? topic = null,
        CancellationToken cancellationToken = default)
    {
        string whole = (users ?? string.Empty).Trim();

        if (whole.Length == 0)
            return "error: at least one email address is required.";

        // Caught before the split, because splitting on the comma turns one display name
        // into two nonexistent people and the refusal then reads as though the user asked
        // for two of them. How a message is worded is part of the interface: this project
        // has already spent rounds on a tool result that said "Editor - Notepad" and was
        // read as one title.
        if (!whole.Contains('@', StringComparison.Ordinal))
        {
            return $"error: '{whole}' is a display name, not an email address. Teams matches "
                + "on the address, so a name opens an empty search rather than a chat. Use "
                + "mail_history or contacts_find to get the address first.";
        }

        string[] people = whole
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (people.Length == 0)
            return "error: at least one email address is required.";

        // Refused with the reason rather than opened onto an empty search. A chat that opens
        // with nobody in it looks to the user like the feature is broken, and the model has
        // a tool that can turn a name into an address.
        string[] unusable = [.. people.Where(p => !TeamsLinks.LooksAddressable(p))];

        if (unusable.Length > 0)
        {
            return $"error: {string.Join(" and ", unusable.Select(u => $"'{u}'"))} "
                + $"{(unusable.Length == 1 ? "is not an email address" : "are not email addresses")}. "
                + "Teams matches on the address, not on the display name; use mail_history "
                + "or contacts_find to get it first.";
        }

        string link = TeamsLinks.Chat(people, message, topic);

        // waitForWindow is off: Teams is normally already running, so the deep link is
        // handled by the existing window and no NEW window appears. Waiting for one would
        // spend the whole timeout and then report a failure for a link that worked.
        LaunchResult result = await ProgramLauncher
            .LaunchAsync(link, waitForWindow: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
            return $"the chat could not be opened: {result.Detail}";

        string filled = string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : " The message is in the compose box; nothing was sent.";

        return $"opened a Teams chat with {string.Join(", ", people)}.{filled}";
    }

    [ShellvisTool(
        "teams_meeting_join",
        SideEffect.Mutating,
        Description =
            "Join the Teams meeting for a calendar entry, by the id from calendar_list. "
            + "Calendar lines that are Teams meetings are marked [Teams]. Only do this when "
            + "the user asked to join: it opens a meeting others can see them enter.",
        PreviewParameter = "appointmentId",
        Glyph = "chat")]
    public async Task<string> JoinMeeting(
        string appointmentId,
        CancellationToken cancellationToken = default)
    {
        if (_outlook is null || !OutlookClient.IsAvailable)
            return "Outlook is not available on this machine, so the meeting link cannot be read.";

        if (string.IsNullOrWhiteSpace(appointmentId))
            return "error: an appointment id is required. Get one from calendar_list.";

        string? join;

        try
        {
            join = await _outlook.JoinUrlAsync(appointmentId.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"the appointment could not be read: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(join))
        {
            // An answer, not a failure. Plenty of meetings are in a room.
            return "that appointment carries no Teams join link, so it is not an online "
                + "meeting. Do not invent one.";
        }

        LaunchResult result = await ProgramLauncher
            .LaunchAsync(TeamsLinks.Meeting(join), waitForWindow: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded
            ? "opened the meeting."
            : $"the meeting link could not be opened: {result.Detail}";
    }
}
