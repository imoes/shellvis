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
                verdict     TEXT NULL,
                verdict_why TEXT NULL,
                verdict_at  TEXT NULL,
                first_seen  TEXT NOT NULL,
                last_seen   TEXT NOT NULL
            );
            """);

        // The verdict columns arrived after the table did, and there are real rows in it
        // by now. CREATE TABLE IF NOT EXISTS does nothing to an existing table, so without
        // this the new columns would be missing on every machine that has already run
        // Shellvis -- and every query naming them would fail at runtime, which is a crash
        // rather than a defect somebody has to notice.
        AddMissingColumns("objects", new[]
        {
            ("verdict", "TEXT NULL"),
            ("verdict_why", "TEXT NULL"),
            ("verdict_at", "TEXT NULL"),

            // Whether the pass that wrote the verdict could see the message's text.
            //
            // Not a curiosity: the sorting was given sender and subject only for its first
            // several versions, so the best reason it could write was the subject in other
            // words -- "Zwischenmeldung eines Ticket-Alerts von Telekom" for a mail whose
            // subject says exactly that. Those verdicts are kept for three months, so
            // without a way to tell them apart the trays would go on showing paraphrased
            // subject lines until they aged out.
            //
            // Defaulting to 0 is what makes the existing rows re-judgeable, and the flag is
            // set for every verdict written by a body-reading pass -- whether or not that
            // particular message still had a body to read. Otherwise a mail with no text at
            // all would be re-judged for ever.
            ("verdict_body", "INTEGER NOT NULL DEFAULT 0"),

            // Which generation of the sorting rules produced the verdict, and the single
            // test for whether it is still current. Zero for everything judged before this
            // column existed, so a first run after an upgrade re-reads the lot.
            //
            // verdict_body above is kept because it is a different fact -- whether the text
            // was actually there to read -- and it is worth seeing in the store. It is no
            // longer what decides a re-read: a rule can change without the body changing,
            // and it has.
            ("verdict_rules", "INTEGER NOT NULL DEFAULT 0"),

            // The earlier thing this one is about, when the sorting pass recognised one:
            // the same request made a fortnight ago, an earlier mail on the same matter.
            // A column rather than a link row, because it is part of the verdict -- written
            // with it, overwritten with it, and shown beside it -- and a link table entry
            // would outlive the verdict it explained.
            ("related", "TEXT NULL"),
        });

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
    /// Add columns a newer version needs to a table an older version created.
    ///
    /// The smallest migration that works: ask the table what it has, add what it lacks.
    /// SQLite has no ADD COLUMN IF NOT EXISTS, and running a bare ADD COLUMN twice is an
    /// error -- so the check has to be explicit rather than swallowed by a try.
    /// </summary>
    private void AddMissingColumns(string table, IEnumerable<(string Name, string Type)> wanted)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        using (SqliteCommand ask = _connection.CreateCommand())
        {
            ask.CommandText = $"PRAGMA table_info({table});";

            using SqliteDataReader reader = ask.ExecuteReader();

            while (reader.Read())
                present.Add(reader.GetString(1));
        }

        foreach ((string name, string type) in wanted)
        {
            if (present.Contains(name))
                continue;

            Execute($"ALTER TABLE {table} ADD COLUMN {name} {type};");
        }
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

    /// <summary>
    /// Write what the assistant decided this needs: an answer, a read, or nothing.
    /// </summary>
    /// <remarks>
    /// <b>Judged once and remembered, which is the whole reason this is a column.</b> A
    /// verdict is a model call, and a model call on this machine takes seconds a mail. Four
    /// hundred unread messages cannot be judged on every look; they can be judged once each,
    /// and then the answer is free for as long as the row lives.
    ///
    /// Like the enrichment and for the same reason, an indexing pass cannot clear it: the
    /// subject and the read flag belong to Outlook, this belongs to the assistant.
    ///
    /// <b>Overwritten rather than appended</b> -- unlike an enrichment. An enrichment
    /// accumulates because understanding does; a verdict is a current answer to "what should
    /// happen with this", and two of them is not richer, it is ambiguous.
    /// </remarks>
    /// <param name="sawBody">
    /// Whether the pass that produced this verdict reads message bodies. True for every
    /// verdict from such a pass, even for a message that had no text left to read -- the
    /// flag records what the pass could see, not what it happened to find, because a mail
    /// with an empty body would otherwise be re-judged for ever.
    /// </param>
    /// <param name="rules">
    /// The generation of the sorting rules this verdict was made under. Compared against the
    /// current one to decide what needs re-reading, so a rule change reaches the mail that
    /// is already judged instead of only the mail that arrives next.
    /// </param>
    /// <param name="related">
    /// The id of the earlier thing this one is about, when the pass recognised one among
    /// the candidates it was shown; null clears it. Overwritten with the verdict, because
    /// it is part of the verdict.
    /// </param>
    public void Judge(
        string id,
        DeskVerdict verdict,
        string why,
        DateTime when,
        bool sawBody = false,
        int rules = 0,
        string? related = null)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            UPDATE objects
            SET verdict = $verdict,
                verdict_why = $why,
                verdict_at = $at,
                verdict_body = $body,
                verdict_rules = $rules,
                related = $related
            WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$verdict", verdict.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$why", why.Trim());
        command.Parameters.AddWithValue("$at", Text(when));
        command.Parameters.AddWithValue("$body", sawBody ? 1 : 0);
        command.Parameters.AddWithValue("$rules", rules);
        command.Parameters.AddWithValue("$related", related is { Length: > 0 } ? related : DBNull.Value);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Mail that carries a verdict written without its text, oldest verdict first.
    /// </summary>
    /// <remarks>
    /// <b>Separate from <see cref="Unjudged"/> on purpose.</b> These rows are judged: they
    /// appear in their trays and are counted under their verdict, and folding them into the
    /// unjudged count would make the page report a backlog that is not one.
    ///
    /// They are worth revisiting all the same. The verdict itself is probably right --
    /// sender and subject are enough to tell a notification from a question -- but the
    /// sentence beside it is a paraphrase of the subject, and that sentence is the only
    /// thing on the row the assistant contributes.
    ///
    /// Oldest verdict first, so a re-sorting pass works forward through the backlog instead
    /// of circling the same handful.
    /// </remarks>
    public IReadOnlyList<DeskObject> JudgedUnderOldRules(DateTime since, int rules, int limit = 10)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = Select + """
             WHERE verdict IS NOT NULL
               AND verdict_rules < $rules
               AND kind = 'mail'
               AND state <> 'read'
               AND happened >= $since
             ORDER BY verdict_at ASC
             LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$since", Text(since));
        command.Parameters.AddWithValue("$rules", rules);
        command.Parameters.AddWithValue("$limit", limit);

        return ReadAll(command);
    }

    /// <summary>How many verdicts were made under rules older than the current ones.</summary>
    public int StaleVerdictCount(DateTime since, int rules)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*) FROM objects
            WHERE verdict IS NOT NULL
              AND verdict_rules < $rules
              AND kind = 'mail'
              AND state <> 'read'
              AND happened >= $since;
            """;

        command.Parameters.AddWithValue("$since", Text(since));
        command.Parameters.AddWithValue("$rules", rules);

        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Mail that has not been judged yet, newest first.
    /// </summary>
    /// <remarks>
    /// Only mail, and only what is still unread: a judged verdict on a message somebody has
    /// already dealt with is a model call spent on the past. Newest first because that is
    /// where an unanswered question is most likely to be.
    /// </remarks>
    public IReadOnlyList<DeskObject> Unjudged(DateTime since, int limit = 10)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = Select + """
             WHERE verdict IS NULL
               AND kind = 'mail'
               AND state <> 'read'
               AND happened >= $since
             ORDER BY happened DESC
             LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$since", Text(since));
        command.Parameters.AddWithValue("$limit", limit);

        return ReadAll(command);
    }

    /// <summary>Everything with one verdict, newest first.</summary>
    public IReadOnlyList<DeskObject> Judged(DeskVerdict verdict, DateTime since, int limit = 25)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = Select + """
             WHERE verdict = $verdict
               AND state <> 'read'
               AND happened >= $since
             ORDER BY happened DESC
             LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$verdict", verdict.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$since", Text(since));
        command.Parameters.AddWithValue("$limit", limit);

        return ReadAll(command);
    }

    /// <summary>
    /// How many unread things carry each verdict, and how many are still unjudged.
    /// </summary>
    /// <remarks>
    /// One query rather than four. The page shows all of these side by side, and four
    /// round trips to answer one question is how a page that refreshes on a timer starts
    /// costing something.
    /// </remarks>
    /// <remarks>
    /// <b>The caller passes the retention horizon, not the remembering window.</b> That
    /// distinction cost a real defect: the trays and the sorting were bounded by the window
    /// the slider sets -- two weeks -- so a backlog of fifty-five unread messages aged
    /// fifteen to thirty days was skipped for ever. The page then showed 85 unread beside
    /// four zeroes, both correctly calculated and together useless.
    ///
    /// Unread is unread, whatever its age. The window governs what "lately" means when
    /// somebody ASKS a question -- desk_search, desk_recent -- and nothing else.
    /// </remarks>
    public DeskTally Tally(DateTime since)
    {
        using SqliteCommand command = _connection.CreateCommand();

        command.CommandText = """
            SELECT coalesce(verdict, 'pending') AS v, count(*)
            FROM objects
            WHERE kind = 'mail' AND state <> 'read' AND happened >= $since
            GROUP BY v;
            """;

        command.Parameters.AddWithValue("$since", Text(since));

        int answer = 0, information = 0, ignore = 0, pending = 0;

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            int howMany = reader.GetInt32(1);

            switch (reader.GetString(0))
            {
                case "answer": answer = howMany; break;
                case "information": information = howMany; break;
                case "ignore": ignore = howMany; break;
                default: pending = howMany; break;
            }
        }

        return new DeskTally(answer, information, ignore, pending);
    }

    /// <summary>
    /// Everything in this period that the mailbox no longer lists as unread has been read.
    /// </summary>
    /// <param name="stillUnread">The ids the walk just saw in the unread set.</param>
    /// <param name="from">
    /// The oldest moment the walk actually looked at. Rows older than this were not in the
    /// scan, so their absence from <paramref name="stillUnread"/> means nothing.
    /// </param>
    /// <returns>How many rows were marked read.</returns>
    /// <remarks>
    /// <b>Without this the store's idea of "unread" only ever grows.</b> The walk enumerates
    /// the UNREAD items, so a message that has since been read is never seen again and keeps
    /// the state it was first written with -- for the three months the row lives. Measured on
    /// a real mailbox: the folder reported 85 unread while the store counted 399, and every
    /// number derived from it was wrong by that difference.
    ///
    /// <b>Bounded by the scan, which is the part that has to be right.</b> The classification
    /// looks at the newest two hundred, so "not in the unread set" is only evidence for
    /// messages inside that range. Applying it further back would mark a genuinely unread
    /// message from last month as read because the scan never reached it -- the same class of
    /// mistake in the other direction, and a worse one: it would hide something.
    /// </remarks>
    public int MarkRead(IReadOnlySet<string> stillUnread, DateTime from)
    {
        using SqliteCommand command = _connection.CreateCommand();

        // The ids go in as a JSON array and are read back with json_each rather than being
        // pasted into the SQL. Two hundred ids concatenated into a statement is a statement
        // that breaks on the first apostrophe, and a message id may contain one.
        command.CommandText = """
            UPDATE objects
            SET state = 'read'
            WHERE kind = 'mail'
              AND state <> 'read'
              AND happened >= $from
              AND id NOT IN (SELECT value FROM json_each($ids));
            """;

        command.Parameters.AddWithValue("$from", Text(from));

        command.Parameters.AddWithValue(
            "$ids",
            System.Text.Json.JsonSerializer.Serialize(stillUnread));

        return command.ExecuteNonQuery();
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
                   o.first_seen, o.last_seen, o.verdict, o.verdict_why, o.related
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

    /// <summary>
    /// What the desk already holds about the matter of one thing: the same request made
    /// before, the mail that confirmed the order it asks about, the ticket it names.
    /// </summary>
    /// <remarks>
    /// <b>By distinctive word, not by phrase.</b> <see cref="Search"/> treats a query as a
    /// phrase, which is right for a person typing and wrong here: the earlier request was
    /// worded differently, and the confirmation of an order shares only the order number
    /// with the question about it. So each keyword of the subject is searched on its own and
    /// the results are ranked by how many of the words they share, then by recency.
    ///
    /// <b>Not bounded to what came before.</b> "Earlier" is the desk's word for "already
    /// held", and the mail that settles a question may have arrived after the question did:
    /// an unread "wo bleibt meine Bestellung?" from Monday is answered by Tuesday's
    /// confirmation, and a bound on time would hide exactly that row.
    ///
    /// The thing itself is never a candidate for itself. Everything else that shares a
    /// word is, of any kind -- a task or an appointment about the same matter is as much
    /// context as a mail.
    /// </remarks>
    /// <param name="imagined">
    /// The document the model imagined would settle this thing -- HyDE, see
    /// <see cref="DeskTriage.Imagine"/>. Its distinctive words are searched alongside the
    /// subject's, and a row that shares a word with the imagined answer counts as much as
    /// one that shares a word with the question. Optional: without it the subject alone
    /// decides, which finds the same thread and not much else.
    /// </param>
    public IReadOnlyList<DeskObject> About(
        DeskObject thing,
        int limit = DeskTriage.EarlierShown,
        string? imagined = null)
    {
        var words = new List<string>(DeskTriage.Keywords(thing.Subject));

        foreach (string word in DeskTriage.Keywords(imagined, most: 8))
        {
            if (!words.Contains(word, StringComparer.OrdinalIgnoreCase))
                words.Add(word);
        }

        if (words.Count == 0)
            return [];

        var seen = new Dictionary<string, (DeskObject Row, int Words)>(StringComparer.Ordinal);

        foreach (string word in words)
        {
            foreach (DeskObject found in Search(word, since: null, limit: 12))
            {
                if (found.Id == thing.Id)
                    continue;

                seen[found.Id] = seen.TryGetValue(found.Id, out (DeskObject Row, int Words) had)
                    ? (had.Row, had.Words + 1)
                    : (found, 1);
            }
        }

        return seen.Values
            .OrderByDescending(v => v.Words)
            .ThenByDescending(v => v.Row.When)
            .Take(limit)
            .Select(v => v.Row)
            .ToList();
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
               ticket_key, thread, entry_id, facts, enrichment, first_seen, last_seen,
               verdict, verdict_why, related
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
        LastSeen: When(reader, 14) ?? DateTime.MinValue,
        Verdict: reader.FieldCount > 15 && !reader.IsDBNull(15) ? Verdict(reader.GetString(15)) : null,
        VerdictWhy: reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetString(16) : null,
        Related: reader.FieldCount > 17 && !reader.IsDBNull(17) ? reader.GetString(17) : null);

    /// <summary>A verdict as it was written, or null when it is a word nobody knows.</summary>
    private static DeskVerdict? Verdict(string said) => said switch
    {
        "answer" => DeskVerdict.Answer,
        "information" => DeskVerdict.Information,
        "ignore" => DeskVerdict.Ignore,
        _ => null,
    };

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
