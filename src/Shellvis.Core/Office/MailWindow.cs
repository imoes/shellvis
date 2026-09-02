using System.Globalization;

namespace Shellvis.Core.Office;

/// <summary>
/// One page of a mail listing, together with what it left out.
///
/// <b>Why a page and not a list.</b> The list on its own is what made this application
/// answer confidently and wrongly. Asked "what were the highlights last week" the model
/// called <c>mail_list</c>, received the newest twenty messages, and summarised them -- and
/// nothing in that result said the folder held three thousand, or that those twenty covered
/// two days rather than seven. The count was even read from Outlook and then discarded. So
/// the answer named a week and described a Tuesday, and neither the reader nor the model
/// could tell.
///
/// This is the same rule <see cref="Connectors.ResultShaper"/> already enforces for every
/// connector -- the count comes before the content, and a truncation says so -- arriving
/// where it should have been all along.
/// </summary>
/// <param name="Matching">
/// How many messages met the criteria, not how many were returned. The difference is the
/// truncation, and it is the number worth showing.
/// </param>
/// <param name="InFolder">
/// How many the folder holds in total, ignoring the filter. Present so that "nothing in
/// this window" can be told apart from "nothing here at all" -- an empty result that cannot
/// say which of the two it is invites the model to treat it as an answer.
/// </param>
/// <param name="Newest">The timestamp of the first returned message, or null when none were.</param>
/// <param name="Oldest">
/// The timestamp of the last returned message. With <see cref="Newest"/> this is the span the
/// page actually covers, which is the fact a question about "last week" turns on.
/// </param>
public sealed record MailPage(
    IReadOnlyList<MailSummary> Messages,
    int Matching,
    int InFolder,
    DateTime? Newest,
    DateTime? Oldest)
{
    public static MailPage Empty { get; } = new([], 0, 0, null, null);

    /// <summary>Whether more matched than were returned.</summary>
    public int Withheld => Math.Max(0, Matching - Messages.Count);
}

/// <summary>
/// Turning "last week" into a date.
///
/// Separated out and free of COM so the harness can check it: the arithmetic is where a
/// date range goes wrong, and this project has already paid for that once in the calendar.
/// </summary>
public static class MailWindow
{
    /// <summary>The shapes only ISO writes, matched exactly so no culture can reinterpret them.</summary>
    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
    ];


    /// <summary>
    /// Read a point in time from what a model is likely to write.
    ///
    /// Relative first, because that is what a question about "the last few days" turns into
    /// naturally, and because a relative offset cannot be misread the way a written date can.
    /// Absolute dates are accepted in ISO form and in the user's own short format -- a German
    /// machine's model writes <c>25.08.2026</c>, and refusing it would be pedantry.
    /// </summary>
    /// <param name="now">Passed in rather than read, so the harness can fix it.</param>
    /// <param name="problem">A sentence for the user when the answer is false. Never null then.</param>
    public static bool TryParse(
        string? text,
        DateTime now,
        out DateTime value,
        out string? problem)
    {
        value = default;
        problem = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "no date given.";
            return false;
        }

        string trimmed = text.Trim();

        if (string.Equals(trimmed, "today", StringComparison.OrdinalIgnoreCase))
        {
            value = now.Date;
            return true;
        }

        if (string.Equals(trimmed, "yesterday", StringComparison.OrdinalIgnoreCase))
        {
            value = now.Date.AddDays(-1);
            return true;
        }

        // "7d", "36h", "2w" -- an offset BACK from now, because every question phrased this
        // way looks backwards. A future offset has no meaning for received mail.
        if (trimmed.Length >= 2
            && int.TryParse(
                trimmed[..^1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int amount)
            && amount > 0)
        {
            switch (char.ToLowerInvariant(trimmed[^1]))
            {
                case 'h':
                    value = now.AddHours(-amount);
                    return true;

                case 'd':
                    value = now.Date.AddDays(-amount);
                    return true;

                case 'w':
                    value = now.Date.AddDays(-7 * amount);
                    return true;
            }
        }

        // Decided by SHAPE, not by trying one culture and then the other -- and this order
        // was arrived at by getting it wrong.
        //
        // The obvious version is invariant first ("so an ISO date from a model is read as
        // written") and the local culture as a fallback. It silently mangles dates: the
        // invariant parser accepts a full stop as a date separator and reads month first, so
        // '02.09.2026' from a German desktop came back as 9 February. It never fails, so the
        // local fallback is never reached, and only the days above the twelfth escape --
        // above the twelfth the month field is invalid and the invariant parse finally gives
        // up. That is the same shape of bug, and the same near-invisibility, as the calendar
        // range defect this project already paid for.
        //
        // A leading four-digit year is ISO and nothing else, so it is matched exactly.
        // Everything else is the user's own format, and their culture reads it.
        if (DateTime.TryParseExact(
                trimmed,
                IsoFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value))
        {
            return true;
        }

        if (DateTime.TryParse(
                trimmed,
                CultureInfo.CurrentCulture,
                DateTimeStyles.None,
                out value))
        {
            return true;
        }

        // Last, and only for what neither of the above recognises -- an American date on a
        // German machine, say. By now it cannot steal a reading from either.
        if (DateTime.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value))
        {
            return true;
        }

        problem = $"'{text.Trim()}' is not a date. Use 7d, 36h, 2w, today, yesterday, "
            + "or a date like 2026-08-25.";

        return false;
    }
}
