using System.Text.Json;

using Shellvis.Core.Office;

namespace Shellvis.Shell.Views;

/// <summary>
/// The reference page, the button that opens it, and the numbers on it.
///
/// <b>Why the application carries its own rules.</b> How this assistant sorts mail, when it
/// decides something is worth interrupting for, and what it will never do -- those are not
/// implementation details, they are the terms of the arrangement. They were written down in
/// three skill files and a prompt, which is the right place for the model to read them and
/// the wrong place for a person to. So they are also one page, in a window, behind a button
/// next to the answer.
///
/// <b>And the numbers on it are counted, never sorted.</b> The page shows how much is unread,
/// how much came from a person rather than a system, what starts today and what is late. It
/// does not show which mail needs an answer today, because nothing here can know that: the
/// three trays are a sorting somebody performs by reading, and a computed number under that
/// heading would be a guess wearing a triage's clothes. The page says so in as many words,
/// twice.
///
/// <b>A badge means "since you last opened this".</b> The comparison point is written to disk
/// when the window opens, so closing it and coming back an hour later shows what the hour
/// brought -- and a refresh while it is open keeps comparing against that same point, so the
/// badges accumulate rather than resetting every three minutes.
/// </summary>
public sealed partial class PillWindow
{
    private VorzimmerWindow? _vorzimmer;

    /// <summary>The desk as it was when this window was last opened.</summary>
    private DeskSnapshot? _deskBaseline;

    /// <summary>Whether a count is already in flight, so a timer cannot stack them up.</summary>
    private bool _counting;

    private static string DeskStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".shellvis",
        "desk.json");

    /// <summary>Open the reference page, or bring it back if it is already up.</summary>
    private void ShowVorzimmer()
    {
        bool first = _vorzimmer is null;

        if (_vorzimmer is null)
        {
            _vorzimmer = new VorzimmerWindow();

            // The button on the page. Subscribed once, when the window is made, so a second
            // open does not stack a second handler and count the desk twice.
            _vorzimmer.RefreshRequested += () => _ = CountTheDeskAsync(saveBaseline: false);
        }

        _vorzimmer.Reveal(WinRT.Interop.WindowNative.GetWindowHandle(this));

        // The stored baseline is read on the FIRST open of this session only. Later opens
        // keep the one in memory, so a window closed and reopened within a session does not
        // wipe the badges the session earned.
        if (first)
            _deskBaseline = LoadDesk();

        _ = CountTheDeskAsync(saveBaseline: true);
    }

    /// <summary>
    /// Refresh the numbers if the page is open, called from the watcher's own tick.
    ///
    /// Piggybacking on the watcher rather than running a timer of its own: it already looks
    /// at Outlook every three minutes, the COM apartment is single-threaded, and a second
    /// timer would mean two callers queueing behind each other for the same mailbox.
    /// </summary>
    private void RefreshVorzimmer()
    {
        if (_vorzimmer is null)
            return;

        _ = CountTheDeskAsync(saveBaseline: false);
    }

    /// <summary>
    /// Count the desk and hand it to the page.
    /// </summary>
    /// <param name="saveBaseline">
    /// True when this count establishes what "new" means from now on -- that is, when the
    /// window was just opened. A refresh must NOT save one: overwriting the comparison point
    /// on every tick would clear the badges three minutes after they appeared, which is
    /// exactly long enough for somebody to miss them.
    /// </param>
    private async Task CountTheDeskAsync(bool saveBaseline)
    {
        if (_vorzimmer is null)
            return;

        // Already counting: say nothing and do nothing. The count in flight ends in a
        // render, which is what puts the button back -- so the press is not lost, it is
        // answered by the other one.
        if (_counting)
            return;

        if (_session?.Outlook is null)
        {
            _vorzimmer.Trouble("Outlook ist nicht erreichbar");
            return;
        }

        _counting = true;

        try
        {
            DeskSnapshot desk = await _session.Outlook
                .TakeSnapshotAsync(DateTime.Now)
                .ConfigureAwait(true);

            _vorzimmer.Show(desk, _deskBaseline);

            if (saveBaseline)
                SaveDesk(desk);
        }
        catch (Exception ex)
        {
            // One line in the console and no further attempt. Outlook not running, or a
            // mailbox that has gone offline, is a normal state of the world; the page keeps
            // its dashes and the next tick tries again.
            AddRow(GlyphWarning, $"could not count the desk: {ex.Message}", "desk", isWarning: true);
            _vorzimmer?.Trouble("konnte nicht gezählt werden");
        }
        finally
        {
            _counting = false;
        }
    }

    private static DeskSnapshot? LoadDesk()
    {
        try
        {
            return File.Exists(DeskStatePath)
                ? JsonSerializer.Deserialize<DeskSnapshot>(File.ReadAllText(DeskStatePath), DeskFormat)
                : null;
        }
        catch (Exception)
        {
            // A file from an older shape, or a half-written one. Treated as "nothing known",
            // which suppresses badges for one opening rather than showing wrong ones.
            return null;
        }
    }

    private void SaveDesk(DeskSnapshot desk)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DeskStatePath)!);
            File.WriteAllText(DeskStatePath, JsonSerializer.Serialize(desk, DeskFormat));

            // Held in memory too, so the badges this session shows are measured from the
            // same point the next session will measure from.
            _deskBaseline ??= desk;
        }
        catch (Exception)
        {
            // Not being able to remember the comparison point costs badges, not correctness.
        }
    }

    private static readonly JsonSerializerOptions DeskFormat = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
