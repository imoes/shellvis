namespace Shellvis.Core.Desktop;

/// <summary>How a top-level window is currently displayed.</summary>
public enum WindowDisplayState
{
    Normal,
    Minimized,
    Maximized,
}

/// <summary>
/// A top-level desktop window, flattened to the facts an agent needs in order to
/// decide what to do next.
///
/// Deliberately a plain record rather than a live handle wrapper: the agent reasons
/// over a snapshot, and windows close between the snapshot and the action. Acting on
/// a stale <see cref="Handle"/> fails loudly, which is better than a wrapper that
/// silently resurrects a dead window.
/// </summary>
/// <param name="Handle">Native window handle. Only valid while the window lives.</param>
/// <param name="Title">Caption text. Often empty for tool windows.</param>
/// <param name="ClassName">Win32 class, useful for recognising app families.</param>
/// <param name="ProcessId">Owning process.</param>
/// <param name="ProcessName">Executable name without extension, best effort.</param>
/// <param name="Left">Screen X of the window rectangle, physical pixels.</param>
/// <param name="Top">Screen Y of the window rectangle, physical pixels.</param>
/// <param name="Width">Window width in physical pixels.</param>
/// <param name="Height">Window height in physical pixels.</param>
/// <param name="State">Minimized, maximized or normal.</param>
/// <param name="IsForeground">Whether this window currently has focus.</param>
public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ClassName,
    int ProcessId,
    string ProcessName,
    int Left,
    int Top,
    int Width,
    int Height,
    WindowDisplayState State,
    bool IsForeground)
{
    /// <summary>
    /// A short, stable-ish label for prompts and logs. Handles are meaningless to a
    /// language model, so lead with what a human would recognise.
    /// </summary>
    public override string ToString()
    {
        // The title is QUOTED and the process bracketed on purpose. An earlier version
        // rendered this as "Editor - Notepad", and a model duly passed that whole
        // string back as the window title, which of course matched nothing: the title
        // was only "Editor". Making the boundary explicit costs two characters and
        // removes a class of wasted round trips.
        string caption = string.IsNullOrWhiteSpace(Title) ? "(untitled)" : $"\"{Title}\"";
        string focus = IsForeground ? " [foreground]" : string.Empty;
        string state = State == WindowDisplayState.Normal ? string.Empty : $" [{State}]";
        return $"{caption} [{ProcessName}] {Width}x{Height} at {Left},{Top}{state}{focus}";
    }
}
