using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Shellvis.Core.Desktop;

/// <summary>What a single desktop action did, in terms the agent can reason about.</summary>
/// <param name="Succeeded">Whether the action was carried out.</param>
/// <param name="Method">How it was carried out, e.g. "InvokePattern" or "MouseClick".</param>
/// <param name="Detail">Human-readable outcome or the reason for failure.</param>
public sealed record ActionResult(bool Succeeded, string Method, string Detail)
{
    public static ActionResult Ok(string method, string detail) => new(true, method, detail);

    public static ActionResult Failed(string detail) => new(false, "None", detail);

    public override string ToString() =>
        Succeeded ? $"ok via {Method}: {Detail}" : $"failed: {Detail}";
}

/// <summary>
/// Performs actions on elements resolved from a <see cref="DesktopSnapshot"/>.
///
/// Every method takes an element, never a coordinate. Coordinates are wrong by the
/// time the model has finished thinking about them -- the window moves, a list
/// scrolls, a dialog steals focus -- whereas an element reference either still
/// resolves or fails loudly.
///
/// Actions prefer UI Automation patterns over synthetic input. A pattern invoke does
/// not move the physical mouse, does not need the window in the foreground, and
/// cannot be intercepted by whatever happens to be under the cursor. Synthetic input
/// is the fallback for controls that expose no pattern (column headers, custom-drawn
/// canvases), which is a real and common case.
/// </summary>
public static class DesktopActions
{
    /// <summary>
    /// Activate an element: press a button, toggle a checkbox, select a list item,
    /// expand a tree node. Tries patterns in order of specificity, then a real click.
    /// </summary>
    public static ActionResult Click(AutomationElement element, bool forceMouse = false)
    {
        if (!IsUsable(element, out string? blocker))
            return ActionResult.Failed(blocker);

        if (!forceMouse)
        {
            ActionResult? viaPattern =
                TryInvoke(element) ?? TryToggle(element) ?? TrySelect(element) ?? TryExpand(element);

            if (viaPattern is not null)
                return viaPattern;
        }

        // No pattern, or the caller explicitly wants a physical click (some apps only
        // react to real input).
        try
        {
            element.Click();
            return ActionResult.Ok("MouseClick", "clicked at the element centre");
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return ActionResult.Failed($"mouse click failed: {ex.Message}");
        }
    }

    /// <summary>Right-click, for context menus. Always synthetic: UIA has no pattern for it.</summary>
    public static ActionResult RightClick(AutomationElement element)
    {
        if (!IsUsable(element, out string? blocker))
            return ActionResult.Failed(blocker);

        try
        {
            element.RightClick();
            return ActionResult.Ok("MouseRightClick", "right-clicked at the element centre");
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return ActionResult.Failed($"right click failed: {ex.Message}");
        }
    }

    public static ActionResult DoubleClick(AutomationElement element)
    {
        if (!IsUsable(element, out string? blocker))
            return ActionResult.Failed(blocker);

        try
        {
            element.DoubleClick();
            return ActionResult.Ok("MouseDoubleClick", "double-clicked at the element centre");
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return ActionResult.Failed($"double click failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Put text into an editable control.
    ///
    /// ValuePattern is tried first because it is atomic and needs no focus. Typing is
    /// the fallback, and it is genuinely different: some controls (rich edit surfaces,
    /// anything that validates per keystroke) only behave correctly when the
    /// characters arrive one at a time.
    /// </summary>
    public static ActionResult SetText(AutomationElement element, string text, bool forceTyping = false)
    {
        if (!IsUsable(element, out string? blocker))
            return ActionResult.Failed(blocker);

        if (!forceTyping)
        {
            try
            {
                if (element.Patterns.Value.IsSupported && !element.Patterns.Value.Pattern.IsReadOnly)
                {
                    element.Patterns.Value.Pattern.SetValue(text);
                    return ActionResult.Ok("ValuePattern", $"set value to {Quote(text)}");
                }
            }
            catch (Exception ex) when (IsInteropFailure(ex))
            {
                // Fall through to typing -- a refused SetValue is common on controls
                // that advertise the pattern but reject programmatic writes.
            }
        }

        try
        {
            element.Focus();
            Keyboard.Type(text);
            return ActionResult.Ok("Typed", $"typed {Quote(text)}");
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return ActionResult.Failed($"typing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Send a key combination to the focused element, e.g. Ctrl+S.
    ///
    /// Modifiers are held for the duration of the key press and released in reverse
    /// order, because leaving a modifier stuck down corrupts every subsequent action
    /// in the session.
    /// </summary>
    public static ActionResult SendKeys(
        AutomationElement? focusFirst,
        VirtualKeyShort key,
        params VirtualKeyShort[] modifiers)
    {
        try
        {
            focusFirst?.Focus();

            foreach (VirtualKeyShort modifier in modifiers)
                Keyboard.Press(modifier);

            Keyboard.Type(key);

            for (int i = modifiers.Length - 1; i >= 0; i--)
                Keyboard.Release(modifiers[i]);

            string combo = modifiers.Length == 0
                ? key.ToString()
                : string.Join('+', modifiers.Append(key));

            return ActionResult.Ok("Keyboard", $"sent {combo}");
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return ActionResult.Failed($"key send failed: {ex.Message}");
        }
    }

    /// <summary>Read the text an element exposes, whichever way it exposes it.</summary>
    public static string ReadText(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Value.IsSupported)
            {
                string? value = element.Patterns.Value.Pattern.Value.ValueOrDefault;
                if (!string.IsNullOrEmpty(value))
                    return value;
            }

            if (element.Patterns.Text.IsSupported)
            {
                string text = element.Patterns.Text.Pattern.DocumentRange.GetText(int.MaxValue);
                if (!string.IsNullOrEmpty(text))
                    return text;
            }

            return element.Properties.Name.ValueOrDefault ?? string.Empty;
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return string.Empty;
        }
    }

    // ------------------------------------------------------------------ internals

    private static ActionResult? TryInvoke(AutomationElement element) =>
        Attempt(
            () => element.Patterns.Invoke.IsSupported,
            () => element.Patterns.Invoke.Pattern.Invoke(),
            "InvokePattern",
            "invoked");

    private static ActionResult? TryToggle(AutomationElement element) =>
        Attempt(
            () => element.Patterns.Toggle.IsSupported,
            () => element.Patterns.Toggle.Pattern.Toggle(),
            "TogglePattern",
            "toggled");

    private static ActionResult? TrySelect(AutomationElement element) =>
        Attempt(
            () => element.Patterns.SelectionItem.IsSupported,
            () => element.Patterns.SelectionItem.Pattern.Select(),
            "SelectionItemPattern",
            "selected");

    private static ActionResult? TryExpand(AutomationElement element) =>
        Attempt(
            () => element.Patterns.ExpandCollapse.IsSupported,
            () => element.Patterns.ExpandCollapse.Pattern.Expand(),
            "ExpandCollapsePattern",
            "expanded");

    /// <summary>
    /// Run one pattern attempt. Returns null when the pattern is not supported, so
    /// the caller can fall through to the next candidate; returns a failure result
    /// when the pattern WAS supported but the call did not work, because that is
    /// information the agent needs rather than something to silently retry.
    /// </summary>
    private static ActionResult? Attempt(
        Func<bool> supported, Action act, string method, string verb)
    {
        try
        {
            if (!supported())
                return null;

            act();
            return ActionResult.Ok(method, verb);
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return ActionResult.Failed($"{method} failed: {ex.Message}");
        }
    }

    private static bool IsUsable(AutomationElement element, out string blocker)
    {
        try
        {
            if (!element.Properties.IsEnabled.ValueOrDefault)
            {
                blocker = "the element is disabled";
                return false;
            }

            if (element.Properties.IsOffscreen.ValueOrDefault)
            {
                // Acting on an offscreen element is not automatically wrong (patterns
                // still work), but a mouse click would land somewhere else entirely,
                // so it is refused and the agent is told to scroll first.
                blocker = "the element is offscreen; scroll it into view first";
                return false;
            }
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            blocker = $"the element could not be read: {ex.Message}";
            return false;
        }

        blocker = string.Empty;
        return true;
    }

    private static bool IsInteropFailure(Exception ex) =>
        ex is COMException
            or TimeoutException
            or NotSupportedException
            or InvalidOperationException
            or UnauthorizedAccessException;

    private static string Quote(string text) =>
        text.Length <= 60 ? $"\"{text}\"" : $"\"{text[..60]}...\" ({text.Length} chars)";
}
