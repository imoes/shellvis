using System.Text.Json;

namespace Shellvis.Shell.Views;

/// <summary>
/// What happens when a link in an answer is clicked.
///
/// <b>Why there is an internal scheme at all.</b> The point of a link here is not to reach
/// the web; it is to get from a sentence about a mail back to the mail. "Person XY has
/// written to you about Z" is only useful if the next click opens it. That is an action
/// inside this application, so it needs an address this application understands, and
/// <c>shellvis:</c> is it.
///
/// <b>An outside link is a navigation, and navigations are announced.</b> A link's text is
/// written by a model out of something it read, so the target can come from a web page, a
/// tool description or an MCP server rather than from the user. The full target is put in
/// the transcript before the shell is handed it, because a link whose text says one thing
/// and whose target says another is the oldest trick there is, and here the text was written
/// by something that may have been talked into it.
/// </summary>
public sealed partial class PillWindow
{
    /// <summary>The scheme that means "do this inside Shellvis".</summary>
    private const string InternalScheme = "shellvis:";

    /// <summary>
    /// Act on a clicked link.
    ///
    /// Nothing here throws: a malformed target is a bad line in an answer, not a fault in
    /// the application, and taking the window down over one would be out of all proportion.
    /// </summary>
    private void OnLinkActivated(string target)
    {
        string href = (target ?? string.Empty).Trim();

        if (href.Length == 0)
            return;

        if (href.StartsWith(InternalScheme, StringComparison.OrdinalIgnoreCase))
        {
            RunInternalLink(href[InternalScheme.Length..].TrimStart('/'));
            return;
        }

        _ = OpenOutsideAsync(href);
    }

    /// <summary>
    /// Carry out a <c>shellvis:</c> action.
    ///
    /// A closed set, deliberately. A link is text a model wrote, so anything reachable this
    /// way is something a remote party could talk it into writing; the set stays small
    /// enough that each entry is a decision rather than a surface.
    /// </summary>
    private void RunInternalLink(string action)
    {
        int slash = action.IndexOf('/', StringComparison.Ordinal);

        string verb = slash < 0 ? action : action[..slash];
        string argument = slash < 0 ? string.Empty : action[(slash + 1)..];

        switch (verb.ToLowerInvariant())
        {
            case "mail" when argument.Length > 0:
                _ = RunToolAsync("mail_open", "messageId", argument);
                break;

            default:
                AddRow(GlyphWarning, $"I do not know the link '{action}'.", "link");
                break;
        }
    }

    /// <summary>
    /// Run one tool on the user's behalf, from a click.
    ///
    /// <b>Through the registry, not around it.</b> Reaching for an OutlookClient here would
    /// be a second route to the mailbox with its own error handling, its own "Outlook had to
    /// be started" notice and its own way of going wrong. The tool is already the tested
    /// front door, and using it means a click and a model call reach the mailbox the same
    /// way.
    ///
    /// No approval prompt: the click IS the consent, and asking someone to confirm the thing
    /// they just clicked is the kind of prompt that teaches people to dismiss prompts.
    /// </summary>
    private async Task RunToolAsync(string tool, string parameter, string value)
    {
        Shellvis.Core.Tools.ToolRegistry? registry = _session?.Registry;

        if (registry is null)
        {
            AddRow(GlyphWarning, "The agent is not ready yet.", "link");
            return;
        }

        try
        {
            using var arguments = JsonDocument.Parse(
                JsonSerializer.Serialize(new Dictionary<string, string> { [parameter] = value }));

            string result = await registry.InvokeAsync(tool, arguments.RootElement)
                .ConfigureAwait(true);

            AddRow(GlyphTool, result, tool);
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"{tool} failed: {ex.Message}", "link");
        }
    }

    /// <summary>
    /// Hand a link outside Shellvis to the shell.
    ///
    /// The target is written out first. It is the only way the user can see where a link
    /// actually goes, and the visible text is not evidence: it was written by a model out of
    /// material it read somewhere.
    /// </summary>
    private async Task OpenOutsideAsync(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out Uri? uri))
        {
            AddRow(GlyphWarning, $"'{href}' is not an address I can open.", "link");
            return;
        }

        AddRow(GlyphTool, $"opening {uri}", "link");

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"could not open {uri}: {ex.Message}", "link");
        }
    }
}
