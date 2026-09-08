using System.Diagnostics;

using Microsoft.Extensions.AI;

using Shellvis.Core.Config;
using Shellvis.Core.Desk;
using Shellvis.Core.Providers;

namespace Shellvis.DesktopProbe;

/// <summary>
/// One real sorting pass, against the real store and the real model, with no tools.
///
/// <b>Why this exists.</b> The sorting reported "none of the 10 could be sorted; the model
/// answered: (no output)" and three different things could have caused it: a prompt shape the
/// parser could not read, a model that says nothing, or a call that never got far enough to
/// produce anything. The parser is pinned by the pure checks in <c>probe desk</c>; this
/// separates the other two, and it does it the way the application does -- the same prompt
/// builder, the same absence of tools, the same parser.
///
/// <b>The measurement that made this necessary.</b> Prompt processing on this estate's
/// endpoint runs at roughly 88 tokens a second: 17,650 tokens took 201 seconds. The tool
/// catalogue is around a hundred and fourteen schemas, so a sorting pass that carried it
/// spent minutes before the model could start and was abandoned as stalled. Sorting needs no
/// tools at all -- the text to judge is in the prompt -- and this proves it works without
/// them rather than assuming it.
///
/// Nothing is written. The verdicts are printed, not stored: a harness that judged the real
/// mailbox would be a harness with an opinion about somebody's inbox.
/// </summary>
internal static class TriageProbe
{
    public static async Task<int> RunAsync(bool withBodies, bool write = false)
    {
        Console.WriteLine(write
            ? "triage: one real sorting pass, no tools, VERDICTS WILL BE STORED\n"
            : "triage: one real sorting pass, no tools, nothing written\n");

        ShellvisConfig settings = ConfigStore.Load().Config;

        if (ProviderResolver.Find(settings.Model.Provider, settings) is not { } profile)
        {
            Console.WriteLine("no provider configured; nothing to ask.");
            return 1;
        }

        using var store = new DeskStore();

        DateTime since = DateTime.Now - store.Retention;
        IReadOnlyList<DeskObject> batch = store.Unjudged(since, DeskTriage.PerBatch);

        // The same order the application uses: what nobody has judged first, then what was
        // judged before the sorting could read message text.
        bool rejudging = batch.Count == 0;

        if (rejudging)
            batch = store.JudgedWithoutBody(since, DeskTriage.PerBatch);

        if (batch.Count == 0)
        {
            Console.WriteLine("nothing unjudged and nothing to re-read. Sorting has caught up.");
            return 0;
        }

        Console.WriteLine(rejudging
            ? $"{batch.Count} to re-read of {store.WithoutBodyCount(since)} judged without "
                + "their text"
            : $"{batch.Count} unjudged, oldest {batch[^1].When:dd.MM. HH:mm}");

        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);

        if (withBodies)
        {
            // The same fetch the application does, and the same reason: without the text the
            // model can only paraphrase the subject, which is what "die Tickets werden nicht
            // zusammengefasst" was about.
            using var apartment = new Shellvis.Core.Office.ComApartment();
            var outlook = new Shellvis.Core.Office.OutlookClient(apartment);

            foreach (DeskObject one in batch)
            {
                if (one.EntryId is not { Length: > 0 } handle)
                    continue;

                string preview = await outlook.PreviewBodyAsync(handle).ConfigureAwait(false);

                if (preview.Length > 0)
                    bodies[one.Id] = preview;
            }

            Console.WriteLine($"{bodies.Count} of them still had a body to read");
        }

        string prompt = DeskTriage.Ask(batch, bodies.Count > 0 ? bodies : null);

        // Characters, not tokens, because nothing here can tokenise -- but the ratio is
        // stable enough to say whether a prompt is a page or a book.
        Console.WriteLine($"prompt: {prompt.Length} characters, ~{prompt.Length / 4} tokens\n");

        // thinking: false, exactly as the application's sorting pass asks. It is the whole
        // difference between a pass taking seconds and taking eight minutes, so measuring
        // it any other way would measure something nobody runs.
        IChatClient client = ChatClientFactory.Create(
            profile, settings.Model.Model, requestTimeoutSeconds: 300, thinking: false);

        var options = new ChatOptions();
        var said = new System.Text.StringBuilder();
        var clock = Stopwatch.StartNew();

        try
        {
            await foreach (ChatResponseUpdate update in client
                .GetStreamingResponseAsync(
                    [new ChatMessage(ChatRole.User, prompt)],
                    options)
                .ConfigureAwait(false))
            {
                said.Append(update.Text);
            }
        }
        catch (Exception failure)
        {
            Console.WriteLine($"FAIL the call did not complete: {failure.Message}");
            return 1;
        }

        clock.Stop();

        string answer = said.ToString();

        Console.WriteLine($"answered in {clock.Elapsed.TotalSeconds:F1}s, "
            + $"{answer.Length} characters\n");

        IReadOnlyDictionary<string, (DeskVerdict Verdict, string Why)> verdicts =
            DeskTriage.Read(answer, batch);

        foreach (DeskObject one in batch)
        {
            if (verdicts.TryGetValue(one.Id, out var judged))
            {
                Console.WriteLine($"  {judged.Verdict,-11} {judged.Why}");

                // The reason it had before, when there was one, because the whole point of
                // a re-read is that the new sentence says more than the old one.
                if (one.VerdictWhy is { Length: > 0 } before)
                    Console.WriteLine($"       was:   {Flat(before, 70)}");

                Console.WriteLine($"       subj:  {Flat(one.Subject, 70)}");
            }
            else
            {
                Console.WriteLine($"  UNREAD      -- no verdict for: {Flat(one.Subject, 70)}");
            }
        }

        if (write && verdicts.Count > 0)
        {
            // sawBody records that this pass READS bodies, not that this message had one --
            // otherwise a notification with an empty body is re-read for ever.
            foreach ((string id, (DeskVerdict verdict, string why)) in verdicts)
                store.Judge(id, verdict, why, DateTime.Now, sawBody: true);

            Console.WriteLine($"\nstored {verdicts.Count} verdict(s); "
                + $"{store.WithoutBodyCount(since)} still to re-read");
        }

        Console.WriteLine();

        if (verdicts.Count == 0)
        {
            Console.WriteLine("FAIL nothing could be read out of the answer. It said:");
            Console.WriteLine(Flat(answer, 400));
            return 1;
        }

        // A reason that only repeats the subject is the failure the bodies were added for,
        // and it is worth naming rather than leaving to a reader's judgement.
        int echoed = verdicts.Count(v =>
            batch.FirstOrDefault(b => b.Id == v.Key) is { } mail
            && Overlap(v.Value.Why, mail.Subject) > 0.6);

        Console.WriteLine($"{verdicts.Count} of {batch.Count} judged; "
            + $"{echoed} reason(s) mostly repeat the subject");

        Console.WriteLine(verdicts.Count == batch.Count
            ? "\nVERIFIED: a tool-free sorting pass reaches the model, answers in a shape the\n"
                + "parser reads, and returns a verdict for every message it was given."
            : "\nPARTIAL: the call worked but not every message came back judged.");

        Console.WriteLine("\nNothing was written to the store.");

        return verdicts.Count == batch.Count ? 0 : 1;
    }

    /// <summary>
    /// How much of a reason is made of words already in the subject.
    /// </summary>
    /// <remarks>
    /// A crude bag-of-words overlap, and crude is enough: the question is whether a reason
    /// says anything the subject does not, and a reason that reuses four fifths of the
    /// subject's words does not, whatever the word order.
    /// </remarks>
    private static double Overlap(string why, string subject)
    {
        string[] left = Words(why);

        if (left.Length == 0)
            return 0;

        HashSet<string> right = new(Words(subject), StringComparer.OrdinalIgnoreCase);

        return left.Count(right.Contains) / (double)left.Length;
    }

    private static string[] Words(string text) =>
        [.. text.Split([' ', '-', ':', ',', '.', '(', ')', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 3)];

    private static string Flat(string text, int max)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();

        while (flat.Contains("  ", StringComparison.Ordinal))
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);

        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
