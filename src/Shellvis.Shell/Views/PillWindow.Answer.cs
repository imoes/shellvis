namespace Shellvis.Shell.Views;

/// <summary>
/// Routing the assistant's answer to its own window.
///
/// The console under the pill is now a log and nothing else: what was run, what warned, what
/// needed approval. The answer is a document and lives in <see cref="AnswerWindow"/>.
///
/// The log still records that an answer happened, with its size. Dropping the line entirely
/// would leave a record with a hole in it -- a reader scrolling the console would see the
/// tools run and then nothing, which is exactly the opacity this console exists to remove.
/// </summary>
public sealed partial class PillWindow
{
    private AnswerWindow? _answerWindow;

    /// <summary>What has been said, which is what the message window shows.</summary>
    private readonly Conversation _conversation = new();

    /// <summary>
    /// The answer window, created on first use.
    ///
    /// Lazily, because most of what this application does never produces prose -- a dictation
    /// session, a docked bar sitting on the taskbar, a scheduled job that only reads. Opening
    /// a second window at startup would cost every one of those a window nobody asked for.
    /// </summary>
    private AnswerWindow Answer()
    {
        if (_answerWindow is null)
        {
            _answerWindow = new AnswerWindow { OnLink = OnLinkActivated };
            _answerWindow.PlaceBeside(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }

        return _answerWindow;
    }

    /// <summary>Add what the user just asked to the conversation.</summary>
    private void RecordPrompt(string prompt)
    {
        _conversation.Add(Said.User, prompt);
        Redraw(streaming: false, reveal: false);
    }

    /// <summary>The answer as it streams in.</summary>
    private void StreamAnswer(string markdown)
    {
        _conversation.Streaming(markdown);
        Redraw(streaming: true, reveal: true);
    }

    /// <summary>The finished answer.</summary>
    private void RecordAnswer(string markdown)
    {
        _conversation.Add(Said.Assistant, markdown);
        Redraw(streaming: false, reveal: true);
    }

    /// <summary>
    /// Put a scheduled run's report into the conversation.
    ///
    /// <b>Why it belongs here and not only in the log.</b> A desktop alert has to open
    /// something, and what it opens is the message window -- so the report has to BE in the
    /// message window. The console keeps its one-line record of the run either way; the two
    /// are not duplicates, they are the log entry and the document.
    ///
    /// <b>Marked as scheduled, not passed off as an answer.</b> A report nobody asked for,
    /// rendered identically to a reply to a question, would make the conversation read as
    /// though the user had asked something they did not. The heading says where it came
    /// from and when.
    ///
    /// This does not reach the model. The conversation shown here and the history the agent
    /// loop reasons over are separate on purpose -- a scheduled run must never become part of
    /// the context of the next thing the user asks, which is the rule
    /// <see cref="Agent.AgentSession"/> keeps by giving cron a loop of its own.
    /// </summary>
    private void RecordScheduledReport(string job, string report, string headline)
    {
        _conversation.Add(
            Said.Assistant,
            $"**{Conversation.Escape(headline)}**\n\n{report}\n\n*Scheduled job "
                + $"'{Conversation.Escape(job)}', {DateTime.Now:HH:mm}*");

        Redraw(streaming: false, reveal: false);
    }

    /// <summary>Replace the whole conversation, for a resumed or a new session.</summary>
    private void ShowConversation(IEnumerable<Turn> turns, bool reveal)
    {
        _conversation.Clear();

        foreach (Turn turn in turns)
            _conversation.Add(turn.By, turn.Text);

        Redraw(streaming: false, reveal: reveal);
    }

    /// <summary>Start again with nothing said.</summary>
    private void ClearConversation()
    {
        _conversation.Clear();
        AnswerButton.IsEnabled = false;
        _answerWindow?.Hide();
    }

    /// <summary>
    /// Put the conversation on screen.
    ///
    /// <b>Revealing is not the same as drawing.</b> A prompt being recorded must NOT bring
    /// the window forward: the user is typing into the bar and a window arriving in front of
    /// them is the interruption this application has already argued itself out of once. An
    /// answer may reveal, because an answer is the thing they asked for.
    /// </summary>
    private void Redraw(bool streaming, bool reveal)
    {
        if (_conversation.IsEmpty)
            return;

        AnswerWindow window = Answer();

        window.ShowAnswer(
            _conversation.ToMarkdown(),
            streaming ? "Conversation (writing...)" : "Conversation");

        if (reveal)
            window.Reveal();

        AnswerButton.IsEnabled = true;
    }

    /// <summary>
    /// Bring the answer back, from the console header.
    ///
    /// The whole reason this exists: the answer moved into a window of its own and nothing
    /// could reopen it. Closing it made the document unreachable until the next reply, and
    /// the question that surfaced was simply "how do I open the message console?" -- which
    /// is the right question to ask of an interface with no answer to it.
    ///
    /// It also raises a window that is merely behind something, so the same button covers
    /// hidden, minimised and buried.
    /// </summary>
    private void OnShowAnswer()
    {
        if (_answerWindow is null)
            return;

        Answer().Reveal();
    }

    /// <summary>Close the answer window when the pill closes, or the process outlives it.</summary>
    private void CloseAnswerWindow()
    {
        AnswerWindow? window = _answerWindow;
        _answerWindow = null;

        try
        {
            window?.Close();
        }
        catch (Exception)
        {
            // A window already gone is not a problem worth propagating out of a shutdown path.
        }
    }
}
