namespace Shellvis.Core.Desk;

/// <summary>
/// What should happen with one thing on the desk.
///
/// <b>Three outcomes, and the middle one is the one that was missing.</b> The page sorted mail
/// by who sent it -- a person or a system -- and that is not the question. A colleague can
/// send something that needs no answer at all ("die Mail TV muss nicht beantwortet werden,
/// das ist einfach eine Information"), and a system can send something that does. Sender is a
/// fact about the envelope; this is a judgement about the contents, and only a reader can
/// make it.
///
/// <b>So a model makes it, once per message, and it is remembered.</b> Not a pattern, not a
/// list of addresses: the earlier attempt was exactly that, and it put a broadcast under
/// "braucht heute eine Antwort" because the sender happened to be a human being.
/// </summary>
public enum DeskVerdict
{
    /// <summary>Somebody is waiting. This is the tray that costs attention.</summary>
    Answer,

    /// <summary>Worth knowing, nothing to send back. Most of a working inbox.</summary>
    Information,

    /// <summary>Not worth the reading either: a broadcast, a bounce, a duplicate.</summary>
    Ignore,
}

/// <summary>
/// How the unread mail of a period divides up, and how much has not been looked at yet.
/// </summary>
/// <param name="Answer">Somebody is waiting.</param>
/// <param name="Information">Worth knowing, nothing to answer.</param>
/// <param name="Ignore">Not worth either.</param>
/// <param name="Pending">
/// Judged by nothing yet.
///
/// Reported rather than folded into one of the others, and that is the honest half of this
/// type: judging costs a model call each, so a busy morning arrives faster than it can be
/// read. A page that quietly counted the unjudged as "information" would look complete and
/// be wrong; one that says how many are still waiting to be looked at tells the truth about
/// what it knows.
/// </param>
public sealed record DeskTally(int Answer, int Information, int Ignore, int Pending)
{
    /// <summary>Nothing counted yet.</summary>
    public static DeskTally Nothing { get; } = new(0, 0, 0, 0);

    /// <summary>How many unread messages the tally covers.</summary>
    public int Total => Answer + Information + Ignore + Pending;
}
