using System.Text;

namespace Shellvis.Core.Desktop;

/// <summary>
/// One node of a desktop UI snapshot, addressable by a short reference.
///
/// The reference is the whole point. A language model cannot hold a UI Automation
/// runtime id or a screen coordinate in its head reliably, but it handles "@e12"
/// perfectly -- and a reference survives the round trip through a tool call, whereas
/// coordinates go wrong the moment the window moves. Every action tool therefore
/// takes a reference, never a position.
/// </summary>
/// <param name="Ref">Short handle such as "e12", unique within its snapshot.</param>
/// <param name="ControlType">UIA control type, e.g. Button, Edit, MenuItem.</param>
/// <param name="Name">Accessible name. The label a human would read.</param>
/// <param name="Value">Current value for editable or range controls, else null.</param>
/// <param name="IsEnabled">False for greyed-out controls, which cannot be actioned.</param>
/// <param name="IsOffscreen">True when scrolled or clipped out of view.</param>
/// <param name="Left">Screen X in physical pixels.</param>
/// <param name="Top">Screen Y in physical pixels.</param>
/// <param name="Width">Width in physical pixels.</param>
/// <param name="Height">Height in physical pixels.</param>
/// <param name="Actions">Supported interactions, e.g. Invoke, Toggle, SetValue.</param>
/// <param name="Children">Nested elements, already filtered and capped.</param>
public sealed record UiElement(
    string Ref,
    string ControlType,
    string Name,
    string? Value,
    bool IsEnabled,
    bool IsOffscreen,
    int Left,
    int Top,
    int Width,
    int Height,
    IReadOnlyList<string> Actions,
    IReadOnlyList<UiElement> Children)
{
    /// <summary>Whether this node can be acted on at all.</summary>
    public bool IsActionable => IsEnabled && !IsOffscreen && Actions.Count > 0;
}

/// <summary>
/// A captured view of one window's UI tree, plus the window it came from.
///
/// Snapshots are immutable and go stale: the app keeps running after the capture. The
/// reference map that resolves "@e12" back to a live element is held separately by
/// the analyzer, so a stale reference produces an explicit error rather than a click
/// on whatever happens to occupy that position now.
/// </summary>
public sealed record DesktopSnapshot(
    string SnapshotId,
    WindowInfo Window,
    UiElement Root,
    int ElementCount,
    bool WasTruncated)
{
    /// <summary>
    /// Render the tree as indented text for a model prompt.
    ///
    /// Compactness is the design goal: a raw UIA dump of a real application runs to
    /// tens of thousands of tokens, most of it structural containers with no name and
    /// no behaviour. One line per meaningful element keeps a full window under a few
    /// hundred lines.
    /// </summary>
    public string ToPromptText()
    {
        var sb = new StringBuilder();
        sb.Append("window: ").Append(Window).AppendLine();
        Render(sb, Root, depth: 0);

        if (WasTruncated)
        {
            sb.AppendLine(
                "... tree truncated. Narrow the scope with a specific element reference "
                + "instead of raising the cap.");
        }

        return sb.ToString();
    }

    private static void Render(StringBuilder sb, UiElement element, int depth)
    {
        sb.Append(' ', depth * 2)
          .Append('@').Append(element.Ref)
          .Append(' ').Append(element.ControlType);

        if (!string.IsNullOrWhiteSpace(element.Name))
            sb.Append(" \"").Append(element.Name).Append('"');

        if (element.Value is { Length: > 0 } value)
        {
            // Long values are the single biggest source of snapshot bloat: a text
            // editor's whole document arrives as one property.
            string shown = value.Length > 120 ? value[..120] + "..." : value;
            sb.Append(" value=\"").Append(shown).Append('"');
        }

        if (element.Actions.Count > 0)
            sb.Append(" [").Append(string.Join(',', element.Actions)).Append(']');

        if (!element.IsEnabled)
            sb.Append(" (disabled)");

        if (element.IsOffscreen)
            sb.Append(" (offscreen)");

        sb.Append(' ')
          .Append(element.Left).Append(',').Append(element.Top)
          .Append(' ').Append(element.Width).Append('x').Append(element.Height)
          .AppendLine();

        foreach (UiElement child in element.Children)
            Render(sb, child, depth + 1);
    }
}
