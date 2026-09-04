namespace Shellvis.Core.Desk;

/// <summary>
/// How far back "remembering" reaches, as a thing that can change while running.
///
/// <b>Why this exists rather than a number passed once.</b> The window is set by a slider on
/// a page, so it changes between one tool call and the next. Handing the tools an <c>int</c>
/// at construction would freeze whatever the setting was when the session started, and moving
/// the slider would appear to do nothing until a restart -- which is exactly the kind of
/// silent staleness this project keeps finding in itself.
///
/// <b>Why not read the config file on every call.</b> Because a tool call would then do file
/// IO to answer "how many days", and a config file that fails to parse would make a search
/// fail rather than fall back to a default. This holds the live value; the caller that owns
/// the slider is responsible for writing it to the config so it survives a restart.
/// </summary>
public sealed class DeskWindow(int days = 30)
{
    /// <summary>The shortest window worth offering: today and yesterday.</summary>
    public const int Least = 2;

    /// <summary>The longest, which is everything the store keeps.</summary>
    public const int Most = 92;

    private int _days = Clamp(days);

    /// <summary>
    /// How many days back to look. Clamped on the way in, because a slider that sends 0 or
    /// 5000 should be corrected here rather than turning into a query that returns nothing
    /// or everything.
    /// </summary>
    public int Days
    {
        get => _days;
        set => _days = Clamp(value);
    }

    /// <summary>The earliest moment inside the window, from a given now.</summary>
    public DateTime Since(DateTime now) => now.AddDays(-_days);

    /// <summary>The window in words, for a label and for a tool result.</summary>
    public string Describe() => _days switch
    {
        <= 2 => "die letzten zwei Tage",
        <= 7 => $"die letzten {_days} Tage",
        <= 14 => "die letzten zwei Wochen",
        <= 31 => "den letzten Monat",
        <= 62 => "die letzten zwei Monate",
        _ => "das ganze Vierteljahr",
    };

    private static int Clamp(int days) => Math.Clamp(days, Least, Most);
}
