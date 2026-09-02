namespace Shellvis.Core.Desktop;

/// <summary>Where in the z-order a bar belongs.</summary>
public enum BandPosition
{
    /// <summary>In the topmost band, where the taskbar lives.</summary>
    Topmost,

    /// <summary>At the bottom of the z-order, which is where the taskbar goes too.</summary>
    Bottom,
}

/// <summary>
/// Keeping the bar on the taskbar's own level, by the taskbar's own rules.
///
/// <b>What replaced what.</b> The first version of this decided for itself when to get out of
/// the way: it polled the foreground window every seven tenths of a second, measured whether
/// it covered its monitor, asked <c>SHQueryUserNotificationState</c>, and compared the process
/// name against a list of remote desktop clients kept in the configuration. Every part of that
/// was a guess about what Windows was going to do, and it guessed wrong in both directions --
/// the bar vanished behind ordinary windows it had decided to yield to, and it still appeared
/// over a remote desktop connection that none of the three rules recognised.
///
/// <b>Windows already has an answer, and it is a documented one.</b> A window that wants to
/// behave like part of the taskbar registers as an <i>application desktop toolbar</i> with
/// <c>SHAppBarMessage</c>/<c>ABM_NEW</c>, and the shell then tells it what is happening:
/// <c>ABN_FULLSCREENAPP</c> when a full-screen application opens or the last one closes,
/// <c>ABN_POSCHANGED</c> when the taskbar moves or resizes, <c>ABN_STATECHANGE</c> when the
/// taskbar's own always-on-top and autohide state changes. This is the same notification the
/// shell uses to get the taskbar itself out of the way, so a bar that follows it is on the
/// taskbar's level by construction rather than by imitation.
///
/// The undocumented alternative was checked and does not exist: z-order <i>bands</i>
/// (<c>CreateWindowInBand</c>, <c>SetWindowBand</c>) accept only the band an ordinary window
/// already has, and every other band fails with access denied unless the process holds a
/// UIAccess token. So an appbar is not a workaround for the real mechanism. It is the
/// mechanism.
///
/// This class is the part with no Win32 in it, so the harness can drive the whole sequence.
/// </summary>
public sealed class TaskbarBand
{
    // The appbar notification codes, from shellapi.h. Here rather than in the interop layer
    // because they are what this state machine reacts to, and the harness needs them.
    public const uint StateChange = 0x0;
    public const uint PositionChanged = 0x1;
    public const uint FullScreenApp = 0x2;
    public const uint WindowArrange = 0x3;

    /// <summary>Whether a full-screen application currently has the screen.</summary>
    public bool FullScreenAppOpen { get; private set; }

    /// <summary>
    /// Where the bar belongs right now.
    ///
    /// <b><c>ABS_ALWAYSONTOP</c> is deliberately not consulted</b>, and that is the one place
    /// this departs from the sample code in the documentation. The sample reads the taskbar's
    /// state and goes topmost only if the always-on-top flag is set -- which was right in 1996,
    /// when the taskbar had a check box for it. That check box is gone: since Windows 8 the
    /// taskbar is always on top and there is nothing to read, and <c>ABM_GETSTATE</c> can
    /// return zero on a machine whose taskbar is plainly in front of everything. Following the
    /// sample literally therefore drops the bar out of the topmost band on a modern desktop --
    /// and because the docked bar sits ON the taskbar strip, out of the band means underneath
    /// Shell_TrayWnd, which means gone. That is the reported disappearance, arrived at by
    /// doing what the documentation says.
    ///
    /// So the only thing that moves the bar is a full-screen application, which is also the
    /// only thing that moves the taskbar.
    /// </summary>
    public BandPosition Position => FullScreenAppOpen ? BandPosition.Bottom : BandPosition.Topmost;

    /// <summary>
    /// Why the bar is where it is, or null when it is in its ordinary place.
    ///
    /// Worth saying out loud in the console: a bar that drops behind a full-screen application
    /// without explanation is reported as "Shellvis disappeared", which is exactly how this
    /// arrived in the first place.
    /// </summary>
    public string? Reason => FullScreenAppOpen
        ? "A full-screen application has the screen, so Shellvis has stepped behind it -- the "
            + "taskbar does the same. Ctrl+Alt+Space brings it back."
        : null;

    /// <summary>
    /// What to say about the move that just happened, in either direction.
    ///
    /// <b>Both directions, and the return is the half that was missing.</b> The first version
    /// only spoke when the bar stepped aside, so the console filled with "Shellvis has stepped
    /// behind it" and never once said it had come back. A reader watching that has been told
    /// their bar disappeared twice and returned never -- which is the complaint that started
    /// this whole piece of work, produced by the reporting rather than by the behaviour.
    /// </summary>
    public string Moved => FullScreenAppOpen
        ? Reason!
        : "The full-screen application has closed, so Shellvis is back on the taskbar's level.";

    /// <summary>
    /// Fold in one appbar notification.
    ///
    /// Returns true when the bar's place in the z-order changed, so the caller can act only
    /// when there is something to do. <paramref name="flag"/> is the notification's lParam,
    /// which for <c>ABN_FULLSCREENAPP</c> means "opening" rather than "closing".
    /// </summary>
    public bool Apply(uint notification, bool flag)
    {
        BandPosition before = Position;

        if (notification == FullScreenApp)
            FullScreenAppOpen = flag;

        // Everything else -- the taskbar moving, being restyled, the user tiling windows --
        // leaves the bar's BAND alone. ABN_POSCHANGED changes where a docked bar sits, which
        // is a placement question and handled by the window, not a z-order question.
        return Position != before;
    }

    /// <summary>Forget the full-screen state, for when the registration is torn down and remade.</summary>
    public void Reset() => FullScreenAppOpen = false;
}
