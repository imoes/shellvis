using Shellvis.Core.Agent;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Checks the question tool without a window.
///
/// <b>What can be checked here, and what cannot.</b> The dialog itself is WinUI and needs
/// a XAML root, so it is out of reach of a console harness. What is in reach is everything
/// that decides <i>whether</i> and <i>with what</i> a person is asked, and that is where a
/// mistake would be expensive: a question with one option is not a question, a question
/// nobody can answer must not hold up a scheduled job, and the difference between "the
/// user declined" and "nobody was there" has to survive into the text the model reads.
/// Getting that last one wrong makes the model give up where it should have carried on
/// with a stated assumption.
///
/// The clarifier is a recording fake, so the probe also sees exactly what the surface
/// would have been handed.
/// </summary>
internal static class ClarifyProbe
{
    /// <summary>A clarifier that records what it was asked and answers as told.</summary>
    private sealed class Recorder(ClarifyAnswer answer) : IClarifier
    {
        public ClarifyRequest? Last { get; private set; }

        public int Calls { get; private set; }

        public Task<ClarifyAnswer> AskAsync(ClarifyRequest request, CancellationToken cancellationToken)
        {
            // The token is honoured, because a fake that ignores it would let the
            // cancellation check below pass without the cancellation path ever running.
            cancellationToken.ThrowIfCancellationRequested();

            Last = request;
            Calls++;
            return Task.FromResult(answer);
        }
    }

    /// <summary>A clarifier whose surface is broken.</summary>
    private sealed class Broken : IClarifier
    {
        public Task<ClarifyAnswer> AskAsync(ClarifyRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no XamlRoot");
    }

    public static async Task<int> RunAsync()
    {
        int failures = 0;

        void Check(string what, bool passed, string detail = "")
        {
            if (!passed)
                failures++;

            Console.WriteLine($"  {(passed ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        Console.WriteLine("clarify: the shapes a question may take\n");

        // Two options is the smallest real choice.
        var picked = new Recorder(new ClarifyAnswer(["Keep it local"], null, true));
        var tools = new ClarifyTools(picked);

        string two = await tools.Clarify(
            "Where should the recognition run?",
            ["Keep it local", "Use the hosted recogniser"],
            header: "Speech",
            descriptions: ["Slower, nothing leaves the machine", "Faster, audio leaves the machine"])
            .ConfigureAwait(false);

        Check("two options reach the surface", picked.Last?.Options.Count == 2);
        Check("the chosen label comes back", two.Contains("Keep it local", StringComparison.Ordinal), two);
        Check("descriptions are carried, not dropped",
            picked.Last?.Options[1].Description.Contains("audio leaves", StringComparison.Ordinal) == true);

        // The surface adds "something else"; the tool must never fabricate one, or the
        // list the user sees would not be the list the model wrote.
        Check("the tool invents no extra option",
            picked.Last?.Options.All(o =>
                !o.Label.Contains("else", StringComparison.OrdinalIgnoreCase)) == true);

        // One option is a statement, not a choice.
        string one = await tools.Clarify("Shall I go on?", ["Yes"]).ConfigureAwait(false);
        Check("one option is refused", one.StartsWith("error:", StringComparison.Ordinal), one);

        string none = await tools.Clarify("What now?", []).ConfigureAwait(false);
        Check("no options is refused", none.StartsWith("error:", StringComparison.Ordinal));

        string blank = await tools.Clarify("   ", ["a", "b"]).ConfigureAwait(false);
        Check("an empty question is refused", blank.StartsWith("error:", StringComparison.Ordinal));

        // Blank entries do not count towards the two.
        string padded = await tools.Clarify("Which?", ["a", "  ", ""]).ConfigureAwait(false);
        Check("blank options do not pad the count",
            padded.StartsWith("error:", StringComparison.Ordinal), padded);

        // Beyond four it stops being a decision and becomes a menu to study.
        var many = new Recorder(new ClarifyAnswer(["one"], null, true));
        string capped = await new ClarifyTools(many)
            .Clarify("Which?", ["one", "two", "three", "four", "five", "six"])
            .ConfigureAwait(false);

        Check("more than four are capped at four", many.Last?.Options.Count == 4);
        Check("the capping is said out loud, not hidden",
            capped.Contains("first 4 of 6", StringComparison.Ordinal), capped);

        // A header is a chip, not a sentence.
        var chip = new Recorder(new ClarifyAnswer(["a"], null, true));
        await new ClarifyTools(chip).Clarify(
            "Which?", ["a", "b"], header: "Which provider should we reach for here")
            .ConfigureAwait(false);

        Check("a long header is cut to a chip", chip.Last?.Header.Length == 12, chip.Last?.Header ?? "");

        var noChip = new Recorder(new ClarifyAnswer(["a"], null, true));
        await new ClarifyTools(noChip).Clarify("Which?", ["a", "b"]).ConfigureAwait(false);
        Check("a missing header gets a default", (noChip.Last?.Header.Length ?? 0) > 0, noChip.Last?.Header ?? "");

        Console.WriteLine("\nwhat comes back:");

        // Free text wins over the labels: the user was never limited to the offer.
        var wrote = new Recorder(new ClarifyAnswer([], "Use the one on port 8081", true));
        string written = await new ClarifyTools(wrote).Clarify("Which?", ["a", "b"]).ConfigureAwait(false);
        Check("free text is passed through verbatim",
            written.Contains("Use the one on port 8081", StringComparison.Ordinal), written);

        // The important distinction in the whole feature.
        var silent = new Recorder(ClarifyAnswer.NotAnswered);
        string quiet = await new ClarifyTools(silent).Clarify("Which?", ["a", "b"]).ConfigureAwait(false);

        Check("no answer is not read as a refusal",
            quiet.Contains("nobody answered", StringComparison.Ordinal), quiet);
        Check("no answer tells the model to carry on with a stated assumption",
            quiet.Contains("Decide yourself", StringComparison.Ordinal));
        Check("no answer is not an error",
            !quiet.StartsWith("error:", StringComparison.Ordinal));

        // A broken surface is the same situation as a timeout, not a crash.
        string broken = await new ClarifyTools(new Broken()).Clarify("Which?", ["a", "b"]).ConfigureAwait(false);
        Check("a surface that throws becomes text, not an exception",
            broken.Contains("Proceed on your best", StringComparison.Ordinal), broken);

        Console.WriteLine("\nnobody is here:");

        // A scheduled run has no one to ask, so it must not open anything.
        var wouldAsk = new Recorder(new ClarifyAnswer(["a"], null, true));
        bool unattended = true;

        var routed = new ClarifyTools(new UnattendedClarifier(wouldAsk, () => unattended));

        string scheduled = await routed.Clarify("Which?", ["a", "b"]).ConfigureAwait(false);

        Check("a scheduled run never reaches the surface", wouldAsk.Calls == 0);
        Check("a scheduled run gets the refusal immediately",
            scheduled.Contains("nobody answered", StringComparison.Ordinal), scheduled);

        // And the same registration asks normally once the job is over. This is the part
        // that would break if the choice were made when the tool was registered: the
        // registry is shared between the scheduled loop and the interactive one.
        unattended = false;
        string interactive = await routed.Clarify("Which?", ["a", "b"]).ConfigureAwait(false);

        Check("the same tool asks again when someone is back", wouldAsk.Calls == 1);
        Check("and returns their choice", interactive.Contains("the user chose: a", StringComparison.Ordinal));

        // A cancelled turn is neither an answer nor a crash.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);

        string aborted = await new ClarifyTools(new Recorder(ClarifyAnswer.NotAnswered))
            .Clarify("Which?", ["a", "b"], cancellationToken: cancelled.Token)
            .ConfigureAwait(false);

        Check("a cancelled turn is reported as cancelled, not as a refusal",
            aborted.Contains("cancelled along with the turn", StringComparison.Ordinal), aborted);

        Console.WriteLine("\nin the catalog:");

        var registry = new ToolRegistry();
        registry.RegisterFrom(new ClarifyTools(new Recorder(ClarifyAnswer.NotAnswered)));

        ToolEntry? entry = registry.Tools.FirstOrDefault(t => t.Name == "clarify");

        Check("clarify is registered", entry is not null);
        Check("clarify is read-only, because asking changes nothing",
            entry?.SideEffect == SideEffect.ReadOnly, entry?.SideEffect.ToString() ?? "");

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: questions are shaped, capped and refused as intended, and an\n"
                + "unattended run is told nobody is there instead of being made to wait.\n"
                + "NOT checked here: the dialog itself, which needs a XAML root."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }
}
