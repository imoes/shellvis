using System.Globalization;

namespace Shellvis.Core.Agent;

/// <summary>
/// What one call to the model cost: how much went in, how much came out, how fast.
///
/// <b>Why the input number is the one that matters.</b> Output tokens are a few hundred and
/// they cost time; input tokens are the whole conversation plus the tool catalogue plus the
/// skills index, they are re-sent on every iteration of every turn, and when they run out the
/// context is compacted and something is quietly forgotten. A reader who can see the input
/// climbing knows why answers are slowing down and when a fresh session is due -- and that is
/// invisible unless it is on screen.
///
/// <b>Why the percentage needs a denominator that is known rather than assumed.</b> A share of
/// the window is only meaningful if the window's size is real. Guessing it -- 4k because that
/// used to be usual, 128k because the model card says so -- produces a number that looks
/// authoritative and is wrong, on a display whose whole purpose is to say how close to the
/// edge the conversation is. So <see cref="ContextTokens"/> is nullable, and with nothing to
/// divide by the share is simply not shown.
/// </summary>
/// <param name="Input">Prompt tokens the provider reported for this call.</param>
/// <param name="Output">Completion tokens the provider reported.</param>
/// <param name="Elapsed">How long the call took, wall clock.</param>
/// <param name="ContextTokens">The window's size, when it is actually known.</param>
public sealed record TurnCost(
    int Input,
    int Output,
    TimeSpan Elapsed,
    int? ContextTokens = null)
{
    /// <summary>Nothing measured. Distinct from zero tokens, which is a measurement.</summary>
    public static TurnCost Unknown { get; } = new(0, 0, TimeSpan.Zero);

    /// <summary>Whether the provider reported anything at all.</summary>
    /// <remarks>
    /// Not every endpoint does. An OpenAI-compatible server only returns usage on a
    /// streaming call when it is asked to, and some proxies drop it. A display that showed
    /// "0 tokens" in that case would be reporting a measurement that was never taken.
    /// </remarks>
    public bool Measured => Input > 0 || Output > 0;

    /// <summary>How full the window is, or null when its size is not known.</summary>
    public double? Share => ContextTokens is > 0 and { } window
        ? Math.Min(1.0, (double)Input / window)
        : null;

    /// <summary>
    /// Output tokens per second, or null when there is nothing to divide by.
    ///
    /// Wall clock rather than time-to-first-token: what a reader wants to know is how long
    /// an answer took, and splitting that into queue time and generation time would need a
    /// number the provider does not send.
    /// </summary>
    public double? PerSecond => Output > 0 && Elapsed > TimeSpan.FromMilliseconds(50)
        ? Output / Elapsed.TotalSeconds
        : null;

    /// <summary>
    /// The one line the window shows.
    /// </summary>
    /// <remarks>
    /// Compact on purpose: it sits in a header beside the window's own buttons, and a
    /// header that wraps is worse than one that says less. Thousands are abbreviated
    /// because the exact figure is never the question -- "how close am I to the edge" is.
    /// </remarks>
    public string Line()
    {
        if (!Measured)
            return string.Empty;

        var parts = new List<string>();

        parts.Add(Share is { } share
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"in {Short(Input)}/{Short(ContextTokens!.Value)} ({share * 100:F0}%)")
            : string.Create(CultureInfo.CurrentCulture, $"in {Short(Input)}"));

        if (Output > 0)
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"out {Short(Output)}"));

        if (PerSecond is { } rate)
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{rate:F1} tok/s"));

        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// A token count in as few characters as it can be read in.
    /// </summary>
    /// <remarks>
    /// One decimal below ten thousand, none above: "9.4k" is a useful distinction from
    /// "9.9k" while "31.2k" and "31k" are the same fact for anybody reading a header.
    /// </remarks>
    private static string Short(int tokens) => tokens switch
    {
        < 1000 => tokens.ToString(CultureInfo.CurrentCulture),
        < 10_000 => string.Create(CultureInfo.CurrentCulture, $"{tokens / 1000.0:F1}k"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{tokens / 1000:F0}k"),
    };
}
