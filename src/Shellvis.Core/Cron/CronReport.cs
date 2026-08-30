namespace Shellvis.Core.Cron;

/// <summary>
/// Reading a scheduled run's report.
///
/// <b>Why the model decides what is worth a notification.</b> "Is this news" is a judgement
/// about content: three routine mails are not, one from the person whose deadline is tomorrow
/// is. No condition available to this code can tell those apart. The alternatives are both
/// wrong -- announcing every run trains the user to ignore the announcement, announcing none
/// makes a scheduled assistant pointless -- so the run is asked to say, in a form that is
/// absent by default.
///
/// <b>Here rather than in the shell so it can be checked without a desktop.</b> This is the
/// piece that decides whether something appears on someone's screen unbidden, which makes it
/// exactly the piece worth a harness. A check that needs a window is a check that gets
/// skipped on the build machine.
/// </summary>
public static class CronReport
{
    /// <summary>The marker a run uses to say it found something worth telling the user now.</summary>
    /// <remarks>
    /// One constant for the instruction and the parser. Two spellings of the same convention,
    /// one in a prompt and one in a regex, is a thing that drifts apart silently: the model
    /// keeps saying it and nothing keeps hearing it.
    /// </remarks>
    public const string Marker = "NOTIFY:";

    /// <summary>
    /// Take the headline out of a report, leaving the report without it.
    ///
    /// <b>Removed rather than left in place.</b> The same sentence appearing as the notice and
    /// again at the end of the report reads as a stutter, and the report is what the user
    /// opens after the notice has already said it.
    ///
    /// <b>Absent by default.</b> A run that says nothing raises nothing. That asymmetry is the
    /// safety of the whole mechanism: a forgotten marker costs one notice, while the opposite
    /// default costs every future one, because a notice for every routine run teaches the
    /// reader to dismiss them unread.
    /// </summary>
    /// <param name="report">The report, with the marker line removed on return.</param>
    /// <returns>The headline, or null when the run did not ask for one.</returns>
    public static string? TakeHeadline(ref string report)
    {
        string[] lines = (report ?? string.Empty).ReplaceLineEndings("\n").Split('\n');

        // From the end, because the instruction says to put it last, and a report that
        // discusses the convention ("I did not add a NOTIFY line because...") would otherwise
        // be read as carrying one.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            // Bold or bulleted, because a model asked for a literal marker will occasionally
            // dress it: "**NOTIFY:** ..." is the same intent, and refusing it would make the
            // convention brittle for no gain.
            string bare = lines[i].Trim().TrimStart('*', '-', '#', ' ').TrimStart();

            if (!bare.StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
                continue;

            string headline = bare[Marker.Length..].Trim().Trim('*').Trim();

            report = string.Join(
                Environment.NewLine,
                lines.Where((_, index) => index != i)).Trim();

            // A marker with nothing after it is a run that meant to say something and did
            // not. Treated as no headline: an empty notice is worse than none.
            return headline.Length == 0 ? null : headline;
        }

        return null;
    }
}
