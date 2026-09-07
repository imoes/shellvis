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

        // Whether to go straight on to the next batch. Set only when the page is open and
        // this batch was full, which is as close to "there is more" as can be had without a
        // second query.
        bool keepGoing = false;

        try
        {
            // Everything held, not the remembering window: a message fifteen days old that
            // nobody has read still needs sorting, and bounding this by the window is what
            // left fifty-five of them unjudged for ever.
            DateTime since = DateTime.Now - store.Retention;

            IReadOnlyList<DeskObject> batch = store.Unjudged(since, DeskTriage.PerBatch);

            // Nothing new? Then go back over what was judged before the sorting could read
            // message bodies.
            //
            // Those verdicts are probably right and their reasons are useless: the model
            // saw sender and subject only, so the sentence beside each row is the subject
            // in other words. That sentence is the only thing on a row the assistant
            // contributes, and it is kept for three months -- so without this the trays
            // would go on showing paraphrased subject lines until they aged out. This is
            // what "bei Muss man wissen ist immer noch keine Zusammenfassung" was.
            //
            // Second, never first: a message nobody has judged at all is more urgent than
            // one whose reason could be better.
            bool rejudging = batch.Count == 0;

            if (rejudging)
                batch = store.JudgedWithoutBody(since, DeskTriage.PerBatch);

            if (batch.Count == 0)
                return;

            AddRow(
                GlyphTool,
                rejudging
                    ? $"re-reading {batch.Count} message(s) judged before Shellvis read "
                        + "message text, for a summary rather than a paraphrased subject"
                    : $"sorting {batch.Count} unread message(s): which of them needs an answer",
                "desk");

            // The page, if it is open, says so while it runs. Without this the cell read
            // "wird der Reihe nach beurteilt" whether a pass was in flight or not, and the
            // report was the obvious one: warum passiert da nichts?
            _vorzimmer?.Sorting(true, batch.Count);

            // The text of these ten, and only these ten.
            //
            // Without it the model sees sender and subject and nothing else, so the best
            // reason it can write is the subject in other words -- which is what "die
            // Tickets werden nicht zusammengefasst" was about. Fetched here rather than
            // stored during the walk because Body is the one expensive property on an
            // Outlook item: ten reads per pass, instead of two hundred for messages nobody
            // will ask about.
            var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (DeskObject one in batch)
            {
                if (one.EntryId is not { Length: > 0 } handle)
                    continue;

                string preview = await _session.Outlook
                    .PreviewBodyAsync(handle)
                    .ConfigureAwait(true);

                if (preview.Length > 0)
                    bodies[one.Id] = preview;
            }

            var answer = new System.Text.StringBuilder();

            await _session.AskAsideAsync(
                DeskTriage.Ask(batch, bodies),
                agentEvent =>
                {
                    // Nothing is rendered. This is not a conversation and its answer is a
                    // table of labels: shown, it would be a page of noise every few minutes,
                    // and the verdicts are visible where they belong -- on the page, beside
                    // the mail they are about.
                    if (agentEvent is AgentEvent.AssistantMessage message)
                        answer.Append(message.Text);
                },
                CancellationToken.None,

                // WITHOUT the tool catalogue, and that is what makes this work at all.
                // Sorting is a judgement about text that is already in the prompt -- there
                // is nothing here to go and look up. Offered the catalogue, the same call
                // carried a hundred and fourteen tool schemas and spent minutes on prompt
                // processing before the model could start, then was abandoned as stalled.
                withTools: false).ConfigureAwait(true);

            IReadOnlyDictionary<string, (DeskVerdict Verdict, string Why)> verdicts =
                DeskTriage.Read(answer.ToString(), batch);

            // sawBody records that THIS pass reads bodies, not that this message had one.
            // A notification with an empty body would otherwise be picked up as needing a
            // re-read on every pass, for ever.
            foreach ((string id, (DeskVerdict verdict, string why)) in verdicts)
                store.Judge(id, verdict, why, DateTime.Now, sawBody: true);

            // Said plainly, including when it comes to nothing. A pass that read no verdicts
            // out of a full answer is a broken format, not a quiet mailbox, and the two must
            // not look the same in the console.
            AddRow(
                verdicts.Count == 0 ? GlyphWarning : GlyphTool,
                verdicts.Count == 0
                    // With the shape the model actually used, because without it the line
                    // says only that something went wrong. The parser tolerates pipes,
                    // markdown tables and no separator at all; anything it still cannot
                    // read is a shape worth seeing rather than guessing at.
                    ? $"none of the {batch.Count} could be sorted; the model answered: "
                        + FirstLine(answer.ToString())
                    : Summarise(verdicts),
                "desk",
                isWarning: verdicts.Count == 0);

            // The page, if it is open, now has something new to show. Refreshed here rather
            // than left to the next tick: a tray that fills three minutes after the sorting
            // finished looks like nothing happened.
            RefreshVorzimmer();

            // Straight on to the next batch WHILE SOMEBODY IS WATCHING.
            //
            // A backlog of fifty-five takes six passes, and six passes at one every few
            // minutes is half an hour of a page that says "noch unsortiert" and appears
            // idle. With the page open the queue is worked down as fast as the model
            // manages; with it closed the watcher's tick sips one batch at a time, so
            // nothing holds the model for half an hour unasked. Their own turn still wins:
            // the check at the top of this method runs again for every batch.
            keepGoing = _vorzimmer is not null && batch.Count >= DeskTriage.PerBatch;
        }
        catch (Exception ex)
        {
            AddRow(GlyphWarning, $"could not sort the unread mail: {ex.Message}", "desk", isWarning: true);
        }
        finally
        {
            _judging = false;
            _vorzimmer?.Sorting(false, 0);
        }

        // After the flag is cleared, or the next call would meet its own guard and stop.
        if (keepGoing)
            _ = JudgeSomeMailAsync();
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
