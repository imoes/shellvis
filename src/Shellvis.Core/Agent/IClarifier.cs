namespace Shellvis.Core.Agent;

/// <summary>One thing the user can pick.</summary>
/// <param name="Label">One to five words. What the choice is.</param>
/// <param name="Description">
/// What the choice means or costs. Not decoration: a list of bare labels asks the user to
/// guess the consequence of each, which is the work they were being asked to help with.
/// </param>
public sealed record ClarifyOption(string Label, string Description);

/// <summary>A question with a small set of answers.</summary>
/// <param name="Question">The full question, ending in a question mark.</param>
/// <param name="Header">A chip of at most twelve characters, naming the decision.</param>
/// <param name="Options">Two to four choices. "Other" is added by the surface, not here.</param>
/// <param name="MultiSelect">Whether the choices can be combined.</param>
public sealed record ClarifyRequest(
    string Question,
    string Header,
    IReadOnlyList<ClarifyOption> Options,
    bool MultiSelect);

/// <summary>What came back.</summary>
/// <param name="Chosen">Labels the user picked, empty when they typed their own answer.</param>
/// <param name="Other">Free text, when the user chose to write instead of pick.</param>
/// <param name="Answered">
/// Whether a person actually answered. False for a timeout, a cancel, or a scheduled run
/// where nobody is present -- and the difference matters, which is why it is not folded into
/// an empty result.
/// </param>
public sealed record ClarifyAnswer(
    IReadOnlyList<string> Chosen,
    string? Other,
    bool Answered)
{
    public static ClarifyAnswer NotAnswered { get; } = new([], null, false);
}

/// <summary>
/// Asks the user a question with a few concrete options.
///
/// <b>Why this exists.</b> An agent that cannot ask has two bad choices when a request is
/// ambiguous: guess, or stop and say it is confused. Guessing wastes the work; stopping
/// wastes the person. A short question with two to four options and a way to write something
/// else costs seconds and settles it.
///
/// <b>Why an interface.</b> The same reason as <see cref="IApprovalGate"/>: the loop and the
/// tools stay testable, a scheduled run substitutes <see cref="NobodyHome"/>, and the shell
/// is the only thing that knows what a dialog is.
///
/// <b>The rule that is not in the type.</b> Asking is for decisions that belong to the user
/// and that change what happens next. Not for what can be read out of the code, and not for
/// "shall I continue?". An agent that asks about everything gets clicked through unread, in
/// exactly the way an approval prompt that fires too often does -- this project has already
/// argued that through once, for the read-only classifier.
/// </summary>
public interface IClarifier
{
    Task<ClarifyAnswer> AskAsync(ClarifyRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The clarifier for a run with no human in front of it.
///
/// A scheduled job at three in the morning has nobody to answer, and blocking on a dialog
/// would hang the job until its timeout. So it answers immediately and truthfully, and the
/// tool turns that into an instruction to proceed on a stated assumption -- the same shape as
/// <c>DenyEverythingGate</c>, which refuses approvals in scheduled runs for the same reason.
/// </summary>
public sealed class NobodyHome : IClarifier
{
    public Task<ClarifyAnswer> AskAsync(ClarifyRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ClarifyAnswer.NotAnswered);
}

/// <summary>
/// Routes a question to a person, or turns it down when nobody is there.
///
/// <b>Why a router and not two registrations.</b> A scheduled run shares this
/// application's tool registry with the interactive one, deliberately: a second
/// PowerShell runspace would cost a second of startup and a copy of the SDK in memory
/// for something that runs once an hour. But that sharing means the <c>clarify</c> tool
/// is the same instance in both, so the decision cannot be made when it is registered.
/// It is made per call, from a flag the session flips while a job runs.
///
/// The two loops are serialised against each other through one gate, so the flag cannot
/// be true and false at the same moment.
/// </summary>
public sealed class UnattendedClarifier(IClarifier attended, Func<bool> unattended) : IClarifier
{
    public Task<ClarifyAnswer> AskAsync(ClarifyRequest request, CancellationToken cancellationToken) =>
        unattended()
            ? Task.FromResult(ClarifyAnswer.NotAnswered)
            : attended.AskAsync(request, cancellationToken);
}
