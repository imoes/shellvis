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
            _answerWindow = new AnswerWindow();
            _answerWindow.PlaceBeside(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }

        return _answerWindow;
    }

    /// <summary>Put the running answer in front of the reader as it arrives.</summary>
    private void ShowAnswer(string markdown, bool streaming)
    {
        AnswerWindow window = Answer();

        window.ShowAnswer(markdown, streaming ? "Answer (writing...)" : "Answer");
        window.Reveal();
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
