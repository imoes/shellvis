using Shellvis.Core.Sessions;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Reads the REAL session database that the running application writes.
///
/// The point is recovery after a hard kill. The store runs in WAL mode, so a
/// force-terminated process leaves its most recent turns in sessions.db-wal rather
/// than in the main file -- the main file can look almost empty while a couple of
/// hundred kilobytes of conversation sit beside it. Whether that is durable or lost is
/// exactly the question a persistence layer has to answer, and the only honest way to
/// answer it is to kill the app and then open the database again.
/// </summary>
internal static class HistoryProbe
{
    public static int Run(string? sessionId)
    {
        using var store = new SessionStore();

        IReadOnlyList<SessionInfo> sessions = store.ListSessions(20);

        if (sessions.Count == 0)
        {
            Console.WriteLine("no sessions recorded yet.");
            return 1;
        }

        Console.WriteLine($"{sessions.Count} recorded session(s):\n");

        foreach (SessionInfo session in sessions)
        {
            string lineage = session.ParentId is null ? string.Empty : $"  (continues {session.ParentId})";
            Console.WriteLine($"  {session.Id}");
            Console.WriteLine($"    {session}{lineage}");
        }

        string target = sessionId ?? sessions[0].Id;
        IReadOnlyList<StoredMessage> messages = store.GetMessages(target);

        Console.WriteLine($"\n--- {target}: {messages.Count} message(s) ---");

        foreach (StoredMessage message in messages)
        {
            string label = message.ToolName is null ? message.Role : $"{message.Role}:{message.ToolName}";
            Console.WriteLine($"  [{message.Timestamp:HH:mm:ss}] {label,-22} {Clip(message.Content, 90)}");
        }

        // Search has to work against what the application actually wrote, not only
        // against rows a test inserted.
        Console.WriteLine("\n--- search for 'Rechner' ---");
        foreach ((SessionInfo hit, string snippet) in store.Search("Rechner", 5))
            Console.WriteLine($"  {hit.Id}  {Clip(snippet, 90)}");

        bool recovered = messages.Count > 0;

        Console.WriteLine(recovered
            ? "\nVERIFIED: the conversation survived the process being killed."
            : "\nNOT VERIFIED: the session row exists but holds no messages.");

        return recovered ? 0 : 1;
    }

    private static string Clip(string text, int max)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
