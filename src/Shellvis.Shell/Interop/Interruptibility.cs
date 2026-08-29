using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Shellvis.Shell.Interop;

/// <summary>Why this is not a good moment.</summary>
public enum BusyBecause
{
    /// <summary>It is a fine moment.</summary>
    NotBusy,

    /// <summary>Full screen: a presentation, a game, a video call, a remote session.</summary>
    FullScreen,

    /// <summary>Presentation mode: the user has told Windows they are presenting.</summary>
    Presenting,

    /// <summary>Focus assist, do not disturb, or the first hour after a fresh sign-in.</summary>
    QuietHours,

    /// <summary>Nobody is signed in at this session, or the screen is locked.</summary>
    Away,
}

/// <summary>
/// Whether the user can be told something right now.
///
/// <b>The problem this exists for.</b> An assistant that checks things on a timer is only
/// useful if it can say what it found, and only bearable if saying it never lands in the
/// middle of something. A window that appears while someone is writing a mail is the exact
/// failure: it steals the keystroke that was in flight, it covers the sentence being
/// written, and it trains the person to dismiss anything Shellvis shows without reading it.
/// After that, the notice that mattered is gone too.
///
/// <b>Windows already answers this question.</b> <c>SHQueryUserNotificationState</c> is the
/// documented way to ask whether this is a moment for an interruption, and it knows things
/// no heuristic here could: that the user has turned on focus assist, that a full-screen
/// application is running, that they have told Windows they are presenting. Guessing from
/// window titles or idle time would be a worse answer to a question the operating system
/// answers properly.
///
/// <b>What it deliberately does not know.</b> Whether the user is typing. There is no honest
/// way to ask that without watching keystrokes across every application, which is not
/// something this should be doing to answer "is now a good time". The conclusion drawn from
/// that is not to guess harder but to make the notice itself harmless: a mark that appears
/// on a bar the user already has on screen costs nothing to arrive at a bad moment, because
/// it takes no focus, covers nothing and makes no sound.
/// </summary>
public static class Interruptibility
{
    /// <summary>Why now is a bad moment, or <see cref="BusyBecause.NotBusy"/>.</summary>
    public static BusyBecause Ask()
    {
        if (!PInvoke.SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state).Succeeded)
        {
            // A failed query is treated as a fine moment, not as a bad one. The mark this
            // gates is passive, and suppressing it forever because an API call failed would
            // turn a missing answer into a silently broken assistant.
            return BusyBecause.NotBusy;
        }

        return state switch
        {
            QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
                or QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                or QUERY_USER_NOTIFICATION_STATE.QUNS_APP => BusyBecause.FullScreen,

            QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE => BusyBecause.Presenting,
            QUERY_USER_NOTIFICATION_STATE.QUNS_QUIET_TIME => BusyBecause.QuietHours,
            QUERY_USER_NOTIFICATION_STATE.QUNS_NOT_PRESENT => BusyBecause.Away,

            _ => BusyBecause.NotBusy,
        };
    }

    /// <summary>Whether anything at all may be shown now.</summary>
    public static bool AcceptsNotice() => Ask() == BusyBecause.NotBusy;

    /// <summary>What to say in the transcript about a notice that was held back.</summary>
    /// <remarks>
    /// Written down rather than swallowed. A held notice that is never mentioned is
    /// indistinguishable from an assistant that found nothing, and the difference matters:
    /// one of them still owes the user something.
    /// </remarks>
    public static string Explain(BusyBecause reason) => reason switch
    {
        BusyBecause.FullScreen => "held back while a full-screen application is in front",
        BusyBecause.Presenting => "held back while you are presenting",
        BusyBecause.QuietHours => "held back during focus assist",
        BusyBecause.Away => "held back while the session is locked",
        _ => string.Empty,
    };
}
