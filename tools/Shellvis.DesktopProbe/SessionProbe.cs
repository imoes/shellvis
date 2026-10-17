using Shellvis.Core.Sessions;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Verifies the session store, with particular attention to the two things that would
/// quietly lose a user's history.
///
/// FTS5 search has to survive a human-typed query. Its own operator syntax means a
/// stray apostrophe or asterisk reaches it as a syntax error rather than a search, and
/// a search box that throws on ordinary punctuation is worse than none.
///
/// And a compaction chain has to stay traceable. Compaction rotates a session rather
/// than mutating it, so the lineage is the only thing that connects the summary anyone
/// is reading back to the conversation it summarised.
/// </summary>
internal static class SessionProbe
{
    public static int Run()
    {
        string file = Path.Combine(Path.GetTempPath(), "shellvis-session-probe.db");

        foreach (string stale in new[] { file, file + "-wal", file + "-shm" })
        {
            if (File.Exists(stale))
                File.Delete(stale);
        }

        int failures = 0;

        using (var store = new SessionStore(file))
        {
            // ------------------------------------------------------------ basics
            string first = store.CreateSession("laguna", "Printer troubleshooting");
            store.AddMessage(first, "user", "The printer in the Meierstrasse office jams.");
            store.AddMessage(first, "assistant", "Let me look at the spooler.");
            store.AddMessage(first, "tool", "Spooler: Running", toolName: "powershell_run");
            store.AddMessage(first, "assistant", "The spooler was stuck; restarting cleared it.");

            IReadOnlyList<SessionInfo> sessions = store.ListSessions();
            failures += Expect(sessions.Count == 1, "the session is listed");
            failures += Expect(sessions[0].MessageCount == 4, "the message counter is maintained");
            failures += Expect(
                sessions[0].ToolCallCount == 1,
                "tool calls are counted separately from messages");

            IReadOnlyList<StoredMessage> messages = store.GetMessages(first);
            failures += Expect(messages.Count == 4, "the messages come back");
            failures += Expect(messages[0].Role == "user", "in order, oldest first");
            failures += Expect(
                messages[2].ToolName == "powershell_run",
                "the tool name round-trips");

            // ----------------------------------------------------------- search
            var hits = store.Search("printer");
            failures += Expect(hits.Count > 0, "a plain word is found");
            failures += Expect(
                hits[0].Session.Id == first,
                "the hit points at the right session");
            failures += Expect(
                hits[0].Snippet.Contains('[', StringComparison.Ordinal),
                "the snippet marks the match");

            // The cases that break a naive FTS5 pass-through. Each must return a
            // result or an empty list, never throw.
            foreach (string awkward in new[]
            {
                "printer's",          // apostrophe
                "print*",             // wildcard operator
                "\"unclosed",         // unbalanced quote
                "spooler AND stuck",  // bare operator
                "NEAR(printer)",      // function syntax
                "meierstrasse:",      // colon
                "()",                 // punctuation only
            })
            {
                failures += ExpectNoThrow(
                    () => store.Search(awkward),
                    $"a query like {awkward} does not throw");
            }

            failures += Expect(
                store.Search("Meierstrasse").Count > 0,
                "a proper noun is still findable after sanitising");

            // -------------------------------------------------------- compaction
            store.EndSession(first, "compaction");
            string second = store.CreateSession("laguna", "Printer troubleshooting (2)", parentId: first);
            store.AddMessage(second, "system", "Summary: the printer spooler was stuck and got restarted.");

            IReadOnlyList<SessionInfo> after = store.ListSessions();
            SessionInfo child = after.First(s => s.Id == second);
            SessionInfo parent = after.First(s => s.Id == first);

            failures += Expect(child.ParentId == first, "the rotated session points at its parent");
            failures += Expect(parent.EndedAt is not null, "the parent is closed");
            failures += Expect(
                store.GetMessages(first).Count == 4,
                "and the parent still HOLDS its messages, so nothing was lost to compaction");

            // ------------------------------------------------------ delete safety
            store.DeleteSession(first);
            IReadOnlyList<SessionInfo> remaining = store.ListSessions();

            failures += Expect(
                remaining.Count == 1 && remaining[0].Id == second,
                "deleting the parent leaves the child");
            failures += Expect(
                remaining[0].ParentId is null,
                "and detaches the child rather than orphaning a dangling reference");
            failures += Expect(
                store.GetMessages(first).Count == 0,
                "the deleted session's messages are gone");
            // Not "no results at all": the surviving session holds the summary, which
            // legitimately mentions the printer. The check is that the DELETED session
            // no longer appears -- asserting on an empty result set here was simply the
            // wrong assertion.
            failures += Expect(
                store.Search("printer").All(h => h.Session.Id != first),
                "and the deleted session no longer appears in search results");
            failures += Expect(
                store.Search("jams").Count == 0,
                "a phrase only the deleted session contained is gone from the index");

            // -------------------------------------------------------- rename
            store.SetTitle(second, "Printer fixed");
            failures += Expect(
                store.ListSessions()[0].Title == "Printer fixed",
                "a session can be renamed");
        }

        // ---------------------------------------------------- survives a restart
        using (var reopened = new SessionStore(file))
        {
            failures += Expect(
                reopened.ListSessions().Count == 1,
                "the store survives being closed and reopened");
        }

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: history persists, search tolerates real queries, lineage holds."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    private static int Expect(bool condition, string what)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }

    private static int ExpectNoThrow(Action action, string what)
    {
        try
        {
            action();
            Console.WriteLine($"  ok   {what}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL {what} -> {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
