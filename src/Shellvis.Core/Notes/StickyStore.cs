using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Shellvis.Core.Notes;

/// <summary>The classic colours, and what they are for.</summary>
/// <remarks>
/// Five, as in Windows 7's Sticky Notes: yellow, blue, green, pink, purple. Not a palette
/// picker. The point of the colours is that a glance at the desktop separates one kind of
/// note from another, and that works with five and stops working with a hundred.
/// </remarks>
public enum StickyColour
{
    Yellow,
    Blue,
    Green,
    Pink,
    Purple,
}

/// <summary>One note stuck to the desktop.</summary>
/// <param name="X">Screen position in device pixels. Negative is a monitor left of the main one.</param>
/// <param name="NoteId">The note it came from, when the assistant wrote it. Null for a hand-written one.</param>
public sealed record Sticky(
    long Id,
    string Text,
    StickyColour Colour,
    int X,
    int Y,
    int Width,
    int Height,
    DateTime Created,
    DateTime Updated,
    long? NoteId)
{
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Colour} at {X},{Y} ({Width}x{Height})  \"{Preview()}\"  {Updated:yyyy-MM-dd HH:mm}");

    private string Preview()
    {
        string flat = Text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 50 ? flat : flat[..50] + "...";
    }
}

/// <summary>
/// Sticky notes on the desktop, and the state that makes them stick.
///
/// <b>What the Vista feature actually was, and what carries over.</b> Three programs get
/// remembered as one: the XP Tablet Edition ink tool, Vista's Sidebar Notes gadget and the
/// Tablet PC Sticky Notes with its automatic timestamp, and Windows 7's StikyNot.exe, which
/// is the yellow square most people mean. What they share is the behaviour, and the
/// behaviour is what is being rebuilt: one frameless window per note, no taskbar button and
/// no Alt-Tab entry, always on top but unobtrusive, dragged from anywhere, resized from a
/// corner, saved without being asked, and surviving a restart with its position, size and
/// colour. A note you have to save is a document, not a note.
///
/// <b>Why the store is here and not in the shell.</b> Everything above is state, and state
/// belongs where it can be tested without a window. The window is the part that has to wait
/// for a screen; the promise that a note comes back where it was put does not.
///
/// <b>Why the same database as the notes.</b> A sticky and a note are the same material seen
/// two ways: one is what the assistant knows, the other is what the user wants in front of
/// them. Keeping the link means a note the assistant wrote can be put on the desktop and
/// still answer "where did this come from".
///
/// One idea taken from Zhorn's Stickies, which is the most mature program of this kind
/// though it is proprietary freeware rather than open source: a note can sleep until a date
/// and reappear then. Here that is the same due date the note database already keeps, so it
/// costs nothing to honour.
/// </summary>
public sealed partial class NoteStore
{
    /// <summary>The size a new note gets, in DIP.</summary>
    /// <remarks>
    /// A little wider than tall, because notes are written in sentences rather than in
    /// columns. Small enough that several fit on a desktop without becoming a wall.
    /// </remarks>
    public const int DefaultWidth = 220;

    public const int DefaultHeight = 180;

    private void InitialiseStickies()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS stickies (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                text     TEXT NOT NULL,
                colour   TEXT NOT NULL DEFAULT 'Yellow',
                x        INTEGER NOT NULL DEFAULT 0,
                y        INTEGER NOT NULL DEFAULT 0,
                width    INTEGER NOT NULL,
                height   INTEGER NOT NULL,
                created  TEXT NOT NULL,
                updated  TEXT NOT NULL,
                note_id  INTEGER NULL
            );
            """);
    }

    /// <summary>Stick a note to the desktop.</summary>
    public Sticky Stick(
        string text,
        StickyColour colour = StickyColour.Yellow,
        int? x = null,
        int? y = null,
        long? noteId = null)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            INSERT INTO stickies (text, colour, x, y, width, height, created, updated, note_id)
            VALUES ($text, $colour, $x, $y, $w, $h, $now, $now, $note);
            SELECT last_insert_rowid();
            """;

        // A position of nothing means "the shell decides", and the shell puts it where the
        // cursor is. Stored as 0,0 rather than null so the column stays simple; a note
        // genuinely at 0,0 is indistinguishable and lands in the same place anyway.
        command.Parameters.AddWithValue("$text", text.Trim());
        command.Parameters.AddWithValue("$colour", colour.ToString());
        command.Parameters.AddWithValue("$x", x ?? 0);
        command.Parameters.AddWithValue("$y", y ?? 0);
        command.Parameters.AddWithValue("$w", DefaultWidth);
        command.Parameters.AddWithValue("$h", DefaultHeight);
        command.Parameters.AddWithValue("$now", Iso(DateTime.Now));
        command.Parameters.AddWithValue("$note", noteId.HasValue ? noteId.Value : DBNull.Value);

        long id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        return Sticky(id)!;
    }

    /// <summary>Every note on the desktop, oldest first so they layer as they were made.</summary>
    public IReadOnlyList<Sticky> Stickies()
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            SELECT id, text, colour, x, y, width, height, created, updated, note_id
            FROM stickies
            ORDER BY created;
            """;

        return ReadStickies(command);
    }

    /// <summary>One note, or null.</summary>
    public Sticky? Sticky(long id)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            SELECT id, text, colour, x, y, width, height, created, updated, note_id
            FROM stickies WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$id", id);

        return ReadStickies(command).FirstOrDefault();
    }

    /// <summary>
    /// Save whatever changed. Nothing here is optional to call.
    ///
    /// Written on every edit and every move rather than on a Save command, because a note
    /// that has to be saved is a document. The cost is a small write per drag, which SQLite
    /// in WAL mode does not notice.
    /// </summary>
    public bool Update(
        long id,
        string? text = null,
        StickyColour? colour = null,
        int? x = null,
        int? y = null,
        int? width = null,
        int? height = null)
    {
        using SqliteCommand command = _connection.CreateCommand();

        // COALESCE so a caller updating only the position does not have to read the text
        // first and write it back. Reading and rewriting is how a concurrent edit gets lost.
        command.CommandText = """
            UPDATE stickies SET
                text    = COALESCE($text, text),
                colour  = COALESCE($colour, colour),
                x       = COALESCE($x, x),
                y       = COALESCE($y, y),
                width   = COALESCE($w, width),
                height  = COALESCE($h, height),
                updated = $now
            WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$text", text is null ? DBNull.Value : text);
        command.Parameters.AddWithValue("$colour", colour is null ? DBNull.Value : colour.ToString()!);
        command.Parameters.AddWithValue("$x", x.HasValue ? x.Value : DBNull.Value);
        command.Parameters.AddWithValue("$y", y.HasValue ? y.Value : DBNull.Value);
        command.Parameters.AddWithValue("$w", width.HasValue ? width.Value : DBNull.Value);
        command.Parameters.AddWithValue("$h", height.HasValue ? height.Value : DBNull.Value);
        command.Parameters.AddWithValue("$now", Iso(DateTime.Now));
        command.Parameters.AddWithValue("$id", id);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Throw one away.
    ///
    /// Deleted, unlike a note, which is only closed. A sticky IS its window: once it is off
    /// the desktop there is nothing left of it to look at, so keeping the row would be
    /// keeping something with no way to see it. The note it came from, if it came from one,
    /// is untouched and still holds the content.
    /// </summary>
    public bool Unstick(long id)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = "DELETE FROM stickies WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        return command.ExecuteNonQuery() > 0;
    }

    private static List<Sticky> ReadStickies(SqliteCommand command)
    {
        var stickies = new List<Sticky>();

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            stickies.Add(new Sticky(
                Id: reader.GetInt64(0),
                Text: reader.GetString(1),
                Colour: ParseColour(reader.GetString(2)),
                X: reader.GetInt32(3),
                Y: reader.GetInt32(4),
                Width: reader.GetInt32(5),
                Height: reader.GetInt32(6),
                Created: ParseIso(reader.GetString(7)),
                Updated: ParseIso(reader.GetString(8)),
                NoteId: reader.IsDBNull(9) ? null : reader.GetInt64(9)));
        }

        return stickies;
    }

    /// <summary>
    /// A colour name, or yellow.
    ///
    /// A stored value that no longer parses must not make the note disappear. A downgrade
    /// after a colour was removed, or a hand-edited database, should cost the colour and
    /// not the content.
    /// </summary>
    public static StickyColour ParseColour(string? name) =>
        Enum.TryParse(name, ignoreCase: true, out StickyColour colour) ? colour : StickyColour.Yellow;
}
