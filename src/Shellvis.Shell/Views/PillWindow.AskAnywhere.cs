using Shellvis.Core.Desktop;
using Shellvis.Core.Office;

namespace Shellvis.Shell.Views;

/// <summary>
/// Ctrl+Alt+Q: ask about what is in front of you, in any program.
///
/// <b>What it does.</b> The selection in the foreground program -- a paragraph in Word, a mail
/// in Outlook, a log line in the terminal, a passage on a web page -- becomes the next
/// question's context on the workbench, and the cursor is put in the question box. With
/// nothing selected, the text of the focused document is taken instead, marked as the whole
/// document rather than a selection.
///
/// <b>Why a context and not a question.</b> The selection says WHAT, never what about it:
/// translate it, answer it, find the ticket it mentions. Sending it off with a guessed
/// question would spend a minute of the endpoint on the wrong one.
///
/// <b>Writing the answer back</b> needs nothing new: the desktop tools can type into a window,
/// behind the approval they always ask for, and the context names the window it came from.
/// </summary>
public sealed partial class PillWindow
{
    private void AskAboutForeground()
    {
        // Taken now, synchronously, before anything this method does can move the focus.
        nint foreground = WindowInspector.Foreground()?.Handle ?? 0;

        _ = AskAboutForegroundAsync(foreground);
    }

    private async Task AskAboutForegroundAsync(nint foreground)
    {
        SelectionCapture? capture = null;

        bool ours = WindowInspector.Describe(foreground) is { } window
            && window.ProcessId == Environment.ProcessId;

        if (!ours)
        {
            try
            {
                capture = await Task.Run(() => SelectionReader.Read(foreground));
            }
            catch (Exception ex)
            {
                AddRow(GlyphWarning, $"the selection could not be read: {ex.Message}", "ask");
            }
        }

        if (capture is not null)
        {
            string program = capture.Window.ProcessName;

            string name = capture.Selected
                ? L($"Selection from {program}", $"Auswahl aus {program}")
                : L($"Contents of {program}", $"Inhalt von {program}");

            SetDocumentContext(new DocumentContext(
                name,
                $"{program}: {capture.Window.Title}",
                capture.Text,
                capture.Truncated));

            AddRow(GlyphTool,
                $"{(capture.Selected ? "selection" : "whole document")} from {program} ({capture.Text.Length} characters) is the next question's context",
                "ask");
        }
        else if (!ours)
        {
            AddRow(GlyphWarning,
                "the program in front offers no text to UI Automation; select the text and copy it, or attach the document instead",
                "ask");
        }

        ShowVorzimmer();
        _vorzimmer?.ShowSection("conversation");
        _vorzimmer?.FocusPrompt();
    }
}
