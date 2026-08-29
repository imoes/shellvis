using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Shellvis.Shell.Views;

/// <summary>
/// A tool call as a card rather than as a line.
///
/// <b>What was wrong with the line.</b> A tool call went into the transcript as one row
/// whose text was the first line of the result, and the rest of the result was unreachable:
/// not truncated in the model's copy, but simply not present anywhere in the user interface.
/// So a console built to answer "what did it actually do" could show that something ran and
/// could not show what came back. That is the gap this closes.
///
/// <b>What a card carries.</b> Four things, in the order someone scanning wants them: whether
/// it worked, what ran, what it was given, and how long it took. The result sits underneath,
/// collapsed, because most of the time the one-line summary is the answer and the rest is
/// noise; one click opens it.
///
/// <b>Why the mark is a mark and not a colour.</b> Colour in this console already means
/// severity, and a second meaning on the same channel makes both unreadable. A tick, a cross
/// and a dash are three shapes, legible at a glance and legible to someone who cannot
/// separate red from green.
/// </summary>
public sealed partial class PillWindow
{
    /// <summary>Ran and came back.</summary>
    private const string MarkDone = "✓";

    /// <summary>Refused, or failed.</summary>
    private const string MarkFailed = "✕";

    /// <summary>Still going.</summary>
    private const string MarkRunning = "•";

    /// <summary>How much of a result the collapsed card shows.</summary>
    private const int PreviewLength = 150;

    /// <summary>The card currently running, so its completion rewrites it in place.</summary>
    private ToolCard? _runningCard;

    /// <summary>
    /// One tool call, from start to finish.
    ///
    /// Held as a class rather than rebuilt on completion because rebuilding is what the
    /// transcript did before: it removed the pending row and appended a new one, which
    /// works until the user has scrolled or selected something in it. Rewriting the same
    /// element keeps the scroll position and the selection.
    /// </summary>
    private sealed class ToolCard
    {
        public required Grid Root { get; init; }

        public required TextBlock Mark { get; init; }

        public required TextBlock Name { get; init; }

        public required TextBlock Preview { get; init; }

        public required TextBlock Timing { get; init; }

        /// <summary>The one-line "show the rest" control.</summary>
        public required HyperlinkButton Detail { get; init; }

        public required ScrollViewer Body { get; init; }

        public required TextBlock Output { get; init; }
    }

    /// <summary>Put a running tool on screen.</summary>
    private void StartToolCard(string tool, string preview)
    {
        var card = BuildCard(tool, preview);

        _runningCard = card;
        Transcript.Items.Add(card.Root);

        // Narrated rather than left to a blinking cursor. A console that goes quiet for
        // thirty seconds while commands run invisibly is the opacity this application
        // exists to remove, and the status line is the one place a docked pill still shows.
        StatusText.Text = Narrate(tool);

        ScrollToEnd();
    }

    /// <summary>Finish the running card, or add a finished one if it went missing.</summary>
    private void FinishToolCard(bool succeeded, string result, string timing)
    {
        ToolCard card = _runningCard ?? BuildCardIntoTranscript("tool", string.Empty);
        _runningCard = null;

        card.Mark.Text = succeeded ? MarkDone : MarkFailed;
        card.Mark.Foreground = ThemeBrush(succeeded ? "ConsoleMutedBrush" : "ConsoleWarningBrush");
        card.Timing.Text = timing;

        string trimmed = (result ?? string.Empty).Trim();

        card.Preview.Text = FirstLine(trimmed);

        // The detail is offered only when there IS more than the preview. An expander that
        // opens onto the line already shown teaches the reader that expanders are empty,
        // and then the one that matters goes unopened.
        bool more = trimmed.Length > card.Preview.Text.Length
            || trimmed.Contains('\n', StringComparison.Ordinal);

        card.Detail.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        card.Output.Text = trimmed.Length > 0 ? trimmed : "(no output)";

        // The count is on the control, so the reader can tell whether opening it is worth
        // the space before spending it. "result" alone asks them to click to find out.
        int lines = trimmed.Count(c => c == '\n') + 1;
        card.Detail.Content = $"show all {lines} line(s)";

        ScrollToEnd();
    }

    private ToolCard BuildCardIntoTranscript(string tool, string preview)
    {
        ToolCard card = BuildCard(tool, preview);
        Transcript.Items.Add(card.Root);
        return card;
    }

    private ToolCard BuildCard(string tool, string preview)
    {
        var root = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var mark = new TextBlock
        {
            Text = MarkRunning,
            FontSize = 12,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = ThemeBrush("ConsoleMutedBrush"),
        };

        Grid.SetColumn(mark, 0);
        header.Children.Add(mark);

        // The tool's own name, which the line form never showed: it showed the preview
        // argument and left the reader to infer which tool produced it.
        var name = new TextBlock
        {
            Text = tool,
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = ThemeBrush("ConsoleTextBrush"),
        };

        Grid.SetColumn(name, 1);
        header.Children.Add(name);

        var previewText = new TextBlock
        {
            Text = preview,
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,

            // Selectable, so a path or an error can be copied out rather than retyped.
            IsTextSelectionEnabled = true,
            Foreground = ThemeBrush("ConsoleMutedBrush"),
        };

        Grid.SetColumn(previewText, 2);
        header.Children.Add(previewText);

        var timing = new TextBlock
        {
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = ThemeBrush("ConsoleMutedBrush"),
        };

        Grid.SetColumn(timing, 3);
        header.Children.Add(timing);

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var output = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = ThemeBrush("ConsoleMutedBrush"),
        };

        var body = new ScrollViewer
        {
            Content = output,

            // Bounded, because a directory listing is thousands of lines and an opened
            // result that pushes the whole console off screen is worse than one that does
            // not open at all.
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(20, 2, 4, 4),
            Visibility = Visibility.Collapsed,
        };

        // A one-line link rather than a WinUI Expander, and this was a correction made
        // after looking at it. An Expander brings a bordered card about fifty pixels tall
        // even when closed, so three tool calls filled the console with three grey boxes
        // and pushed the actual trace off screen: worse than the single line it replaced.
        // A console is a dense list, and a control that costs three lines to say "there is
        // more" does not belong in one.
        var detail = new HyperlinkButton
        {
            Content = "show the rest",
            FontSize = 10,
            Margin = new Thickness(16, 0, 0, 0),
            Padding = new Thickness(4, 0, 4, 0),
            MinHeight = 0,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        detail.Click += (_, _) =>
        {
            bool open = body.Visibility == Visibility.Collapsed;

            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            detail.Content = open ? "hide" : detail.Content;

            if (!open)
                detail.Content = $"show all {output.Text.Count(c => c == '\n') + 1} line(s)";
        };

        var lower = new StackPanel { Orientation = Orientation.Vertical };
        lower.Children.Add(detail);
        lower.Children.Add(body);

        Grid.SetRow(lower, 1);
        root.Children.Add(lower);

        return new ToolCard
        {
            Root = root,
            Mark = mark,
            Name = name,
            Preview = previewText,
            Timing = timing,
            Detail = detail,
            Body = body,
            Output = output,
        };
    }

    /// <summary>
    /// What to put in the status line while a tool runs.
    ///
    /// <b>Why a table and not the tool name.</b> "powershell_run" tells the user which
    /// function is executing; "running a command" tells them what is happening to their
    /// machine. The second is what somebody watching a bar on their taskbar wants, and the
    /// first is already on the card two lines below.
    ///
    /// An unknown name falls through to the name itself rather than to something vague:
    /// a tool this table has not heard of is better named exactly than described wrongly.
    /// </summary>
    private static string Narrate(string tool) => tool switch
    {
        "mail_list" or "mail_read" or "mail_thread" or "mail_history" => "Reading the mail...",
        "mail_open" => "Opening the message...",
        "calendar_list" or "agenda_due" or "agenda_today" => "Checking the calendar...",
        "task_list" or "task_create" or "task_complete" => "Looking at the task list...",
        "note_add" or "note_search" or "note_due" or "note_close" => "Checking the notes...",
        "note_stick" or "note_stickies" => "Handling a sticky note...",
        "powershell_run" or "powershell_run_winps" => "Running a command...",
        "process" => "Handling a background process...",
        "window_list" or "desktop_analyze" or "ui_read_text" => "Looking at the desktop...",
        "ui_click" or "ui_set_text" or "ui_send_keys" or "window_focus" => "Working the desktop...",
        "screen_capture" => "Taking a picture of the screen...",
        "program_open" => "Opening a program...",
        var name when name.StartsWith("browser_", StringComparison.Ordinal) => "Using the browser...",
        var name when name.StartsWith("teams_", StringComparison.Ordinal) => "Reaching Teams...",
        var name when name.StartsWith("word_", StringComparison.Ordinal)
            || name.StartsWith("excel_", StringComparison.Ordinal)
            || name.StartsWith("slides_", StringComparison.Ordinal)
            || name.StartsWith("office_", StringComparison.Ordinal) => "Working on a document...",
        var name when name.StartsWith("skill", StringComparison.Ordinal) => "Reading its own notes...",
        _ => $"Running {tool}...",
    };
}
