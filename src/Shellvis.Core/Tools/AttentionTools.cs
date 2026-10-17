using System.Text;
using Shellvis.Core.Desktop;

namespace Shellvis.Core.Tools;

/// <summary>
/// What the user had in front of them recently, read from <see cref="AttentionLog"/>.
/// </summary>
public sealed class AttentionTools(AttentionLog log)
{
    /// <summary>Lines shown at most, newest kept.</summary>
    private const int MaxLines = 60;

    /// <summary>Glances shorter than this are passing through, not attention, and are left out.</summary>
    private static readonly TimeSpan Passing = TimeSpan.FromSeconds(3);

    [ShellvisTool(
        "attention_recent",
        SideEffect.ReadOnly,
        Description =
            "Which windows the user had in front over the last minutes, oldest first, with how "
            + "long each was in front. Use it whenever the user refers to something by when they "
            + "looked at it rather than by name: 'the mail I just had open', 'the document from "
            + "before the meeting', 'what was I working on at ten'. An opened Outlook mail is "
            + "titled with its subject, a document with its file name, a browser tab with the "
            + "page title -- search for that title with the mail or file tools next. Window "
            + "titles only, kept in memory for eight hours and never written anywhere.",
        PreviewParameter = "minutes",
        Glyph = "focus")]
    public string Recent(int minutes = 30)
    {
        int span = Math.Clamp(minutes, 1, (int)AttentionLog.Kept.TotalMinutes);
        DateTime now = DateTime.Now;

        IReadOnlyList<GlanceSpan> glances = log.Since(now.AddMinutes(-span), now)
            .Where((g, i) => g.For >= Passing || i == 0)
            .ToList();

        if (glances.Count == 0)
        {
            return $"nothing recorded in the last {span} minutes. The record starts when "
                + "Shellvis starts and keeps eight hours; before that there is nothing to read.";
        }

        var text = new StringBuilder();
        text.AppendLine($"windows in front over the last {span} minutes, oldest first:");

        foreach (GlanceSpan glance in glances.TakeLast(MaxLines))
        {
            text.AppendLine(
                $"{glance.Glance.At:HH:mm}  {Duration(glance.For),7}  {glance.Glance.Program}  {glance.Glance.Title}");
        }

        if (glances.Count > MaxLines)
            text.AppendLine($"({glances.Count - MaxLines} earlier ones not shown; ask for fewer minutes)");

        return text.ToString().TrimEnd();
    }

    private static string Duration(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{span.TotalMinutes:F0} min" : $"{span.TotalSeconds:F0} s";
}
