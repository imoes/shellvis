using System.Globalization;

namespace Shellvis.Core.Cron;

/// <summary>Which of the three schedule forms a job uses.</summary>
public enum ScheduleKind
{
    /// <summary>Every N seconds/minutes/hours/days, counted from the last run.</summary>
    Interval,

    /// <summary>A five-field cron expression.</summary>
    Cron,

    /// <summary>A single moment.</summary>
    Once,
}

/// <summary>
/// When a job should run.
///
/// Three forms because they answer different questions and one cannot express the
/// others. "every 30 minutes" is relative to the last run and does not care what the
/// clock says; "at 07:00 on weekdays" is absolute and must not drift; "at 14:30 today"
/// happens once. Forcing all three into cron syntax would make the common case
/// (<c>30m</c>) unreadable, and cron cannot express "half an hour after the last run"
/// at all.
/// </summary>
public sealed class CronSchedule
{
    private CronSchedule(ScheduleKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public ScheduleKind Kind { get; }

    /// <summary>The expression as written, so it can be shown and round-tripped.</summary>
    public string Text { get; }

    /// <summary>For Interval.</summary>
    public TimeSpan Interval { get; private init; }

    /// <summary>For Once.</summary>
    public DateTimeOffset At { get; private init; }

    /// <summary>For Cron: the allowed values per field.</summary>
    private bool[] _minutes = [];
    private bool[] _hours = [];
    private bool[] _daysOfMonth = [];
    private bool[] _months = [];
    private bool[] _daysOfWeek = [];

    /// <summary>
    /// Whether day-of-month and day-of-week are BOTH restricted.
    ///
    /// This is the classic cron subtlety and it is wrong in most re-implementations:
    /// when both fields are restricted, they combine with OR, not AND. So
    /// <c>0 0 13 * 5</c> means "the 13th, and every Friday" -- not "Friday the 13th".
    /// Getting this backwards makes a job fire far too rarely, which is the kind of bug
    /// nobody notices for a month.
    /// </summary>
    private bool _bothDayFieldsRestricted;

    /// <summary>Parse a schedule, or explain what is wrong with it.</summary>
    public static bool TryParse(string? text, out CronSchedule? schedule, out string? problem)
    {
        schedule = null;
        problem = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "a schedule is required: an interval like 30m, a five-field cron "
                + "expression, or an ISO timestamp.";
            return false;
        }

        string value = text.Trim();

        // Interval first: it is the shortest form and cannot be confused with the
        // others, since a cron expression always has spaces and a timestamp has digits
        // in a different shape.
        if (TryParseInterval(value, out TimeSpan interval, out string? intervalProblem))
        {
            if (intervalProblem is not null)
            {
                problem = intervalProblem;
                return false;
            }

            schedule = new CronSchedule(ScheduleKind.Interval, value) { Interval = interval };
            return true;
        }

        if (value.Contains(' '))
            return TryParseCron(value, out schedule, out problem);

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTimeOffset moment))
        {
            schedule = new CronSchedule(ScheduleKind.Once, value) { At = moment };
            return true;
        }

        problem = $"'{value}' is not a schedule. Use an interval (30m, 2h, 1d), a "
            + "five-field cron expression (0 7 * * 1-5), or an ISO timestamp "
            + "(2026-08-25T09:00).";

        return false;
    }

    private static bool TryParseInterval(string value, out TimeSpan interval, out string? problem)
    {
        interval = TimeSpan.Zero;
        problem = null;

        if (value.Length < 2)
            return false;

        char unit = char.ToLowerInvariant(value[^1]);

        if (unit is not ('s' or 'm' or 'h' or 'd'))
            return false;

        if (!int.TryParse(value[..^1], CultureInfo.InvariantCulture, out int amount))
            return false;

        // Recognised as an interval but not a usable one: reported rather than falling
        // through to "not a schedule", which would send the user looking in the wrong
        // place.
        if (amount <= 0)
        {
            problem = $"'{value}' has a non-positive interval; a job that runs every "
                + "zero seconds is a busy loop, not a schedule.";

            return true;
        }

        interval = unit switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount),
        };

        // A floor, not a preference. Every run costs model tokens and can touch the
        // machine; a ten-second job is almost certainly a typo for ten minutes.
        if (interval < TimeSpan.FromSeconds(30))
        {
            problem = $"'{value}' is shorter than the 30 second minimum. Each run costs "
                + "a model call and can change the machine.";

            return true;
        }

        return true;
    }

    private static bool TryParseCron(string value, out CronSchedule? schedule, out string? problem)
    {
        schedule = null;
        problem = null;

        string[] fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (fields.Length != 5)
        {
            problem = $"'{value}' has {fields.Length} fields; a cron expression has five: "
                + "minute hour day-of-month month day-of-week.";

            return false;
        }

        var result = new CronSchedule(ScheduleKind.Cron, value);

        if (!TryField(fields[0], 0, 59, "minute", out result._minutes, out problem)
            || !TryField(fields[1], 0, 23, "hour", out result._hours, out problem)
            || !TryField(fields[2], 1, 31, "day-of-month", out result._daysOfMonth, out problem)
            || !TryField(fields[3], 1, 12, "month", out result._months, out problem)
            || !TryField(fields[4], 0, 7, "day-of-week", out result._daysOfWeek, out problem))
        {
            return false;
        }

        // 7 and 0 both mean Sunday; folding them here means the matcher only has to
        // know about 0.
        if (result._daysOfWeek[7])
            result._daysOfWeek[0] = true;

        result._bothDayFieldsRestricted =
            fields[2].Trim() != "*" && fields[4].Trim() != "*";

        schedule = result;
        return true;
    }

    /// <summary>
    /// Parse one cron field: <c>*</c>, <c>*/n</c>, <c>a-b</c>, <c>a-b/n</c>, <c>a,b,c</c>.
    /// </summary>
    private static bool TryField(
        string field, int min, int max, string name, out bool[] allowed, out string? problem)
    {
        allowed = new bool[max + 1];
        problem = null;

        foreach (string part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string item = part.Trim();
            int step = 1;

            int slash = item.IndexOf('/');

            if (slash >= 0)
            {
                if (!int.TryParse(item[(slash + 1)..], CultureInfo.InvariantCulture, out step)
                    || step <= 0)
                {
                    problem = $"the {name} field has an invalid step in '{item}'.";
                    return false;
                }

                item = item[..slash];
            }

            int from;
            int to;

            if (item is "*" or "")
            {
                from = min;
                to = max;
            }
            else if (item.Contains('-'))
            {
                string[] bounds = item.Split('-');

                if (bounds.Length != 2
                    || !int.TryParse(bounds[0], CultureInfo.InvariantCulture, out from)
                    || !int.TryParse(bounds[1], CultureInfo.InvariantCulture, out to))
                {
                    problem = $"the {name} field has an invalid range in '{item}'.";
                    return false;
                }
            }
            else if (int.TryParse(item, CultureInfo.InvariantCulture, out int single))
            {
                from = single;
                to = single;
            }
            else
            {
                problem = $"the {name} field contains '{item}', which is not a number, "
                    + "a range or *.";

                return false;
            }

            if (from < min || to > max || from > to)
            {
                problem = $"the {name} field value '{item}' is outside {min}-{max}.";
                return false;
            }

            for (int i = from; i <= to; i += step)
                allowed[i] = true;
        }

        return true;
    }

    /// <summary>
    /// The next time this schedule is due, or null when it never will be again.
    /// </summary>
    /// <param name="after">The moment to search from, exclusive.</param>
    /// <param name="lastRun">
    /// When the job last ran, which is what an interval counts from. Null means it has
    /// never run, and an interval job is then due immediately -- a job added at 09:00
    /// with "every hour" should not stay silent until 10:00 before proving it works.
    /// </param>
    public DateTimeOffset? Next(DateTimeOffset after, DateTimeOffset? lastRun)
    {
        switch (Kind)
        {
            case ScheduleKind.Interval:
                return lastRun is null ? after : lastRun.Value + Interval;

            case ScheduleKind.Once:
                return At > after ? At : null;

            default:
                return NextCron(after);
        }
    }

    /// <summary>
    /// Walk forward to the next matching minute.
    ///
    /// Day by day and then minute within the day, rather than minute by minute from
    /// now: a schedule like "29 February" would otherwise mean scanning two million
    /// minutes. Bounded at four years, which covers the leap-year case and guarantees
    /// termination for an expression that can never match (30 February).
    /// </summary>
    private DateTimeOffset? NextCron(DateTimeOffset after)
    {
        // Cron has minute resolution, so the search starts at the next whole minute.
        DateTimeOffset cursor = new DateTimeOffset(
            after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Offset)
            .AddMinutes(1);

        DateTimeOffset limit = after.AddYears(4);

        while (cursor < limit)
        {
            if (!_months[cursor.Month])
            {
                // Skip the whole month: jump to the first of the next one.
                cursor = new DateTimeOffset(cursor.Year, cursor.Month, 1, 0, 0, 0, cursor.Offset)
                    .AddMonths(1);

                continue;
            }

            if (!DayMatches(cursor))
            {
                cursor = new DateTimeOffset(
                    cursor.Year, cursor.Month, cursor.Day, 0, 0, 0, cursor.Offset).AddDays(1);

                continue;
            }

            if (!_hours[cursor.Hour])
            {
                cursor = new DateTimeOffset(
                    cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0, cursor.Offset)
                    .AddHours(1);

                continue;
            }

            if (!_minutes[cursor.Minute])
            {
                cursor = cursor.AddMinutes(1);
                continue;
            }

            return cursor;
        }

        return null;
    }

    private bool DayMatches(DateTimeOffset moment)
    {
        bool dom = _daysOfMonth[moment.Day];
        bool dow = _daysOfWeek[(int)moment.DayOfWeek];

        // The OR rule. Only applies when both fields are restricted; if either is *,
        // the other one alone decides.
        return _bothDayFieldsRestricted ? dom || dow : dom && dow;
    }

    /// <summary>A human-readable rendering, for a job listing.</summary>
    public string Describe() => Kind switch
    {
        ScheduleKind.Interval => $"every {Text}",
        ScheduleKind.Once => $"once at {At:yyyy-MM-dd HH:mm}",
        _ => $"cron {Text}",
    };
}
