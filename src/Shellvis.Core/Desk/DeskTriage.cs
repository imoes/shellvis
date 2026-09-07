using System.Globalization;
using System.Text;

namespace Shellvis.Core.Desk;

/// <summary>
/// Asking a model which of these needs an answer, and reading what it says back.
///
/// <b>Why a model and not a rule.</b> The desk sorted mail by who sent it, and that is the
/// wrong question: a colleague can send something that needs no answer at all -- an
/// announcement, a broadcast, a note for the file -- and a system can send something that
/// does. Sender is a fact about the envelope. Whether somebody is waiting is a judgement
/// about the contents, and the only thing here that can read contents is the model.
///
/// <b>Why in batches, and small ones.</b> One turn against a local model takes between one
/// and three minutes on this machine. Asking per message would mean four hundred unread
/// messages could never be judged; asking about all of them at once would mean a prompt
/// nobody can hold and an answer that drifts halfway through. Ten at a time, judged once
/// each, and the verdict is remembered -- so the cost is paid once per message rather than
/// once per look.
///
/// <b>Why the parsing is here and pure.</b> It is the part that fails silently. A model that
/// answers in prose, renumbers the list, translates the labels or invents an id produces
/// either no verdicts or verdicts on the wrong messages, and both look like a quiet morning.
/// So the format is narrow, the reading is forgiving about everything that does not matter,
/// and it is checked without a model in the loop.
/// </summary>
public static class DeskTriage
{
    /// <summary>How many messages one question covers.</summary>
    /// <remarks>
    /// Ten. Enough that a busy morning is worked through in a few passes, few enough that
    /// the model still has each subject in view when it writes the last line -- a list of
    /// forty comes back with the first ten judged and the rest labelled by rhythm.
    /// </remarks>
    public const int PerBatch = 10;

    /// <summary>
    /// The question, with one numbered line per message.
    /// </summary>
    /// <remarks>
    /// <b>Numbered, not addressed by id.</b> The ids are message ids -- forty characters of
    /// punctuation -- and a model asked to copy one back gets it wrong often enough to
    /// matter, silently attributing a verdict to nothing. A number from 1 to 10 cannot be
    /// mistyped into another message's verdict, and the caller holds the mapping.
    /// </remarks>
    public static string Ask(IReadOnlyList<DeskObject> mail)
    {
        var sb = new StringBuilder();

        sb.AppendLine("""
            Sort this unread mail. For each one decide what it needs from the person whose
            desk this is.

            ANSWER      somebody is waiting for a reply from them, or a deadline in it
                        requires them to act. Only this one costs their attention.
            INFORMATION worth knowing, but nothing goes back. Announcements, notifications,
                        reports, a colleague copying them in, a page that changed, a ticket
                        that moved.
            IGNORE      not worth even reading: a broadcast to everybody, a bounce, an
                        advertisement, a duplicate of something already in this list.

            Most mail is INFORMATION. ANSWER is the exception, and marking too much of it as
            ANSWER is the failure to avoid: a tray of forty things that all need answering is
            a tray nobody can use. A newsletter is never an ANSWER. A notification from a
            system is INFORMATION unless it names a deadline for this person.

            Answer with one line per message, nothing else, in this exact form:

                <number> | <ANSWER|INFORMATION|IGNORE> | <reason in at most twelve words>

            The reason is for the person reading the tray, in the language of the mail. No
            preamble, no numbering of your own, no blank lines, no line for anything not
            listed below.

            The mail:
            """);

        for (int i = 0; i < mail.Count; i++)
        {
            DeskObject one = mail[i];

            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{i + 1}. "))
                .Append(one.When.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture))
                .Append("  from: ")
                .Append(Short(one.WhoName is { Length: > 0 } who ? who : one.WhoAddress, 60))
                .Append("  <")
                .Append(Short(one.WhoAddress, 60))
                .Append(">  subject: ")
                .AppendLine(Short(one.Subject, 160));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Read the verdicts back, keyed by the id of the message each one is about.
    /// </summary>
    /// <param name="said">Whatever the model answered.</param>
    /// <param name="asked">The same list, in the same order, that <see cref="Ask"/> was given.</param>
    /// <remarks>
    /// <b>Forgiving about form, strict about identity.</b> Extra prose, markdown bullets,
    /// bold labels and a different separator are all tolerated -- they change nothing about
    /// which message is meant. A number outside the list, or a label that is not one of the
    /// three, is dropped rather than guessed: an unjudged message is asked about again on the
    /// next pass, while a wrong verdict is remembered for three months.
    /// </remarks>
    public static IReadOnlyDictionary<string, (DeskVerdict Verdict, string Why)> Read(
        string? said,
        IReadOnlyList<DeskObject> asked)
    {
        var verdicts = new Dictionary<string, (DeskVerdict, string)>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(said) || asked.Count == 0)
            return verdicts;

        foreach (string raw in said.Split('\n', StringSplitOptions.TrimEntries))
        {
            // Markdown decoration first: a model told to answer "3 | ANSWER | ..." will
            // sometimes answer "- **3** | ANSWER | ...", and none of that is disagreement.
            string line = raw.Trim('*', '-', '#', '>', ' ', '\t', '\r');

            if (line.Length == 0)
                continue;

            string[] parts = line.Split('|', StringSplitOptions.TrimEntries);

            if (parts.Length < 2)
                continue;

            if (!int.TryParse(
                    new string(parts[0].Where(char.IsDigit).ToArray()),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int number))
            {
                continue;
            }

            // One-based, and out of range means the model invented a message. Dropped: the
            // alternative is attributing a verdict to whichever row happens to be there.
            if (number < 1 || number > asked.Count)
                continue;

            if (Label(parts[1]) is not { } verdict)
                continue;

            string why = parts.Length > 2 ? Short(parts[2].Trim('*', ' '), 120) : string.Empty;

            // First verdict wins. A model that lists a message twice has changed its mind
            // in the middle of one answer, and the later line is not more considered than
            // the earlier one -- it is just later.
            verdicts.TryAdd(asked[number - 1].Id, (verdict, why));
        }

        return verdicts;
    }

    /// <summary>One of the three words, whatever else is around it.</summary>
    private static DeskVerdict? Label(string said)
    {
        string word = said.Trim('*', '_', '`', ' ', '.', ':').ToUpperInvariant();

        // Contains rather than equals: "ANSWER (dringend)" and "**INFORMATION**" both mean
        // what they say. Checked in an order that cannot be ambiguous -- no label is a
        // substring of another.
        if (word.Contains("ANSWER", StringComparison.Ordinal)
            || word.Contains("ANTWORT", StringComparison.Ordinal))
        {
            return DeskVerdict.Answer;
        }

        if (word.Contains("INFORMATION", StringComparison.Ordinal)
            || word.Contains("INFO", StringComparison.Ordinal))
        {
            return DeskVerdict.Information;
        }

        if (word.Contains("IGNORE", StringComparison.Ordinal)
            || word.Contains("IGNORIEREN", StringComparison.Ordinal))
        {
            return DeskVerdict.Ignore;
        }

        return null;
    }

    /// <summary>
    /// Clip, and flatten.
    ///
    /// A subject with a newline in it would otherwise break the numbered list into lines the
    /// model reads as separate messages -- and then every verdict after it is attributed to
    /// the wrong mail.
    /// </summary>
    private static string Short(string? text, int max)
    {
        string flat = (text ?? string.Empty).ReplaceLineEndings(" ").Replace("|", "/", StringComparison.Ordinal).Trim();

        while (flat.Contains("  ", StringComparison.Ordinal))
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);

        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
