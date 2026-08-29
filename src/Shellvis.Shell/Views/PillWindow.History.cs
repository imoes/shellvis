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
    private const string GlyphHistory = ""; // U+E81C
    private const string GlyphDelete = ""; // U+E74D
    private const string GlyphResume = ""; // U+E768

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
        Grid.SetColumn(resume, 1);
        grid.Children.Add(resume);

        Button delete = IconButton(GlyphDelete, "Delete this conversation");
        delete.IsEnabled = !row.IsCurrent;
        delete.Click += (_, _) => OnDelete(info);
        Grid.SetColumn(delete, 2);
        grid.Children.Add(delete);

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

        foreach (StoredMessage message in messages)
        {
            switch (message.Role)
            {
                case "user":
                    AddRow(GlyphPerson, message.Content, string.Empty, isPrompt: true);
                    break;

                case "assistant":
                    // isAnswer, not isAnnouncement. A replayed answer is still an answer, and
                    // rendering it as one of Shellvis' own remarks put it in italic -- the same
                    // wrong category that made live answers look unformatted.
                    AddRow(GlyphSpeaker, message.Content, string.Empty, isAnswer: true);
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
                + $"{info.StartedAt:dd.MM.yyyy HH:mm}. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Keep",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

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
