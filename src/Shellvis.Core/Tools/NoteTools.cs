using System.Globalization;
using System.Text;

using Shellvis.Core.Notes;

namespace Shellvis.Core.Tools;

/// <summary>
/// The notes an assistant keeps about people, topics and deadlines.
///
/// <b>What belongs here and what does not.</b> A note is a durable observation that a later
/// session would otherwise have to rediscover: someone's wife likes roses, the head of
/// department wants the figures by Friday, this colleague handled a difficult customer well.
/// What does not belong is an answer to today's question, anything the code already knows,
/// and anything the user would be unhappy to find written down about them.
///
/// <b>Writing is silent.</b> Unusually for a mutating action, and deliberately: a note is
/// small, private to this machine, closable, and it records where it came from. Prompting for
/// each one would mean the assistant either stops noticing things or trains the user to click
/// through prompts. The prompt budget is spent on actions that touch the world.
/// </summary>
public sealed class NoteTools(NoteStore notes)
{
    private const int MaxBody = 500;

    [ShellvisTool(
        "note_add",
        SideEffect.ReadOnly,
        Description =
            "Write down a durable observation about a person, a topic or a deadline, so a "
            + "later session does not have to rediscover it. Good: 'his wife likes roses, "
            + "their anniversary is in May', 'Dr Weber wants the Q3 figures by Friday', "
            + "'Schulz handled the Meier escalation well'. Bad: the answer to today's "
            + "question, general knowledge, or anything the user would not want written "
            + "down about them. Give dueDate as yyyy-MM-dd when it needs acting on by a "
            + "date, and name the mail or appointment it came from.",
        PreviewParameter = "body",
        Glyph = "note")]
    public string AddNote(
        string body,
        string? person = null,
        string? topic = null,
        string? dueDate = null,
        string? sourceKind = null,
        string? sourceId = null)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "error: a note needs something to say.";

        if (body.Length > MaxBody)
        {
            return $"error: that is {body.Length} characters. A note is an observation, not a "
                + $"transcript; keep it under {MaxBody} and put the detail in the source it "
                + "came from.";
        }

        DateTime? due = null;

        if (!string.IsNullOrWhiteSpace(dueDate))
        {
            if (!TryReadDate(dueDate, out DateTime parsed))
                return $"error: '{dueDate}' is not a date I can read. Use yyyy-MM-dd.";

            due = parsed;
        }

        try
        {
            long id = notes.Add(
                body,
                person ?? string.Empty,
                topic ?? string.Empty,
                due,
                sourceKind ?? string.Empty,
                sourceId ?? string.Empty);

            string when = due is { } date
                ? string.Create(CultureInfo.InvariantCulture, $", due {date:ddd yyyy-MM-dd}")
                : string.Empty;

            return $"noted (id {id}){when}.";
        }
        catch (Exception ex)
        {
            return $"the note could not be saved: {ex.Message}";
        }
    }

    [ShellvisTool(
        "note_search",
        SideEffect.ReadOnly,
        Description =
            "Search the notes by words, or list what is known about one person. Use it "
            + "before writing to someone or preparing for a meeting with them.",
        PreviewParameter = "query",
        Glyph = "note")]
    public string SearchNotes(
        string? query = null,
        string? person = null,
        int limit = 20)
    {
        try
        {
            IReadOnlyList<Note> found = !string.IsNullOrWhiteSpace(person)
                ? notes.About(person, Math.Clamp(limit, 1, 100))
                : notes.Search(query ?? string.Empty, Math.Clamp(limit, 1, 100));

            if (found.Count == 0)
            {
                // An empty result is an answer. Said explicitly for the same reason it is
                // said in the mail tools: this application has invented a calendar once,
                // out of a query that legitimately found nothing.
                return person is not null
                    ? $"nothing noted about '{person}'. That is the answer; do not invent one."
                    : "no notes match that. That is the answer; do not invent one.";
            }

            return Render(found, "note(s)");
        }
        catch (Exception ex)
        {
            return $"the notes could not be searched: {ex.Message}";
        }
    }

    [ShellvisTool(
        "note_due",
        SideEffect.ReadOnly,
        Description =
            "Notes that fall due on or before a date, soonest first. Defaults to the next "
            + "seven days. This is what makes 'buy the roses' arrive while there is still "
            + "time to act rather than the day after.",
        Glyph = "note")]
    public string DueNotes(string? through = null, int limit = 20)
    {
        DateTime horizon = DateTime.Today.AddDays(7);

        if (!string.IsNullOrWhiteSpace(through))
        {
            if (!TryReadDate(through, out DateTime parsed))
                return $"error: '{through}' is not a date I can read. Use yyyy-MM-dd.";

            horizon = parsed;
        }

        try
        {
            IReadOnlyList<Note> found = notes.Due(horizon, Math.Clamp(limit, 1, 100));

            return found.Count == 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"nothing is noted as due on or before {horizon:ddd yyyy-MM-dd}.")
                : Render(found, string.Create(
                    CultureInfo.InvariantCulture,
                    $"note(s) due on or before {horizon:ddd yyyy-MM-dd}"));
        }
        catch (Exception ex)
        {
            return $"the notes could not be read: {ex.Message}";
        }
    }

    [ShellvisTool(
        "note_close",
        SideEffect.Mutating,
        Description =
            "Close one note by its id, when what it was about has happened. It stops "
            + "surfacing but is kept, so 'why did you remind me about that' stays "
            + "answerable.",
        PreviewParameter = "noteId",
        Glyph = "note")]
    public string CloseNote(long noteId)
    {
        try
        {
            return notes.Close(noteId)
                ? $"note {noteId} closed."
                : $"there is no open note with id {noteId}.";
        }
        catch (Exception ex)
        {
            return $"the note could not be closed: {ex.Message}";
        }
    }

    private static string Render(IReadOnlyList<Note> found, string heading)
    {
        var sb = new StringBuilder();
        sb.Append(found.Count).Append(' ').AppendLine(heading + ":");

        foreach (Note note in found)
        {
            sb.Append("  ").Append(note);

            // The origin, so "how do you know that?" is answerable. A note whose source
            // cannot be shown is one the user has to take on trust.
            if (note.SourceKind.Length > 0)
            {
                sb.Append("  [from ").Append(note.SourceKind);

                if (note.SourceId.Length > 0)
                    sb.Append(' ').Append(note.SourceId);

                sb.Append(']');
            }

            sb.Append("   id ").AppendLine(note.Id.ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// One written form, refused otherwise.
    ///
    /// Exact and invariant. A loose parse reads "01.09.2026" and "09/01/2026" as whichever
    /// the machine's culture prefers, which is precisely what made the calendar filter
    /// return an empty week for half the days of a month.
    /// </summary>
    private static bool TryReadDate(string text, out DateTime value) =>
        DateTime.TryParseExact(
            text.Trim(),
            ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
}
