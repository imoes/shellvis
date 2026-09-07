using Shellvis.Core.Desk;

namespace Shellvis.DesktopProbe;

/// <summary>
/// What the real desk store actually holds, for when the page and the expectation disagree.
///
/// <b>Why this exists.</b> "Under 'muss man wissen' there is no summary" has three possible
/// causes and they need opposite fixes: the model did not write one, the parser dropped it, or
/// the page does not render it. Guessing which costs a build and a restart each time; reading
/// the row settles it in a second.
///
/// It prints what is stored, clipped -- this is a real mailbox and a diagnostic does not need
/// anybody's correspondence in full to answer the question it was asked.
/// </summary>
internal static class DeskPeek
{
    public static int Run(int howMany)
    {
        Console.WriteLine("=== the remembered desk, as stored ===\n");

        using var store = new DeskStore();

        Console.WriteLine($"{store.Count()} thing(s) held, oldest {store.Oldest():yyyy-MM-dd}");
        Console.WriteLine($"retention {store.Retention.TotalDays:F0} days\n");

        // Several windows, because the page shows one and the store holds three months --
        // and a page reading zero while the store holds fifty-five is either a window that
        // excludes them or a state that dropped them. The two need opposite fixes.
        foreach (int days in new[] { 1, 3, 7, 14, 30, 92 })
        {
            DeskTally t = store.Tally(DateTime.Now.AddDays(-days));

            Console.WriteLine(
                $"last {days,3} day(s): {t.Answer,3} answer  {t.Information,3} information  "
                + $"{t.Ignore,3} ignore  {t.Pending,3} unjudged   = {t.Total,3} unread rows");
        }

        Console.WriteLine();
        Console.WriteLine("-- the newest mail rows, whatever their state --");

        foreach (DeskObject row in store.Recent(DateTime.Now.AddDays(-3), 10)
            .Where(o => o.Kind == DeskKind.Mail))
        {
            Console.WriteLine(
                $"   {row.When:dd.MM. HH:mm}  state={row.State,-16} "
                + $"verdict={row.Verdict?.ToString() ?? "-",-12} {Clip(row.Subject, 40)}");
        }

        Console.WriteLine();

        foreach (DeskVerdict verdict in new[] { DeskVerdict.Answer, DeskVerdict.Information })
        {
            IReadOnlyList<DeskObject> rows = store.Judged(verdict, DateTime.Now.AddDays(-30), howMany);

            Console.WriteLine($"-- {verdict} ({rows.Count}) --");

            if (rows.Count == 0)
                Console.WriteLine("   (none)");

            foreach (DeskObject row in rows)
            {
                Console.WriteLine($"   {row.When:dd.MM. HH:mm}  {Clip(row.WhoName, 28)}");
                Console.WriteLine($"      subject : {Clip(row.Subject, 70)}");
                Console.WriteLine($"      why     : {(row.VerdictWhy is { Length: > 0 } w ? Clip(w, 90) : "(EMPTY -- nothing was stored)")}");
                Console.WriteLine($"      entryId : {(row.EntryId is { Length: > 0 } e ? e[..Math.Min(16, e.Length)] + "..." : "(none)")}");
                Console.WriteLine($"      enriched: {(row.Enrichment is { Length: > 0 } n ? Clip(n, 90) : "(none)")}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static string Clip(string? text, int max)
    {
        string flat = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();

        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
