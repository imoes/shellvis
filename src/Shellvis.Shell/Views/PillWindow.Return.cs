using Shellvis.Core.Desk;
using Shellvis.Core.Office;
using Shellvis.Shell.Interop;

namespace Shellvis.Shell.Views;

/// <summary>
/// Coming back to the machine: one line about what needs you and what is next.
///
/// The rules for when it speaks live in <see cref="ReturnBriefing"/>, where a probe can reach
/// them. This part only notices the lock and the unlock, and makes sure the figures are fresh
/// before the line is composed: a count from before lunch would brief somebody about the
/// morning.
/// </summary>
public sealed partial class PillWindow
{
    private SessionWatch? _sessionWatch;

    private DateTime? _lockedAt;

    private DeskSnapshot? _deskAtLock;

    private AttentionWatch? _attentionWatch;

    /// <summary>
    /// Start recording which window is in front, for the attention_recent tool. Here beside
    /// the lock watch because both are the bar listening to Windows about the person.
    /// </summary>
    private void RegisterAttentionWatch()
    {
        _attentionWatch = new AttentionWatch(Core.Desktop.AttentionLog.Shared);

        if (!_attentionWatch.Start())
            AddRow(GlyphWarning, "Windows did not report window switches; \"the mail I just had open\" cannot be answered.", "attention");
    }

    private void RegisterSessionWatch()
    {
        _sessionWatch = new SessionWatch(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        _sessionWatch.Locked += OnSessionLocked;
        _sessionWatch.Unlocked += () => _ = OnSessionUnlockedAsync();

        if (!_sessionWatch.Start())
            AddRow(GlyphWarning, "Windows did not report the lock screen; there will be no briefing on return.", "session");
    }

    private void OnSessionLocked()
    {
        _lockedAt = DateTime.Now;
        _deskAtLock = _deskLast;
    }

    private async Task OnSessionUnlockedAsync()
    {
        if (_lockedAt is not { } lockedAt)
            return;

        TimeSpan away = DateTime.Now - lockedAt;
        DeskSnapshot? before = _deskAtLock;

        _lockedAt = null;
        _deskAtLock = null;

        if (away < ReturnBriefing.Threshold)
            return;

        // Counted again, without the sorting pass: the line has to arrive while the person is
        // still sitting down, and the sorting pass takes as long as the endpoint does. The
        // watcher's next look sorts what came in, and announces it if it needs an answer.
        try
        {
            await CountTheDeskAsync(saveBaseline: false, thenSort: false);
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"the desk could not be counted after the unlock: {ex.Message}", "session");
        }

        if (_deskLast is not { } now)
            return;

        string? line = ReturnBriefing.Compose(
            away, before, now, _tallyLast ?? DeskTally.Nothing, L("en", "de") == "de", DateTime.Now);

        if (line is null)
        {
            AddRow(GlyphTool, $"back after {away.TotalMinutes:F0} minutes; nothing worth a briefing.", "session");
            return;
        }

        NoteQuietly(line, "session", isProblem: false, headline: line, opens: NoticeOpens.Vorzimmer);
    }
}
