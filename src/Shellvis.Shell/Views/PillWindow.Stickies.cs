using Shellvis.Core.Notes;

namespace Shellvis.Shell.Views;

/// <summary>
/// The notes on the desktop, and keeping them in step with what is stored.
///
/// <b>Why the pill owns them.</b> A sticky note has no process of its own; it is a window
/// this application opens and has to close again. Somebody has to restore them at startup,
/// write down every move, and shut them when the application goes, and the pill is the one
/// object that lives for exactly as long as all of that needs to happen.
///
/// <b>Closing the application is not throwing a note away.</b> The two are told apart by
/// which path closed the window: the cross on the note discards it, everything else leaves
/// the record alone. Getting that backwards would mean a restart quietly clears the desktop,
/// which is the single worst thing a sticky note program can do.
/// </summary>
public sealed partial class PillWindow
{
    private readonly List<StickyWindow> _stickies = [];

    private bool _stickiesRestored;

    /// <summary>Put back what was on the desktop, once, when the application starts.</summary>
    private void RestoreStickies()
    {
        if (_stickiesRestored)
            return;

        _stickiesRestored = true;

        NoteStore? store = _session?.Notes;

        if (store is null)
            return;

        try
        {
            foreach (Sticky sticky in store.Stickies())
                Open(sticky);
        }
        catch (Exception ex)
        {
            // A desktop that comes back without its notes is a disappointment; a window that
            // will not open because of it is a fault. The notes are still in the database.
            AddRow(GlyphWarning, $"the desktop notes could not be restored: {ex.Message}",
                "notes", isWarning: true);
        }
    }

    /// <summary>
    /// Show a note that was just written.
    ///
    /// Called from the tool result rather than by the tool itself: the tool lives in Core
    /// and Core has no windows, which is the same split that keeps the Markdown parser
    /// testable and the approval gate replaceable.
    /// </summary>
    private void ShowNewStickies()
    {
        NoteStore? store = _session?.Notes;

        if (store is null)
            return;

        try
        {
            var known = _stickies.Select(w => w.Id).ToHashSet();

            foreach (Sticky sticky in store.Stickies())
            {
                if (!known.Contains(sticky.Id))
                    Open(sticky);
            }
        }
        catch (Exception)
        {
            // Nothing useful to say: the note is in the database and will appear at the next
            // start. Reporting it would be a warning about a window, not about the note.
        }
    }

    private void Open(Sticky sticky)
    {
        // A note restored at 0,0 has never been placed. Putting it under the pill rather
        // than in the screen corner means the user sees it appear where they were looking,
        // which is where a note being written for them belongs.
        Sticky placed = sticky.X == 0 && sticky.Y == 0
            ? sticky with { X = AppWindow.Position.X + 40, Y = Math.Max(40, AppWindow.Position.Y - 260) }
            : sticky;

        var window = new StickyWindow(placed, Save, Forget);

        _stickies.Add(window);
        window.Activate();
    }

    /// <summary>Write down what changed. Called on every keystroke, move and resize.</summary>
    private void Save(StickyWindow window)
    {
        try
        {
            _session?.Notes?.Update(
                window.Id,
                window.Text,
                window.Colour,
                window.X,
                window.Y,
                window.Width,
                window.Height);
        }
        catch (Exception)
        {
            // A write that fails costs the last edit, not the note. Reporting one per
            // keystroke would fill the console with the same line.
        }
    }

    /// <summary>A note's window has gone. Whether the note goes with it depends on why.</summary>
    private void Forget(StickyWindow window)
    {
        _stickies.Remove(window);

        if (!window.Discarded)
            return;

        try
        {
            _session?.Notes?.Unstick(window.Id);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Close every note window without touching what is stored.</summary>
    private void CloseStickies()
    {
        foreach (StickyWindow window in _stickies.ToList())
        {
            try
            {
                // Save first. A note being edited when the application closes should keep
                // what was typed, and the TextChanged handler has already written it, but
                // a position changed by the system border may not have arrived yet.
                Save(window);
                window.Close();
            }
            catch (Exception)
            {
                // A window already gone during shutdown is not worth propagating.
            }
        }

        _stickies.Clear();
    }
}
