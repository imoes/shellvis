using System.Globalization;
using Microsoft.Data.Sqlite;
using Shellvis.Core.Config;

namespace Shellvis.Core.Sessions;

/// <summary>One stored conversation.</summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Title">Human-readable name, generated or set.</param>
/// <param name="Model">Which model it ran against.</param>
/// <param name="StartedAt">When it began.</param>
/// <param name="EndedAt">When it was closed, if it was.</param>
/// <param name="MessageCount">How many messages it holds.</param>
/// <param name="ToolCallCount">How many tool calls it made.</param>
/// <param name="ParentId">
/// The session this one continued from, set when a conversation was rotated by
/// compaction. This is what turns a chain of compactions into a visible lineage
/// rather than a pile of unexplained duplicates.
/// </param>
public sealed record SessionInfo(
    string Id,
    string Title,
    string Model,
    DateTime StartedAt,
    DateTime? EndedAt,
    int MessageCount,
    int ToolCallCount,
    string? ParentId)
{
    public override string ToString()
    {
        string closed = EndedAt is null ? "open" : "closed";
        return $"{StartedAt:yyyy-MM-dd HH:mm}  \"{Title}\"  {MessageCount} msg  "
            + $"{ToolCallCount} calls  {closed}";
    }
}

/// <summary>One stored message.</summary>
public sealed record StoredMessage(
    long Id,
    string SessionId,
    string Role,
    string Content,
    string? ToolName,
    DateTime Timestamp);

/// <summary>
/// Persists conversations to SQLite.
///
/// Two schema decisions carry weight.
///
/// Compaction ROTATES a session rather than mutating it: the old session is closed, a
/// new one is opened with <c>parent_id</c> pointing back, and the summary becomes the
/// new session's first message. The full history therefore survives compaction, which
/// matters both for auditing what the agent did and for the user who wants to see what
/// was actually said before it was summarised. Hermes does the same thing and for the
/// same reason.
///
/// Message bodies are indexed with FTS5. Without it, "find the conversation where I
/// fixed the printer" requires scanning every message body in every session, which is
/// exactly the query a session store exists to answer.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SessionStore(string? path = null)
    {
        string file = path ?? Path.Combine(ShellvisPaths.Home, "sessions.db");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            // WAL so a read never blocks the write that is appending the current turn.
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();
        Initialise();
    }

    private void Initialise()
    {
        Execute("PRAGMA journal_mode=WAL;");

        // A short busy timeout plus WAL is enough here: there is one writer, and a
        // reader that waits a second for it is invisible to a human.
        Execute("PRAGMA busy_timeout=2000;");

        Execute("""
            CREATE TABLE IF NOT EXISTS sessions (
                id            TEXT PRIMARY KEY,
                title         TEXT NOT NULL DEFAULT '',
                model         TEXT NOT NULL DEFAULT '',
                started_at    TEXT NOT NULL,
                ended_at      TEXT,
                end_reason    TEXT,
                parent_id     TEXT REFERENCES sessions(id),
                message_count INTEGER NOT NULL DEFAULT 0,
                tool_calls    INTEGER NOT NULL DEFAULT 0
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS messages (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL REFERENCES sessions(id),
                role       TEXT NOT NULL,
                content    TEXT NOT NULL,
                tool_name  TEXT,
                timestamp  TEXT NOT NULL
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id, id);");
        Execute("CREATE INDEX IF NOT EXISTS idx_sessions_started ON sessions(started_at DESC);");
        Execute("CREATE INDEX IF NOT EXISTS idx_sessions_parent ON sessions(parent_id);");

        // External-content FTS: the index references the messages table rather than
        // copying every body, so search costs no duplicate storage.
        Execute("""
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                content,
                content='messages',
                content_rowid='id'
            );
            """);

        // Triggers keep the index in step. Without them the index silently drifts from
        // the table and search starts returning results that no longer exist.
        Execute("""
            CREATE TRIGGER IF NOT EXISTS messages_fts_insert AFTER INSERT ON messages BEGIN
                INSERT INTO messages_fts(rowid, content) VALUES (new.id, new.content);
            END;
            """);

        Execute("""
            CREATE TRIGGER IF NOT EXISTS messages_fts_delete AFTER DELETE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, content)
                VALUES ('delete', old.id, old.content);
            END;
            """);
    }

    /// <summary>Open a new session. Returns its id.</summary>
    public string CreateSession(string model, string? title = null, string? parentId = null)
    {
        // Time-ordered id with a random tail: sortable by eye, and collision-free
        // without needing a round trip to check.
        string id = $"s{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xffff):x}";

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (id, title, model, started_at, parent_id)
            VALUES ($id, $title, $model, $started, $parent);
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title ?? "Untitled");
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$started", Iso(DateTime.Now));
        command.Parameters.AddWithValue("$parent", parentId ?? (object)DBNull.Value);
        command.ExecuteNonQuery();

        return id;
    }

    /// <summary>Append a message and keep the session counters current.</summary>
    public void AddMessage(
        string sessionId, string role, string content, string? toolName = null)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();

        using (SqliteCommand insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO messages (session_id, role, content, tool_name, timestamp)
                VALUES ($session, $role, $content, $tool, $timestamp);
                """;

            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$role", role);
            insert.Parameters.AddWithValue("$content", content);
            insert.Parameters.AddWithValue("$tool", toolName ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$timestamp", Iso(DateTime.Now));
            insert.ExecuteNonQuery();
        }

        // Counters are maintained rather than computed: listing sessions is the common
        // operation, and a COUNT per row makes it scale with total message volume.
        using (SqliteCommand update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE sessions
                SET message_count = message_count + 1,
                    tool_calls = tool_calls + CASE WHEN $tool IS NULL THEN 0 ELSE 1 END
                WHERE id = $session;
                """;

            update.Parameters.AddWithValue("$session", sessionId);
            update.Parameters.AddWithValue("$tool", toolName ?? (object)DBNull.Value);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Close a session, recording why.</summary>
    public void EndSession(string sessionId, string reason)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions SET ended_at = $ended, end_reason = $reason WHERE id = $id;
            """;

        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$ended", Iso(DateTime.Now));
        command.Parameters.AddWithValue("$reason", reason);
        command.ExecuteNonQuery();
    }

    /// <summary>Rename a session.</summary>
    public void SetTitle(string sessionId, string title)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "UPDATE sessions SET title = $title WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$title", title);
        command.ExecuteNonQuery();
    }

    /// <summary>List sessions, newest first.</summary>
    public IReadOnlyList<SessionInfo> ListSessions(int limit = 50)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, model, started_at, ended_at, message_count, tool_calls, parent_id
            FROM sessions
            ORDER BY started_at DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

        var sessions = new List<SessionInfo>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
            sessions.Add(ReadSession(reader));

        return sessions;
    }

    /// <summary>Every message of one session, in order.</summary>
    public IReadOnlyList<StoredMessage> GetMessages(string sessionId)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, role, content, tool_name, timestamp
            FROM messages WHERE session_id = $session ORDER BY id;
            """;

        command.Parameters.AddWithValue("$session", sessionId);

        var messages = new List<StoredMessage>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            messages.Add(new StoredMessage(
                Id: reader.GetInt64(0),
                SessionId: reader.GetString(1),
                Role: reader.GetString(2),
                Content: reader.GetString(3),
                ToolName: reader.IsDBNull(4) ? null : reader.GetString(4),
                Timestamp: ParseIso(reader.GetString(5))));
        }

        return messages;
    }

    /// <summary>
    /// Full-text search across message bodies.
    ///
    /// The query is sanitised into a bare phrase rather than passed through. FTS5 has
    /// its own operator syntax, and a user typing an apostrophe or a stray quote would
    /// otherwise get a syntax error instead of results.
    /// </summary>
    public IReadOnlyList<(SessionInfo Session, string Snippet)> Search(string query, int limit = 20)
    {
        string sanitised = SanitiseFtsQuery(query);

        if (sanitised.Length == 0)
            return [];

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.title, s.model, s.started_at, s.ended_at,
                   s.message_count, s.tool_calls, s.parent_id,
                   snippet(messages_fts, 0, '[', ']', '...', 12)
            FROM messages_fts
            JOIN messages m ON m.id = messages_fts.rowid
            JOIN sessions s ON s.id = m.session_id
            WHERE messages_fts MATCH $query
            ORDER BY s.started_at DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$query", sanitised);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<(SessionInfo, string)>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
            results.Add((ReadSession(reader), reader.GetString(8)));

        return results;
    }

    /// <summary>Delete a session and its messages.</summary>
    public bool DeleteSession(string sessionId)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();

        using (SqliteCommand messages = _connection.CreateCommand())
        {
            messages.Transaction = transaction;
            messages.CommandText = "DELETE FROM messages WHERE session_id = $id;";
            messages.Parameters.AddWithValue("$id", sessionId);
            messages.ExecuteNonQuery();
        }

        int removed;
        using (SqliteCommand session = _connection.CreateCommand())
        {
            session.Transaction = transaction;

            // Children are detached rather than deleted: a compaction chain must not
            // lose its later halves because an early one was removed.
            session.CommandText = """
                UPDATE sessions SET parent_id = NULL WHERE parent_id = $id;
                DELETE FROM sessions WHERE id = $id;
                """;

            session.Parameters.AddWithValue("$id", sessionId);
            removed = session.ExecuteNonQuery();
        }

        transaction.Commit();
        return removed > 0;
    }

    /// <summary>Remove sessions older than a cutoff. Returns how many went.</summary>
    public int Prune(TimeSpan olderThan)
    {
        DateTime cutoff = DateTime.Now - olderThan;

        List<string> ids = ListSessions(int.MaxValue)
            .Where(s => s.StartedAt < cutoff)
            .Select(s => s.Id)
            .ToList();

        foreach (string id in ids)
            DeleteSession(id);

        return ids.Count;
    }

    private static SessionInfo ReadSession(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Title: reader.GetString(1),
        Model: reader.GetString(2),
        StartedAt: ParseIso(reader.GetString(3)),
        EndedAt: reader.IsDBNull(4) ? null : ParseIso(reader.GetString(4)),
        MessageCount: reader.GetInt32(5),
        ToolCallCount: reader.GetInt32(6),
        ParentId: reader.IsDBNull(7) ? null : reader.GetString(7));

    /// <summary>
    /// Reduce a query to a quoted phrase of safe tokens.
    ///
    /// FTS5 treats quotes, asterisks, colons and parentheses as operators, so a
    /// human-typed query reaches it as a syntax error rather than a search. Quoting the
    /// cleaned tokens gives phrase semantics, which is what someone typing words
    /// expects anyway.
    /// </summary>
    private static string SanitiseFtsQuery(string query)
    {
        IEnumerable<string> tokens = query
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

    // Round-trip safe and sortable as text, which is what lets ORDER BY work on a
    // TEXT column without converting anything.
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
