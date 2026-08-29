using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

using Shellvis.Shell.Interop;

namespace Shellvis.Shell.Views;

/// <summary>
/// Telling the user something without interrupting them.
///
/// <b>The rule this is built around.</b> A window that appears while someone is writing a
/// mail is the worst thing a scheduled assistant can do. It takes the keystroke that was in
/// flight, covers the sentence being written, and teaches the person to dismiss anything
/// Shellvis shows without reading it -- after which the notice that mattered is gone too.
/// So: nothing opens, nothing takes focus, nothing makes a sound, and nothing moves.
///
/// <b>What replaces it.</b> A dot on the bar the user already has on screen, and a count in
/// the tray tooltip. Passive: it costs nothing to arrive at a bad moment, because it covers
/// nothing and interrupts nothing. Whoever is busy will see it in their own time, which is
/// the entire point.
///
/// <b>The trade being made, said plainly.</b> Something this quiet can be missed. That is
/// the correct direction for this: a missed reminder costs one reminder, while an
/// interruption at the wrong moment costs every future one, because the user learns to
/// ignore them. The loud channels stay available for things the user asked for directly.
///
/// <b>Held, not dropped.</b> While Windows says this is not a moment for notices -- a
/// presentation, a full-screen call, focus assist -- even the dot waits. It is a change on a
/// screen that may be shared, and a mark that appeared during a presentation would be
/// noticed by the wrong people and forgotten by the right one. The items are kept and the
/// mark appears when the moment passes.
/// </summary>
public sealed partial class PillWindow
{
    /// <summary>How often the held items are offered again.</summary>
    /// <remarks>
    /// Slow on purpose. Nothing here is urgent enough to be worth a fast timer, and the
    /// question being asked -- "is the user still presenting?" -- does not change in
    /// seconds.
    /// </remarks>
    private static readonly TimeSpan QuietRetry = TimeSpan.FromSeconds(20);

    private readonly List<string> _unread = [];

    private DispatcherQueueTimer? _quietTimer;

    /// <summary>Why the mark is currently being withheld, so it is said once and not repeatedly.</summary>
    private BusyBecause _heldReason = BusyBecause.NotBusy;

    /// <summary>Whether something is waiting that the user has not seen.</summary>
    private bool HasUnread => _unread.Count > 0;

    /// <summary>
    /// Say something without interrupting.
    ///
    /// The transcript always gets the line: the console is the record, and a scheduled run
    /// that touched the machine invisibly is what this whole console exists to prevent. What
    /// is gated is only the MARK, which is the part the user notices.
    /// </summary>
    private void NoteQuietly(string text, string trailing, bool isProblem)
    {
        AddRow(isProblem ? GlyphWarning : GlyphTool, text, trailing, isWarning: isProblem);

        // Seen already: the console is open in front of them, so a dot saying "there is
        // something in the console" would be pointing at what they are reading.
        if (_consoleOpen && !_docked)
        {
            ScrollToEnd();
            return;
        }

        _unread.Add(text);
        Offer();
    }

    /// <summary>Show the mark if this is a moment for it; otherwise wait and try again.</summary>
    private void Offer()
    {
        if (!HasUnread)
            return;

        BusyBecause busy = Interruptibility.Ask();

        if (busy != BusyBecause.NotBusy)
        {
            // Said once per spell, not once per item. A held notice that is never mentioned
            // is indistinguishable from an assistant that found nothing, and the difference
            // matters: one of them still owes the user something. Repeating it every twenty
            // seconds would be its own kind of nagging.
            if (_heldReason != busy)
            {
                _heldReason = busy;
                AddRow(GlyphSpeaker, Interruptibility.Explain(busy), "quiet", isAnnouncement: true);
            }

            StartQuietTimer();
            return;
        }

        _heldReason = BusyBecause.NotBusy;

        StopQuietTimer();

        UnreadDot.Visibility = Visibility.Visible;

        // The count goes in the tooltip rather than on the dot. A number rendered on a bar
        // this size is four pixels of glyph nobody can read, and the tooltip is where a
        // Windows tray icon has always said this sort of thing.
        _tray?.UpdateTooltip(TrayText());
    }

    private void StartQuietTimer()
    {
        if (_quietTimer is not null)
            return;

        _quietTimer = DispatcherQueue.CreateTimer();
        _quietTimer.Interval = QuietRetry;
        _quietTimer.Tick += (_, _) => Offer();
        _quietTimer.Start();
    }

    private void StopQuietTimer()
    {
        _quietTimer?.Stop();
        _quietTimer = null;
    }

    /// <summary>
    /// The user has looked. Clear the mark.
    ///
    /// Called when the console is opened, by whatever route: the chevron, the tray, the
    /// hotkey. Opening the console IS reading them, because the lines are already in it.
    /// </summary>
    private void MarkRead()
    {
        if (!HasUnread)
            return;

        _unread.Clear();
        StopQuietTimer();

        UnreadDot.Visibility = Visibility.Collapsed;
        _tray?.UpdateTooltip(TrayText());
    }

    private string TrayText()
    {
        const string Base = "Shellvis - Ctrl+Alt+Space to show, Ctrl+Alt+D to dictate";

        return _unread.Count == 0
            ? Base
            : $"Shellvis - {_unread.Count} new since you last looked\n{Base}";
    }
}
