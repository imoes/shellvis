using FlaUI.Core.AutomationElements;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;

namespace Shellvis.Core.Desktop;

/// <summary>What was in front of the user when they asked.</summary>
/// <param name="Text">The selected text, or the text of the focused document when nothing was selected.</param>
/// <param name="Selected">True when <paramref name="Text"/> is a selection rather than the whole document.</param>
/// <param name="Window">The foreground window it came from.</param>
/// <param name="Truncated">Whether the text was cut to the length limit.</param>
public sealed record SelectionCapture(string Text, bool Selected, WindowInfo Window, bool Truncated);

/// <summary>
/// The selection in whatever program is in front, read through UI Automation.
///
/// <b>Why UI Automation and not a simulated Ctrl+C.</b> Copying needs the user's keys to be
/// up -- the hotkey that asked is Ctrl+Alt+something, and a Ctrl+C sent while Alt is still held
/// is Ctrl+Alt+C -- and it overwrites the clipboard, which then has to be put back without
/// losing a format somebody cared about. The text pattern reads the selection without touching
/// either. Word, Outlook's reading pane, Edge, Chrome, Notepad and Windows Terminal all expose
/// it; a program that does not gets nothing here, and is said to.
///
/// <b>Nothing selected is not nothing.</b> The focused document's own text is the next best
/// answer to "what am I looking at", so it is taken instead and marked as such: the open mail,
/// the open page.
/// </summary>
public static class SelectionReader
{
    /// <summary>The most text taken, the same budget a document from the workbench gets.</summary>
    public const int MaxChars = Office.DocumentContextReader.MaxChars;

    /// <summary>How far up from the focused element a text pattern is looked for.</summary>
    private const int AncestorsTried = 6;

    /// <summary>
    /// Read the selection of the foreground window, or null when that window offers no text.
    ///
    /// Run it off the UI thread: UI Automation calls into the other process and can take a
    /// second on a busy one.
    /// </summary>
    public static SelectionCapture? Read(nint foreground)
    {
        WindowInfo? window = WindowInspector.Describe(foreground);

        if (window is null)
            return null;

        using var automation = new UIA3Automation();

        AutomationElement? element;

        try
        {
            element = automation.FocusedElement();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }

        for (int step = 0; element is not null && step < AncestorsTried; step++)
        {
            ITextPattern? text = TryText(element);

            if (text is not null && Take(text, window) is { } capture)
                return capture;

            try
            {
                element = element.Parent;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                return null;
            }
        }

        return null;
    }

    private static ITextPattern? TryText(AutomationElement element)
    {
        try
        {
            return element.Patterns.Text.PatternOrDefault;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static SelectionCapture? Take(ITextPattern pattern, WindowInfo window)
    {
        try
        {
            string selected = string.Join(
                "\n",
                pattern.GetSelection()
                    .Select(range => range.GetText(MaxChars + 1))
                    .Where(part => !string.IsNullOrWhiteSpace(part)));

            if (selected.Trim().Length > 0)
                return Shape(selected, selected: true, window);

            string whole = pattern.DocumentRange.GetText(MaxChars + 1);

            return whole.Trim().Length > 0 ? Shape(whole, selected: false, window) : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static SelectionCapture Shape(string text, bool selected, WindowInfo window)
    {
        string trimmed = text.Trim();
        bool truncated = trimmed.Length > MaxChars;

        return new SelectionCapture(truncated ? trimmed[..MaxChars] : trimmed, selected, window, truncated);
    }
}
