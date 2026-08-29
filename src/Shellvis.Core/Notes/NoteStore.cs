using System.Globalization;

using Microsoft.Data.Sqlite;

using Shellvis.Core.Config;

namespace Shellvis.Core.Notes;

/// <summary>One thing worth remembering about a person, a topic or a date.</summary>
/// <param name="Person">Who it is about, or empty when it is about a topic.</param>
/// <param name="Topic">What it is about, or empty.</param>
/// <param name="Due">When it needs acting on, if it does.</param>
/// <param name="SourceKind">Where it came from: "mail", "appointment", "user", "turn".</param>
/// <param name="SourceId">
/// The Outlook entry id, or whatever addresses the origin. This is what makes "how do you
/// know that?" answerable, and a note whose origin cannot be shown is a note the user has to
/// take on trust.
/// </param>
public sealed record Note(
    long Id,
    DateTime Created,
    string Person,
    string Topic,
    string Body,
    DateTime? Due,
    bool Closed,
    string SourceKind,
    string SourceId)
{
    public override string ToString()
    {
        string who = Person.Length > 0 ? $"{Person}: " : string.Empty;
        string subject = Topic.Length > 0 ? $"[{Topic}] " : string.Empty;

        string when = Due is { } date
            ? string.Create(CultureInfo.InvariantCulture, $"  (due {date:ddd yyyy-MM-dd})")
            : string.Empty;

        string late = !Closed && Due is { } deadline && deadline.Date < DateTime.Today
            ? "  OVERDUE"
            : string.Empty;

        string state = Closed ? "  [closed]" : string.Empty;

        return $"{subject}{who}{Body}{when}{late}{state}";
    }
}

/// <summary>
/// The observations a good assistant keeps: about people, topics and deadlines.
///
/// <b>Why not the memory store.</b> <c>MemoryStore</c> is capped at 2200 characters and its
/// whole content is injected into every system prompt. Notes about people and dates grow
/// without bound, so putting them there would either eat the context or turn the cap against
/// the user. These live in a database, are searched when they are relevant, and never reach
/// the prompt wholesale.
///
/// <b>Why they must not reach the prompt at all.</b> A note says things like which flowers
/// someone's wife likes and how an employee has been performing. That is exactly the
/// material that should be produced when it is needed and not carried around in the header
/// of every request, including requests that have nothing to do with the person. The
/// harness asserts this rather than trusting it.
///
/// <b>Why they surface mechanically.</b> This project has measured twice that an instruction
/// at the end of a long turn does not get followed, which is why skill writing was taken out
/// of the model's hands. The same applies here: the relevant notes are attached to the tool
/// results that mention a person, so the model does not have to remember to look.
/// </summary>
public sealed class NoteStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public NoteStore(string? path = null)
    {
        string file = path ?? Path.Combine(ShellvisPaths.Home, "notes.db");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();
        Initialise();
    }

    private void Initialise()
    {
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=2000;");

        Execute("""
            CREATE TABLE IF NOT EXISTS notes (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                created     TEXT NOT NULL,
                person      TEXT NOT NULL DEFAULT '',
                topic       TEXT NOT NULL DEFAULT '',
                body        TEXT NOT NULL,
                due         TEXT NULL,
                closed      INTEGER NOT NULL DEFAULT 0,
                source_kind TEXT NOT NULL DEFAULT '',
                source_id   TEXT NOT NULL DEFAULT ''
            );
            """);

        // Due dates are read on every reminder tick, so they get an index. Everything else
        // goes through the full-text table.
        Execute("CREATE INDEX IF NOT EXISTS notes_due ON notes(due) WHERE closed = 0;");

        Execute("""
            CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(
                person, topic, body, content='notes', content_rowid='id'
            );
            """);

        // Triggers rather than writing both tables by hand. Doing it by hand works until
        // one path forgets, and then the index drifts from the table and a note simply
        // stops being findable, with nothing to show that anything is wrong.
        Execute("""
            CREATE TRIGGER IF NOT EXISTS notes_fts_insert AFTER INSERT ON notes BEGIN
                INSERT INTO notes_fts(rowid, person, topic, body)
                VALUES (new.id, new.person, new.topic, new.body);
            END;
            """);

        Execute("""
            CREATE TRIGGER IF NOT EXISTS notes_fts_delete AFTER DELETE ON notes BEGIN
                INSERT INTO notes_fts(notes_fts, rowid, person, topic, body)
                VALUES ('delete', old.id, old.person, old.topic, old.body);
            END;
            """);

        Execute("""
            CREATE TRIGGER IF NOT EXISTS notes_fts_update AFTER UPDATE ON notes BEGIN
                INSERT INTO notes_fts(notes_fts, rowid, person, topic, body)
                VALUES ('delete', old.id, old.person, old.topic, old.body);
                INSERT INTO notes_fts(rowid, person, topic, body)
                VALUES (new.id, new.person, new.topic, new.body);
            END;
            """);
    }

    /// <summary>Write one note down.</summary>
    public long Add(
        string body,
        string person = "",
        string topic = "",
        DateTime? due = null,
        string sourceKind = "",
        string sourceId = "")
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            INSERT INTO notes (created, person, topic, body, due, closed, source_kind, source_id)
            VALUES ($created, $person, $topic, $body, $due, 0, $kind, $source);
            SELECT last_insert_rowid();
            """;

        command.Parameters.AddWithValue("$created", Iso(DateTime.Now));
        command.Parameters.AddWithValue("$person", person.Trim());
        command.Parameters.AddWithValue("$topic", topic.Trim());
        command.Parameters.AddWithValue("$body", body.Trim());
        command.Parameters.AddWithValue("$due", due is { } date ? Iso(date) : DBNull.Value);
        command.Parameters.AddWithValue("$kind", sourceKind.Trim());
        command.Parameters.AddWithValue("$source", sourceId.Trim());

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Free-text search across person, topic and body.</summary>
    public IReadOnlyList<Note> Search(string query, int limit = 20, bool includeClosed = false)
    {
        string sanitised = SanitiseFtsQuery(query);

        // An empty query after sanitising means the user typed only punctuation. Matching
        // everything would be a surprising answer to that, so it matches nothing.
        if (sanitised.Length == 0)
            return [];

        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = $"""
            SELECT n.id, n.created, n.person, n.topic, n.body, n.due, n.closed,
                   n.source_kind, n.source_id
            FROM notes_fts
            JOIN notes n ON n.id = notes_fts.rowid
            WHERE notes_fts MATCH $query
              {(includeClosed ? string.Empty : "AND n.closed = 0")}
            ORDER BY rank
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$query", sanitised);
        command.Parameters.AddWithValue("$limit", limit);

        return Read(command);
    }

    /// <summary>
    /// Notes about one person, whether or not the query words happen to match.
    ///
    /// Separate from <see cref="Search"/> because this is the lookup that runs mechanically
    /// when a mail from someone is read. It matches on the person column with a LIKE rather
    /// than through the full-text index: "Meier" must find a note filed under
    /// "Meier, Anna" and full-text tokenisation cannot be relied on for that.
    /// </summary>
    public IReadOnlyList<Note> About(string person, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(person))
            return [];

        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            SELECT id, created, person, topic, body, due, closed, source_kind, source_id
            FROM notes
            WHERE closed = 0 AND person <> '' AND (
                  person LIKE $needle OR $whole LIKE '%' || person || '%')
            ORDER BY COALESCE(due, created)
            LIMIT $limit;
            """;

        // Both directions, because the caller may know more or less than the note does.
        // A note on "Meier" must surface for a sender called "Meier, Anna <a.meier@x>",
        // and a note on "Meier, Anna" must surface when the user asks about "Meier".
        command.Parameters.AddWithValue("$needle", "%" + person.Trim() + "%");
        command.Parameters.AddWithValue("$whole", person.Trim());
        command.Parameters.AddWithValue("$limit", limit);

        return Read(command);
    }

    /// <summary>Open notes that fall due on or before a date, soonest first.</summary>
    public IReadOnlyList<Note> Due(DateTime through, int limit = 20)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            SELECT id, created, person, topic, body, due, closed, source_kind, source_id
            FROM notes
            WHERE closed = 0 AND due IS NOT NULL AND due <= $through
            ORDER BY due
            LIMIT $limit;
            """;

        // End of the given day, not its midnight. "Due through Friday" that excludes
        // everything on Friday is the kind of off-by-one nobody notices until a deadline
        // is missed, and this project has already shipped exactly that in the calendar.
        command.Parameters.AddWithValue("$through", Iso(through.Date.AddDays(1).AddTicks(-1)));
        command.Parameters.AddWithValue("$limit", limit);

        return Read(command);
    }

    /// <summary>Close one note. Returns false when there was nothing by that id.</summary>
    public bool Close(long id)
    {
        using SqliteCommand command = _connection.CreateCommand();

        // Closed rather than deleted. "Why did you remind me about the roses?" is a fair
        // question after the fact, and a deleted note cannot answer it.
        command.CommandText = "UPDATE notes SET closed = 1 WHERE id = $id AND closed = 0;";
        command.Parameters.AddWithValue("$id", id);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>How many notes are held, for a status line.</summary>
    public int Count(bool includeClosed = false)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = includeClosed
            ? "SELECT COUNT(*) FROM notes;"
            : "SELECT COUNT(*) FROM notes WHERE closed = 0;";

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<Note> Read(SqliteCommand command)
    {
        var notes = new List<Note>();

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            notes.Add(new Note(
                Id: reader.GetInt64(0),
                Created: ParseIso(reader.GetString(1)),
                Person: reader.GetString(2),
                Topic: reader.GetString(3),
                Body: reader.GetString(4),
                Due: reader.IsDBNull(5) ? null : ParseIso(reader.GetString(5)),
                Closed: reader.GetInt64(6) != 0,
                SourceKind: reader.GetString(7),
                SourceId: reader.GetString(8)));
        }

        return notes;
    }

    /// <summary>
    /// Make a human's words safe for FTS5.
    ///
    /// The same treatment the session store needs, and for the same reason: FTS5 reads
    /// quotes, asterisks, colons and parentheses as operators, so an apostrophe in
    /// "printer's" arrives as a syntax error rather than as a search. A search box that
    /// fails on ordinary punctuation is worse than none.
    /// </summary>
    private static string SanitiseFtsQuery(string query)
    {
        IEnumerable<string> tokens = (query ?? string.Empty)
            .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 0);

        return string.Join(" ", tokens.Select(t => $"\"{t}\""));
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Iso(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseIso(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed)
            ? parsed
            : DateTime.MinValue;

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}
