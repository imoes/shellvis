using Shellvis.Core.Agent;
using Shellvis.Core.Desk;

namespace Shellvis.Shell.Views;

/// <summary>
/// Having the model sort the unread mail, a batch at a time.
///
/// <b>Why this exists at all.</b> The desk sorted by sender, and the report was exact: "die
/// Mail TV muss nicht beantwortet werden, das ist einfach eine Information. Warum analysiert
/// die KI das nicht?" It did not because nothing asked it to. A sender is a fact about the
/// envelope; whether somebody is waiting is a judgement about the contents, and the only
/// thing here that reads contents is the model.
///
/// <b>Judged once, then free.</b> A verdict is a model call, and a turn on this machine takes
/// one to three minutes. Four hundred unread messages cannot be judged on every look -- but
/// they can be judged once each, ten at a time, with the answer kept in the desk store for as
/// long as the row lives. What that buys is a tray that is actually sorted rather than a tray
/// sorted by a regular expression.
///
/// <b>One pass per look, and never two at once.</b> The pass rides on the watcher's timer,
/// which already wakes every few minutes; a batch that is still running blocks the next one,
/// because two turns would queue on the same gate anyway and queueing them here keeps the
/// console honest about what is happening.
///
/// <b>The user's own turn always wins.</b> Nothing is judged while they are waiting for an
/// answer of their own: the model is one resource, and spending it on triage while somebody
/// is looking at a spinner is the wrong trade every time.
/// </summary>
public sealed partial class PillWindow
{
    private bool _judging;

    /// <summary>
    /// Judge one batch of unread mail, if there is one and nothing else is going on.
    /// </summary>
    private async Task JudgeSomeMailAsync()
    {
        if (_judging || _session is null || _session.Desk is not { } store)
            return;

        // Their turn first, always. IsBusy is the session's own flag rather than a second
        // one of mine -- two flags answering the same question is how they disagree.
        if (_session.IsBusy)
            return;

        _judging = true;

        try
        {
            DateTime since = (_session.DeskWindow ?? new DeskWindow()).Since(DateTime.Now);

            IReadOnlyList<DeskObject> batch = store.Unjudged(since, DeskTriage.PerBatch);

            if (batch.Count == 0)
                return;

            AddRow(
                GlyphTool,
                $"sorting {batch.Count} unread message(s): which of them needs an answer",
                "desk");

            var answer = new System.Text.StringBuilder();

            await _session.AskAsideAsync(
                DeskTriage.Ask(batch),
                agentEvent =>
                {
                    // Nothing is rendered. This is not a conversation and its answer is a
                    // table of labels: shown, it would be a page of noise every few minutes,
                    // and the verdicts are visible where they belong -- on the page, beside
                    // the mail they are about.
                    if (agentEvent is AgentEvent.AssistantMessage message)
                        answer.Append(message.Text);
                },
                CancellationToken.None).ConfigureAwait(true);

            IReadOnlyDictionary<string, (DeskVerdict Verdict, string Why)> verdicts =
                DeskTriage.Read(answer.ToString(), batch);

            foreach ((string id, (DeskVerdict verdict, string why)) in verdicts)
                store.Judge(id, verdict, why, DateTime.Now);

            // Said plainly, including when it comes to nothing. A pass that read no verdicts
            // out of a full answer is a broken format, not a quiet mailbox, and the two must
            // not look the same in the console.
            AddRow(
                verdicts.Count == 0 ? GlyphWarning : GlyphTool,
                verdicts.Count == 0
                    ? $"none of the {batch.Count} could be sorted; the answer did not carry verdicts"
                    : Summarise(verdicts),
                "desk",
                isWarning: verdicts.Count == 0);

            // The page, if it is open, now has something new to show. Refreshed here rather
            // than left to the next tick: a tray that fills three minutes after the sorting
            // finished looks like nothing happened.
            RefreshVorzimmer();
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"could not sort the unread mail: {ex.Message}", "desk", isWarning: true);
        }
        finally
        {
            _judging = false;
        }
    }

    /// <summary>What the batch came to, in one line.</summary>
    private static string Summarise(
        IReadOnlyDictionary<string, (DeskVerdict Verdict, string Why)> verdicts)
    {
        int answer = verdicts.Values.Count(v => v.Verdict == DeskVerdict.Answer);
        int information = verdicts.Values.Count(v => v.Verdict == DeskVerdict.Information);
        int ignore = verdicts.Values.Count(v => v.Verdict == DeskVerdict.Ignore);

        return $"sorted {verdicts.Count}: {answer} need an answer, "
            + $"{information} to know about, {ignore} not worth reading";
    }
}
