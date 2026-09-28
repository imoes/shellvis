using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shellvis.Core.Office;
using Shellvis.Shell.Agent;
using Shellvis.Shell.Controls;
using Windows.System;

namespace Shellvis.Shell.Views;

/// <summary>The workbench shares the pill's session; this window only displays and requests work.</summary>
public sealed partial class VorzimmerWindow
{
    public event Action<string>? PromptRequested;
    public event Action? PickDocumentRequested;
    public event Action? RefreshDocumentsRequested;
    public event Action<OpenDocument>? OpenDocumentRequested;
    public event Action? ClearContextRequested;
    public event Action<string?>? HistoryRequested;
    public event Action<string>? ResumeRequested;
    public event Action<string>? DeleteRequested;
    public event Action? NewSessionRequested;

    private bool German => _text.LanguageTag == "de";
    public XamlRoot RootXamlRoot => RootHost.XamlRoot;
    public string? HistoryQuery => string.IsNullOrWhiteSpace(WorkbenchHistorySearch.Text)
        ? null : WorkbenchHistorySearch.Text;

    private void InitializeWorkbench()
    {
        TodayButton.Click += (_, _) => ShowSection("today");
        ConversationButton.Click += (_, _) => ShowSection("conversation");
        DocumentsButton.Click += (_, _) =>
        {
            ShowSection("documents");
            RefreshDocumentsRequested?.Invoke();
        };
        ActivityButton.Click += (_, _) => ShowSection("activity");
        WorkbenchHistoryButton.Click += (_, _) =>
        {
            ShowSection("history");
            HistoryRequested?.Invoke(WorkbenchHistorySearch.Text);
        };
        WorkbenchHistorySearch.TextChanged += (_, _) => HistoryRequested?.Invoke(WorkbenchHistorySearch.Text);
        WorkbenchNewSessionButton.Click += (_, _) =>
        {
            NewSessionRequested?.Invoke();
            ShowSection("conversation");
        };
        SendButton.Click += (_, _) => SubmitPrompt();
        WorkbenchPrompt.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter)
                return;
            args.Handled = true;
            SubmitPrompt();
        };
        PickDocumentButton.Click += (_, _) => PickDocumentRequested?.Invoke();
        RefreshDocumentsButton.Click += (_, _) => RefreshDocumentsRequested?.Invoke();
        ContextButton.Click += (_, _) => ClearContextRequested?.Invoke();

        MeetingAction.Click += (_, _) => Prepare(German
            ? "Bereite meine nächste Besprechung vor: Termin, Teilnehmende, relevante Korrespondenz und offene Punkte."
            : "Prepare my next meeting: appointment, attendees, relevant correspondence and open questions.");
        MailAction.Click += (_, _) => Prepare(German
            ? "Entwirf eine Antwort auf die betreffende E-Mail. Speichere sie nur als Entwurf."
            : "Draft a reply to the relevant email. Save it as a draft only.");
        DocumentAction.Click += (_, _) => Prepare(German
            ? "Fasse das ausgewählte Dokument zusammen und nenne offene Fragen."
            : "Summarize the selected document and list open questions.");
        TaskAction.Click += (_, _) => Prepare(German
            ? "Leite aus meinen Notizen konkrete Aufgaben mit möglichen Fälligkeiten ab. Frage vor dem Anlegen nach."
            : "Derive concrete tasks and possible due dates from my notes. Ask before creating them.");

        TodayButton.Content = German ? "Heute" : "Today";
        ConversationButton.Content = German ? "Unterhaltung" : "Conversation";
        DocumentsButton.Content = German ? "Dokumente" : "Documents";
        ActivityButton.Content = German ? "Aktivität" : "Activity";
        WorkbenchHistoryButton.Content = German ? "Verlauf" : "History";
        WorkbenchNewSessionButton.Content = German ? "Neue Unterhaltung beginnen" : "Start a new conversation";
        WorkbenchHistorySearch.PlaceholderText = German ? "Unterhaltungen suchen" : "Search conversations";
        MeetingAction.Content = German ? "Besprechung vorbereiten" : "Prepare meeting";
        MailAction.Content = German ? "E-Mail-Antwort entwerfen" : "Draft email reply";
        DocumentAction.Content = German ? "Dokument zusammenfassen" : "Summarize document";
        TaskAction.Content = German ? "Aufgaben aus Notizen" : "Tasks from notes";
        RefreshDocumentsButton.Content = German ? "Offene Dokumente aktualisieren" : "Refresh open documents";
        PickDocumentButton.Content = German ? "Datei auswählen…" : "Choose file…";
        WorkbenchPrompt.PlaceholderText = German ? "Shellvis fragen" : "Ask Shellvis";
        SendButton.Content = German ? "Fragen" : "Ask";
        ResetDocumentHint();
        ShowSection("today");
    }

    private void ResetDocumentHint() => DocumentsHint.Text = German
        ? "Wähle ein offenes Office-Dokument oder eine DOCX-, XLSX- oder PPTX-Datei. Der Inhalt wird erst mit deiner nächsten Frage verwendet."
        : "Choose an open Office document or a DOCX, XLSX or PPTX file. Its contents are used only with your next question.";

    public void ShowSection(string section)
    {
        View.Visibility = section == "today" ? Visibility.Visible : Visibility.Collapsed;
        ConversationPanel.Visibility = section == "conversation" ? Visibility.Visible : Visibility.Collapsed;
        DocumentsPanel.Visibility = section == "documents" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPanel.Visibility = section == "activity" ? Visibility.Visible : Visibility.Collapsed;
        WorkbenchHistoryPanel.Visibility = section == "history" ? Visibility.Visible : Visibility.Collapsed;
        TodayButton.IsEnabled = section != "today";
        ConversationButton.IsEnabled = section != "conversation";
        DocumentsButton.IsEnabled = section != "documents";
        ActivityButton.IsEnabled = section != "activity";
        WorkbenchHistoryButton.IsEnabled = section != "history";
    }

    private void SubmitPrompt()
    {
        string prompt = WorkbenchPrompt.Text.Trim();
        if (prompt.Length == 0)
            return;
        WorkbenchPrompt.Text = string.Empty;
        ShowSection("conversation");
        PromptRequested?.Invoke(prompt);
    }

    private void Prepare(string prompt)
    {
        WorkbenchPrompt.Text = prompt;
        WorkbenchPrompt.Focus(FocusState.Programmatic);
        WorkbenchPrompt.Select(WorkbenchPrompt.Text.Length, 0);
    }

    public void ShowConversation(string markdown, bool streaming)
    {
        MarkdownRenderer.Render(
            ConversationBody, markdown,
            new FontFamily("Segoe UI Variable Text"), new FontFamily("Cascadia Mono"), 14,
            (Brush)Application.Current.Resources["ConsoleTextBrush"],
            (Brush)Application.Current.Resources["ConsoleMutedBrush"]);
        StateText.Text = streaming
            ? (German ? "Antwort wird geschrieben…" : "Writing answer…")
            : string.Empty;
        DispatcherQueue.TryEnqueue(() =>
            ConversationScroller.ChangeView(null, ConversationScroller.ScrollableHeight, null));
    }

    public void AppendActivity(string line)
    {
        ActivityItems.Children.Add(new TextBlock
        {
            Text = line,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono"),
        });
        while (ActivityItems.Children.Count > 200)
            ActivityItems.Children.RemoveAt(0);
    }

    public void ShowDocuments(IReadOnlyList<OpenDocument> documents)
    {
        ResetDocumentHint();
        DocumentItems.Children.Clear();
        if (documents.Count == 0)
        {
            DocumentItems.Children.Add(new TextBlock
            {
                Text = German ? "Kein Office-Dokument ist geöffnet." : "No Office document is open.",
            });
            return;
        }

        foreach (OpenDocument document in documents)
        {
            var button = new Button
            {
                Content = $"{document.Application}: {document.Name}"
                    + (document.Saved ? string.Empty : (German ? " (ungespeichert)" : " (unsaved)")),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            button.Click += (_, _) => OpenDocumentRequested?.Invoke(document);
            DocumentItems.Children.Add(button);
        }
    }

    internal void ShowSessions(IReadOnlyList<AgentSession.SessionRow> sessions)
    {
        WorkbenchHistoryItems.Children.Clear();
        foreach (AgentSession.SessionRow row in sessions)
        {
            var layout = new Grid { ColumnSpacing = 6 };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var button = new Button
            {
                Content = $"{row.Info.Title}  ·  {row.Info.StartedAt:dd.MM.yyyy HH:mm}"
                    + (row.IsCurrent ? (German ? "  ·  aktuell" : "  ·  current") : string.Empty),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };

            // The current one is enabled too, and only switches to the conversation tab: it
            // is already there, and resuming it from storage would reload what is on screen.
            // Disabled, a history holding one conversation was one greyed-out button --
            // reported as the list not being shown at all.
            button.Click += (_, _) =>
            {
                if (!row.IsCurrent)
                    ResumeRequested?.Invoke(row.Info.Id);

                ShowSection("conversation");
            };
            layout.Children.Add(button);

            var delete = new Button { Content = German ? "Löschen" : "Delete" };
            AutomationProperties.SetName(delete, German
                ? $"Unterhaltung {row.Info.Title} löschen"
                : $"Delete conversation {row.Info.Title}");
            delete.Click += (_, _) => DeleteRequested?.Invoke(row.Info.Id);
            Grid.SetColumn(delete, 1);
            layout.Children.Add(delete);
            WorkbenchHistoryItems.Children.Add(layout);
        }

        if (sessions.Count == 0)
            WorkbenchHistoryItems.Children.Add(new TextBlock
            {
                Text = German ? "Keine Unterhaltungen gefunden." : "No conversations found.",
            });
    }

    public void SetContext(DocumentContext? context)
    {
        if (context is not null)
            ResetDocumentHint();
        ContextButton.Visibility = context is null ? Visibility.Collapsed : Visibility.Visible;
        ContextButton.Content = context is null ? string.Empty : $"{context.Name}  ×";
        ContextNote.Text = context is null ? string.Empty
            : $"{context.Source} · " + (context.Truncated
                ? (German ? "Auszug gekürzt" : "Excerpt shortened")
                : (German ? "Wird bei der nächsten Frage mitgelesen" : "Included with the next question"));
        ToolTipService.SetToolTip(ContextNote, context?.Source);
    }

    public void ShowDocumentError(string message)
    {
        DocumentsHint.Text = message;
        ShowSection("documents");
    }
}
