using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shellvis.Core.Sessions;
using Shellvis.Shell.Agent;

namespace Shellvis.Shell.Views;

/// <summary>
/// The session manager: list past conversations, resume one, delete one, start fresh.
///
/// It lives in the console panel rather than in a surface of its own. The pill's
/// clipping region and expand animation are the two most delicate pieces of the window,
/// and a second floating panel would mean a third shape in the region and a second
/// animation to keep in step with it. Switching what the existing panel shows costs
/// none of that.
/// </summary>
public sealed partial class PillWindow
{
    // Escaped rather than written as the character itself, and that is the whole point.
    //
    // These three were literal private-use characters and every one of them was EMPTY: the
    // glyph had been lost by some file rewrite along the way, leaving an empty string. The
    // buttons were still there and still clickable, so nothing failed and no test noticed --
    // there was simply nothing drawn on them. That is why the history looked like a list
    // with no controls, and why "you cannot open a chat by clicking" and "where is the
    // delete button" were both true at once.
    //
    // The eight glyph constants elsewhere in this application all use unicode escapes and
    // all survived. A character that cannot be typed, cannot be read back, and vanishes
    // silently has no business being stored as itself.
    private const string GlyphHistory = "\uE81C";
    private const string GlyphDelete = "\uE74D";
    private const string GlyphResume = "\uE768";

    private bool _historyVisible;

    /// <summary>Swap the panel between the transcript and the session list.</summary>
    private void ToggleHistory()
    {
        _historyVisible = !_historyVisible;

        HistoryView.Visibility = _historyVisible ? Visibility.Visible : Visibility.Collapsed;
        TranscriptScroller.Visibility = _historyVisible ? Visibility.Collapsed : Visibility.Visible;
        // The header slot carries the model name now, and while the history is showing it
        // says so instead. Switching back restores the model rather than a fixed word,
        // because that slot is also the model picker.
        if (_historyVisible)
            SetModelButtonText("History");
        else
            RefreshModelLabel();

        // Switching views is pointless while the panel is shut, so opening it is part
        // of the same gesture.
        if (_historyVisible && !_consoleOpen)
            ToggleConsole();

        if (_historyVisible)
            RefreshSessionList();
    }

    private void RefreshSessionList()
    {
        SessionList.Items.Clear();

        if (_session is null)
        {
            SessionList.Items.Add(Muted("history is not available yet."));
            return;
        }

        string? search = HistorySearch.Text is { Length: > 0 } text ? text : null;
        IReadOnlyList<AgentSession.SessionRow> rows = _session.ListSessions(search);

        if (rows.Count == 0)
        {
            SessionList.Items.Add(Muted(
                search is null
                    ? "no conversations recorded yet."
                    : $"nothing matches \"{search}\"."));
            return;
        }

        foreach (AgentSession.SessionRow row in rows)
            SessionList.Items.Add(BuildRow(row));
    }

    private FrameworkElement BuildRow(AgentSession.SessionRow row)
    {
        SessionInfo info = row.Info;

        var grid = new Grid
        {
            // Indentation carries the compaction lineage. Without it a compacted
            // conversation reads as several unrelated near-duplicates.
            Margin = new Thickness(row.Depth * 14, 2, 0, 2),
            ColumnSpacing = 4,
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new StackPanel { Spacing = 0 };

        label.Children.Add(new TextBlock
        {
            Text = info.Title,
            FontSize = 12,
            // The current conversation is marked by weight rather than by a badge:
            // there is no horizontal room for one.
            FontWeight = row.IsCurrent
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = ThemeBrush("ConsoleTextBrush"),
        });

        string detail = $"{info.StartedAt:dd.MM. HH:mm}  ·  {info.MessageCount} msg"
            + (info.ToolCallCount > 0 ? $"  ·  {info.ToolCallCount} calls" : string.Empty)
            + (row.Depth > 0 ? "  ·  continued" : string.Empty)
            + (row.IsCurrent ? "  ·  current" : string.Empty);

        label.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 10,
            Opacity = 0.65,
            Foreground = ThemeBrush("ConsoleMutedBrush"),
        });

        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        Button resume = IconButton(GlyphResume, "Resume this conversation");
        resume.IsEnabled = !row.IsCurrent;
        resume.Click += (_, _) => OnResume(info);
        resume.Tapped += (_, e) => e.Handled = true;
        Grid.SetColumn(resume, 1);
        grid.Children.Add(resume);

        Button delete = IconButton(GlyphDelete, row.IsCurrent
            ? "Delete this conversation (a new one will be started)"
            : "Delete this conversation");

        // Enabled for the current conversation too. It was disabled because the store
        // refuses to delete the live session -- correctly, since the agent would be writing
        // into a row that no longer exists. But a greyed-out button on the one row the user
        // is most likely to try is a dead end, not a safeguard: the whole complaint was that
        // sessions cannot be deleted from the history. Leaving the conversation first is
        // exactly what the refusal message told the user to do, so it is done for them.
        delete.IsEnabled = true;
        delete.Click += (_, _) => OnDelete(info);

        // Marked handled BEFORE it reaches the row, or deleting a conversation would
        // also open it -- the confirmation would appear over a transcript that was just
        // replaced, which is the worst possible moment to ask "are you sure".
        delete.Tapped += (_, e) => e.Handled = true;
        Grid.SetColumn(delete, 2);
        grid.Children.Add(delete);

        // The whole row opens the conversation, not only the small arrow at its end.
        //
        // Reported as "you cannot open a chat from the history by clicking", and the
        // report was right: a list of conversations that does not respond to being
        // clicked is a list that looks broken. Every other list of this shape -- a mail
        // client, a browser history, a chat application -- opens on the row, and a
        // twenty-six pixel target at the far right is not a discoverable substitute.
        //
        // The buttons keep working and take precedence: a click that lands on Delete
        // must not also resume. Tapped bubbles, so the handlers below mark it handled.
        if (!row.IsCurrent)
        {
            // Background rather than none, or the gaps between the label and the buttons
            // are not part of the row for hit-testing purposes and the click falls
            // through. Transparent still receives input; null does not.
            grid.Background = new SolidColorBrush(Colors.Transparent);

            grid.Tapped += (_, e) =>
            {
                e.Handled = true;
                OnResume(info);
            };

            // Says so, because a click target with no pointer feedback is one nobody
            // tries twice.
            ToolTipService.SetToolTip(grid, "Open this conversation");
        }

        return grid;
    }

    private void OnResume(SessionInfo info)
    {
        if (_session is null)
            return;

        IReadOnlyList<StoredMessage> messages = _session.ResumeSession(info.Id);

        // The transcript is rebuilt from storage so the console shows the conversation
        // being continued, not the one that happened to be on screen before.
        Transcript.Items.Clear();

        AddRow(GlyphSpeaker,
            $"Resuming \"{info.Title}\" ({messages.Count} messages).",
            string.Empty, isAnnouncement: true);

        // The conversation goes to the conversation window; the console gets the log.
        //
        // This used to put the whole exchange into the console as prose AND the last answer
        // into the window, while a live turn did neither of those things. Two paths giving
        // two different answers to "where do messages appear" is what made the separation
        // read as arbitrary. One rule now: what was said is in the window, what the machine
        // did is in the console.
        var said = new List<Turn>();

        foreach (StoredMessage message in messages)
        {
            switch (message.Role)
            {
                case "user":
                    said.Add(new Turn(Said.User, message.Content));
                    AddRow(GlyphPerson, Oneline(message.Content), "asked");
                    break;

                case "assistant":
                    said.Add(new Turn(Said.Assistant, message.Content));
                    AddRow(GlyphSpeaker, $"answered, {WordCount(message.Content)} words",
                        "answer", isAnnouncement: true);
                    break;

                case "tool":
                    AddRow(GlyphTool, $"{message.ToolName}: {FirstLine(message.Content)}", "replayed");
                    break;
            }
        }

        // The machine has moved on since the conversation was recorded, and the model
        // must not assume otherwise.
        AddRow(GlyphWarning,
            "Tool state was not restored: the shell session, UI snapshots and "
            + "connections belong to now, not to then.",
            string.Empty);

        ToggleHistory();
        StatusText.Text = ShellvisVoice.Standby;

        // Opening a conversation from the history means wanting to read it, so the window
        // comes forward with the whole exchange in it rather than only the last answer.
        ShowConversation(said, reveal: true);
    }

    private async void OnDelete(SessionInfo info)
    {
        if (_session is null)
            return;

        // Deleting a conversation is irreversible and the row is small, so the
        // confirmation is worth the extra click.
        var dialog = new ContentDialog
        {
            XamlRoot = (Content as FrameworkElement)?.XamlRoot,
            Title = "Delete this conversation?",
            Content = $"\"{info.Title}\"\n{info.MessageCount} messages from "
                + $"{info.StartedAt:dd.MM.yyyy HH:mm}. This cannot be undone."
                + (_session.IsCurrentSession(info.Id)
                    ? "\n\nThis is the conversation you are in, so a new one will be started."
                    : string.Empty),
            PrimaryButtonText = "Delete",
            CloseButtonText = "Keep",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        // Leave it before removing it. The store still refuses to delete the live session,
        // and that guard stays: this makes the session not-live rather than weakening it.
        if (_session.IsCurrentSession(info.Id))
        {
            _session.StartNewSession();

            Transcript.Items.Clear();
            ClearConversation();

            AddRow(GlyphSpeaker, "Shellvis has taken the stage again.",
                string.Empty, isAnnouncement: true);
        }

        string result = _session.DeleteSession(info.Id);
        RefreshSessionList();

        if (result != "deleted.")
            AddRow(GlyphWarning, result, "history");
    }

    private void OnNewSession()
    {
        if (_session is null)
            return;

        _session.StartNewSession();

        Transcript.Items.Clear();
        ClearConversation();

        AddRow(GlyphSpeaker, "Shellvis has taken the stage again.", string.Empty, isAnnouncement: true);

        ToggleHistory();
        StatusText.Text = ShellvisVoice.Standby;
    }

    private static Button IconButton(string glyph, string tooltip)
    {
        // Every value comes from the shared style now. Building a button in code with its
        // own size, font and chrome is how these ended up looking like a different
        // application from every other icon button in the pill.
        var button = new Button
        {
            Content = glyph,
            Style = (Style)Application.Current.Resources["PillIconButtonSmallStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.7,
        Margin = new Thickness(0, 8, 0, 0),
        TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeBrush("ConsoleMutedBrush"),
    };
}
