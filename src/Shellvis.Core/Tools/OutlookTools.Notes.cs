using System.Text;

using Shellvis.Core.Notes;
using Shellvis.Core.Office;

namespace Shellvis.Core.Tools;

/// <summary>
/// Attaching what is already known about the people in a result.
///
/// <b>Why this is mechanical and not an instruction.</b> This project has measured twice
/// that an instruction the model is supposed to follow at the end of a long turn does not
/// get followed: telling it to write down what it learned produced not a single call across
/// two different phrasings, which is why skill writing moved into code. Telling it to
/// remember to search its notes before answering would fail the same way, and fail
/// invisibly, because an answer written without the note looks exactly like an answer.
///
/// So the notes come to the model rather than the model going to the notes, in the same
/// shape as the module diff on <c>powershell_module_import</c>: the tool result carries what
/// the next turn needs.
///
/// <b>Bounded on purpose.</b> Only open notes, only the people actually named in the result,
/// only a few lines. A tool result that drags the whole note database along would put
/// private observations about everyone into the context of every question.
/// </summary>
public sealed partial class OutlookTools
{
    /// <summary>At most this many notes attached to one result.</summary>
    private const int MaxAttached = 8;

    /// <summary>
    /// What is noted about these people, as a block to append to a tool result.
    ///
    /// Empty when there is nothing, and empty is the common case. A trailing "no notes"
    /// line on every mail listing would be noise the reader learns to skip, which is how a
    /// signal stops being one.
    /// </summary>
    private string NotesAbout(IEnumerable<string> people)
    {
        if (notes is null)
            return string.Empty;

        var seen = new HashSet<long>();
        var lines = new List<string>();

        foreach (string person in people.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(person))
                continue;

            try
            {
                foreach (Note note in notes.About(person, MaxAttached))
                {
                    if (lines.Count >= MaxAttached)
                        break;

                    if (seen.Add(note.Id))
                        lines.Add($"  {note}   id {note.Id}");
                }
            }
            catch (Exception)
            {
                // A note database that cannot be read must not break reading mail. The
                // notes are an enrichment; the mail is the answer.
                return string.Empty;
            }
        }

        if (lines.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine().AppendLine("You have noted about the people above:");

        foreach (string line in lines)
            sb.AppendLine(line);

        return sb.ToString();
    }

    /// <summary>Notes falling due inside a date range, for a calendar answer.</summary>
    /// <remarks>
    /// Attached to the calendar rather than left for a reminder job, because "what is on
    /// this week" is exactly the moment a deadline noted three weeks ago becomes relevant.
    /// A reminder that arrives on the day is a reminder that arrives too late to act on.
    /// </remarks>
    private string NotesDueBy(DateTime through)
    {
        if (notes is null)
            return string.Empty;

        try
        {
            IReadOnlyList<Note> due = notes.Due(through, MaxAttached);

            if (due.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine().AppendLine("Also noted as due in this period:");

            foreach (Note note in due)
                sb.Append("  ").Append(note).Append("   id ").AppendLine(note.Id.ToString());

            return sb.ToString();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>The people named in a set of messages, senders first.</summary>
    private static IEnumerable<string> PeopleIn(IEnumerable<MailSummary> mail)
    {
        foreach (MailSummary message in mail)
        {
            if (message.From.Length > 0)
                yield return message.From;

            if (message.SenderAddress.Length > 0)
                yield return message.SenderAddress;
        }
    }
}
