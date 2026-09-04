using System.Globalization;

using Microsoft.Data.Sqlite;

using Shellvis.Core.Config;

namespace Shellvis.Core.Desk;

/// <summary>
/// The desk, remembered.
///
/// <b>What this is for.</b> Every question about a mail, a ticket or a task currently costs a
/// round trip through COM or HTTP, and the answer arrives with no memory of anything learned
/// about it before. So the same notification gets read three times in a week and produces
/// three summaries, none of which knows about the other two. This is the store that makes the
/// second look cheaper than the first, and better: an object is written once with what the
/// source knows about it, and enriched afterwards with what the assistant worked out.
///
/// <b>Three months, then gone.</b> A desk is not an archive. Outlook keeps the mail; this
/// keeps what was understood about it while it mattered, and lets that expire -- because an
/// enrichment about a ticket that closed in June is worse than nothing in September: it reads
/// as current and is not. <see cref="Prune"/> is called on every indexing pass, so the
/// retention needs no scheduled job to enforce it.
///
/// <b>Sightings, not inserts.</b> <see cref="See"/> is an upsert that keeps the enrichment
/// and refreshes everything the source owns. That split is the reason the cache is worth
/// having: the subject and the read flag belong to Outlook and are overwritten without
/// hesitation, while <c>enrichment</c> belongs to this assistant and is never overwritten by
/// an indexing pass -- only by something that deliberately writes it.
///
/// <b>Why not the notes store.</b> Notes are about people and topics and are written by hand;
/// this is a machine-filled index of things that already exist elsewhere, with its own
/// retention and its own identity rules. Sharing one table would mean one of the two has to
/// give up its keying, and neither can.
/// </summary>
public sealed class DeskStore : IDisposable
{
    /// <summary>How long a thing is remembered after it was last seen.</summary>
    /// <remarks>
    /// Three months, as asked for, expressed in days so a quarter is a quarter rather than
    /// whatever "three months" means from the 31st.
    /// </remarks>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(92);

    private readonly SqliteConnection _connection;

    /// <summary>
    /// How long anything is kept, taken from the configuration at construction.
    ///
    /// A property rather than a parameter on every Prune call: the retention is a property
    /// of the store, and threading it through each caller is how one caller ends up using
    /// the default while the setting says otherwise.
    /// </summary>
    public TimeSpan Retention { get; }

    public DeskStore(string? path = null, TimeSpan? retention = null)
    {
        Retention = retention is { } keep && keep > TimeSpan.FromDays(1)
            ? keep
            : DefaultRetention;

        string file = path ?? Path.Combine(ShellvisPaths.Home, "desk.db");
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
            CREATE TABLE IF NOT EXISTS objects (
                id          TEXT PRIMARY KEY,
                kind        TEXT NOT NULL,
                subject     TEXT NOT NULL DEFAULT '',
                who_name    TEXT NOT NULL DEFAULT '',
                who_address TEXT NOT NULL DEFAULT '',
                happened    TEXT NOT NULL,
                due         TEXT NULL,
                state       TEXT NOT NULL DEFAULT '',
                ticket_key  TEXT NULL,
                thread      TEXT NULL,
                entry_id    TEXT NULL,
                facts       TEXT NULL,
                enrichment  TEXT NULL,
                first_seen  TEXT NOT NULL,
                last_seen   TEXT NOT NULL
            );
            """);

        // The three questions this store is actually asked: what is recent, what is about
        // this ticket, and what else is in this conversation. Each gets an index; nothing
        // else does, because an index on a column nobody filters by is a write cost with
        // no reader.
        Execute("CREATE INDEX IF NOT EXISTS objects_when ON objects(happened DESC);");
        Execute("CREATE INDEX IF NOT EXISTS objects_ticket ON objects(ticket_key) WHERE ticket_key IS NOT NULL;");
        Execute("CREATE INDEX IF NOT EXISTS objects_thread ON objects(thread) WHERE thread IS NOT NULL;");

        // Links are their own table rather than a column of ids.
        //
        // A mail is about a ticket AND part of a conversation AND sometimes the reason a
        // task exists. Encoding that as a list in a text column means every read has to
        // parse it and every write has to rewrite the whole list, and a half-written list
        // loses relationships silently. A row per relationship cannot half-exist.
        Execute("""
            CREATE TABLE IF NOT EXISTS links (
                from_id  TEXT NOT NULL,
                to_id    TEXT NOT NULL,
                relation TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (from_id, to_id, relation)
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS links_to ON links(to_id);");

        Execute("""
            CREATE VIRTUAL TABLE IF NOT EXISTS objects_fts USING fts5(
                subject, who_name, who_address, enrichment,
                content='objects', content_rowid='rowid'
            );
            """);

        // Triggers rather than writing both tables by hand, the same decision the note
        // store made and for the same reason: a path that forgets leaves the index drifting
        // from the table, and then a thing simply stops being findable with nothing to show
        // that anything is wrong.
        Execute("""
            CREATE TRIGGER IF NOT EXISTS objects_fts_insert AFTER INSERT ON objects BEGIN
                INSERT INTO objects_fts(rowid, subject, who_name, who_address, enrichment)
                VALUES (new.rowid, new.subject, new.who_name, new.who_address, coalesce(new.enrichment, ''));
            END;
            """);

        Execute("""
            CREATE TRIGGER IF NOT EXISTS objects_fts_delete AFTER DELETE ON objects BEGIN
                INSERT INTO objects_fts(objects_fts, rowid, subject, who_name, who_address, enrichment)
                VALUES ('delete', old.rowid, old.subject, old.who_name, old.who_address, coalesce(old.enrichment, ''));
            END;
            """);

        Execute("""
            CREATE TRIGGER IF NOT EXISTS objects_fts_update AFTER UPDATE ON objects BEGIN
                INSERT INTO objects_fts(objects_fts, rowid, subject, who_name, who_address, enrichment)
                VALUES ('delete', old.rowid, old.subject, old.who_name, old.who_address, coalesce(old.enrichment, ''));
                INSERT INTO objects_fts(rowid, subject, who_name, who_address, enrichment)
                VALUES (new.rowid, new.subject, new.who_name, new.who_address, coalesce(new.enrichment, ''));
            END;
            """);
    }

    /// <summary>
    /// Record that this thing exists, keeping anything the assistant has added to it.
    /// </summary>
    /// <remarks>
    /// <b>The enrichment is not passed in and cannot be cleared here.</b> An indexing pass
    /// knows what Outlook knows and nothing else; if it were allowed to write the
    /// enrichment column it would write null, and three months of understanding would be
    /// erased by a routine sweep. <see cref="Enrich"/> is the only way in.
    /// </remarks>
    /// <returns>True when this was the first sighting.</returns>
    public bool See(DeskObject thing)
    {
        // Asked before the write, and deliberately not derived from it.
        //
        // The first version returned "first_seen = last_seen" out of the upsert, which is
        // wrong exactly when it matters: an indexing pass stamps every object with one
        // timestamp, so on the second sighting the two columns are equal and every sighting
        // reports itself as the first. One extra SELECT against a primary key costs nothing
        // and cannot be subtly wrong.
        bool known = Exists(thing.Id);

        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            INSERT INTO objects (
                id, kind, subject, who_name, who_address, happened, due, state,
                ticket_key, thread, entry_id, facts, enrichment, first_seen, last_seen)
            VALUES (
                $id, $kind, $subject, $whoName, $whoAddress, $happened, $due, $state,
                $ticketKey, $thread, $entryId, $facts, NULL, $seen, $seen)
            ON CONFLICT(id) DO UPDATE SET
                subject     = excluded.subject,
                who_name    = excluded.who_name,
                who_address = excluded.who_address,
                happened    = excluded.happened,
                due         = excluded.due,
                state       = excluded.state,
                ticket_key  = coalesce(excluded.ticket_key, objects.ticket_key),
                thread      = coalesce(excluded.thread, objects.thread),
                entry_id    = coalesce(excluded.entry_id, objects.entry_id),
                facts       = coalesce(excluded.facts, objects.facts),
                last_seen   = excluded.last_seen;
            """;

        command.Parameters.AddWithValue("$id", thing.Id);
        command.Parameters.AddWithValue("$kind", DeskObject.Prefix(thing.Kind));
        command.Parameters.AddWithValue("$subject", thing.Subject);
        command.Parameters.AddWithValue("$whoName", thing.WhoName);
        command.Parameters.AddWithValue("$whoAddress", thing.WhoAddress);
        command.Parameters.AddWithValue("$happened", Text(thing.When));
        command.Parameters.AddWithValue("$due", thing.Due is { } due ? Text(due) : DBNull.Value);
        command.Parameters.AddWithValue("$state", thing.State);
        command.Parameters.AddWithValue("$ticketKey", thing.TicketKey ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$thread", thing.Thread ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$entryId", thing.EntryId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$facts", thing.Facts ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$seen", Text(thing.LastSeen));

        command.ExecuteNonQuery();

        return !known;
    }

    /// <summary>Whether this id is already held.</summary>
    private bool Exists(string id)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM objects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        return command.ExecuteScalar() is not null;
    }

    /// <summary>Add what the assistant worked out, appended rather than replaced.</summary>
    /// <remarks>
    /// Appended, because understanding accumulates: the first look says what a ticket is
    /// about, the second says what was decided, and replacing the first with the second
    /// loses the half that explains the other. Each line carries its date so a reader can
    /// tell what is old.
    /// </remarks>
    public void Enrich(string id, string text, DateTime when)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            UPDATE objects
            SET enrichment = CASE
                WHEN enrichment IS NULL OR enrichment = '' THEN $line
                ELSE enrichment || char(10) || $line
            END
            WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$id", id);

        command.Parameters.AddWithValue(
            "$line",
            when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "  " + text.Trim());

        command.ExecuteNonQuery();
    }

    /// <summary>Note that one thing relates to another.</summary>
    public void Link(string fromId, string toId, string relation)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            INSERT OR IGNORE INTO links (from_id, to_id, relation)
            VALUES ($from, $to, $relation);
            """;

        command.Parameters.AddWithValue("$from", fromId);
        command.Parameters.AddWithValue("$to", toId);
        command.Parameters.AddWithValue("$relation", relation);

        command.ExecuteNonQuery();
    }

    /// <summary>One thing by id, or null.</summary>
    public DeskObject? Get(string id)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = Select + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>What this thing is linked to, in both directions.</summary>
    /// <remarks>
    /// Both directions on purpose. A mail names a ticket, so the link is written from the
    /// mail; asked about the ticket, the interesting answer is that mail. A store that only
    /// followed links forwards would answer "nothing" to the more useful of the two
    /// questions.
    /// </remarks>
    public IReadOnlyList<DeskObject> Related(string id, int limit = 25)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = Select + """
             WHERE id IN (
                SELECT to_id FROM links WHERE from_id = $id
                UNION
                SELECT from_id FROM links WHERE to_id = $id
             )
             ORDER BY happened DESC
             LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$limit", limit);

        return ReadAll(command);
    }

    /// <summary>Everything about one ticket: the ticket, and the mail that mentioned it.</summary>
    public IReadOnlyList<DeskObject> AboutTicket(string key, int limit = 25)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = Select + """
             WHERE ticket_key = $key OR id = $id
             ORDER BY happened DESC
             LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$key", key.ToUpperInvariant());
        command.Parameters.AddWithValue("$id", DeskObject.MakeId(DeskKind.Ticket, key));
        command.Parameters.AddWithValue("$limit", limit);

        return ReadAll(command);
    }

    /// <summary>
    /// Free-text search over subjects, senders and the assistant's own notes.
    /// </summary>
    /// <param name="since">
    /// How far back to look. This is the window the slider sets, and it is a parameter
    /// rather than a constant because remembering three months and being reminded about
    /// three months are different things: the store keeps a quarter, and how much of it is
    /// brought to bear on a question is the reader's choice.
    /// </param>
    public IReadOnlyList<DeskObject> Search(string query, DateTime? since = null, int limit = 25)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            SELECT o.id, o.kind, o.subject, o.who_name, o.who_address, o.happened, o.due,
                   o.state, o.ticket_key, o.thread, o.entry_id, o.facts, o.enrichment,
                   o.first_seen, o.last_seen
            FROM objects_fts f
            JOIN objects o ON o.rowid = f.rowid
            WHERE objects_fts MATCH $query
              AND ($since IS NULL OR o.happened >= $since)
            ORDER BY o.happened DESC
            LIMIT $limit;
            """;

        // Quoted as a phrase, then loosened with a trailing star on the last token. A raw
        // string reaches FTS5 as a query language: an unbalanced quote or a bare NEAR is a
        // syntax error, and a search that throws on a subject somebody typed is worse than
        // a search that finds a little too much.
        command.Parameters.AddWithValue("$query", Fts(query));
        command.Parameters.AddWithValue("$since", since is { } from ? Text(from) : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        return ReadAll(command);
    }

    /// <summary>The most recent things, whatever they are.</summary>
    public IReadOnlyList<DeskObject> Recent(DateTime since, int limit = 40)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = Select + " WHERE happened >= $since ORDER BY happened DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$since", Text(since));
        command.Parameters.AddWithValue("$limit", limit);

        return ReadAll(command);
    }

    /// <summary>How many things are held, for the page and the harness.</summary>
    public int Count()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM objects;";

        return command.ExecuteScalar() is long count ? (int)count : 0;
    }

    /// <summary>The oldest thing still held, so the page can say how far back it goes.</summary>
    public DateTime? Oldest()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT min(happened) FROM objects;";

        return command.ExecuteScalar() is string text
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime when)
                ? when
                : null;
    }

    /// <summary>
    /// Forget what is older than the retention, links included.
    /// </summary>
    /// <returns>How many things were forgotten.</returns>
    public int Prune(DateTime now, TimeSpan? keep = null)
    {
        string cutoff = Text(now - (keep ?? Retention));

        using SqliteCommand command = _connection.CreateCommand();

        // The links go first and by hand: SQLite enforces no foreign keys unless asked, and
        // a link whose ends have been deleted is a row that answers "related" with a thing
        // that is not there any more.
        command.CommandText = """
            DELETE FROM links WHERE from_id IN (SELECT id FROM objects WHERE happened < $cutoff)
                                 OR to_id   IN (SELECT id FROM objects WHERE happened < $cutoff);

            DELETE FROM objects WHERE happened < $cutoff;

            SELECT changes();
            """;

        command.Parameters.AddWithValue("$cutoff", cutoff);

        return command.ExecuteScalar() is long gone ? (int)gone : 0;
    }

    private const string Select = """
        SELECT id, kind, subject, who_name, who_address, happened, due, state,
               ticket_key, thread, entry_id, facts, enrichment, first_seen, last_seen
        FROM objects
        """;

    private IReadOnlyList<DeskObject> ReadAll(SqliteCommand command)
    {
        var found = new List<DeskObject>();

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
            found.Add(Read(reader));

        return found;
    }

    private static DeskObject Read(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Kind: DeskObject.KindOf(reader.GetString(0)) ?? DeskKind.Mail,
        Subject: reader.GetString(2),
        WhoName: reader.GetString(3),
        WhoAddress: reader.GetString(4),
        When: When(reader, 5) ?? DateTime.MinValue,
        Due: When(reader, 6),
        State: reader.GetString(7),
        TicketKey: reader.IsDBNull(8) ? null : reader.GetString(8),
        Thread: reader.IsDBNull(9) ? null : reader.GetString(9),
        EntryId: reader.IsDBNull(10) ? null : reader.GetString(10),
        Facts: reader.IsDBNull(11) ? null : reader.GetString(11),
        Enrichment: reader.IsDBNull(12) ? null : reader.GetString(12),
        FirstSeen: When(reader, 13) ?? DateTime.MinValue,
        LastSeen: When(reader, 14) ?? DateTime.MinValue);

    private static DateTime? When(SqliteDataReader reader, int column) =>
        !reader.IsDBNull(column)
        && DateTime.TryParse(
            reader.GetString(column), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value)
            ? value
            : null;

    /// <summary>
    /// A date as text, sortable and culture-proof.
    /// </summary>
    /// <remarks>
    /// Round-trip format, so string comparison in SQL is chronological comparison. This is
    /// the same trap the mail filters met from the other side: a date written in the user's
    /// short format sorts alphabetically and therefore wrongly, and a filter built on it
    /// looks right for eleven days of every month.
    /// </remarks>
    private static string Text(DateTime when) =>
        when.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Turn what somebody typed into something FTS5 will accept.</summary>
    private static string Fts(string query)
    {
        string[] words = query
            .Split([' ', '\t', '\r', '\n', ',', ';', ':', '"', '\'', '(', ')', '*'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1)
            .Take(8)
            .ToArray();

        if (words.Length == 0)
            return "\"\"";

        // Every word quoted, so nothing in it is read as an operator, and the last one gets
        // a prefix star: somebody searching "perform" means "performance" too.
        return string.Join(" AND ", words.Select((w, i) =>
            i == words.Length - 1 ? $"\"{w}\"*" : $"\"{w}\""));
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
