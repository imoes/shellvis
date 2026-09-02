using System.Globalization;
using System.Text;

namespace Shellvis.Core.Office;

/// <summary>Which of the two ways of looking actually produced the answer.</summary>
public enum MailSearchPath
{
    /// <summary>The Windows Search content index, through a DASL filter.</summary>
    Index,

    /// <summary>A bounded walk of the newest messages, because the index found nothing.</summary>
    Scan,
}

/// <param name="Path">Which way answered. Said out loud, because it changes what an empty result means.</param>
/// <param name="Folders">How many folders were looked in.</param>
/// <param name="Scanned">How many messages the fallback read, or zero when it did not run.</param>
public sealed record MailSearchResult(
    MailPage Page,
    MailSearchPath Path,
    int Folders,
    int Scanned);

public sealed partial class OutlookClient
{
    /// <summary>How many matches are read from one folder before it is called enough.</summary>
    /// <remarks>
    /// A search for a common word can match thousands. Marshalling all of them to sort in C#
    /// costs seconds per folder for rows nobody will read; the cap is generous enough that the
    /// newest matches are certainly inside it.
    /// </remarks>
    private const int RowsPerFolder = 500;

    /// <summary>
    /// Search mail by content, with the index first and a walk behind it.
    ///
    /// <b>Two ways of looking, and the reason is a rule this project already learned.</b> The
    /// content-index operators are fast, and they depend on Windows Search having indexed the
    /// store: on a store it has not, they either fail or quietly return nothing. Quietly
    /// returning nothing is the failure mode named in <c>OutlookClient.Threads.cs</c> -- "an
    /// empty result that looks exactly like an answer" -- so it cannot be the only way of
    /// looking. When the index finds nothing, the newest messages are read and compared
    /// directly, and the caller is told which way answered. An empty result then means both
    /// looked.
    ///
    /// <b>Outlook's own AdvancedSearch is not used</b>, and not by preference: it reports
    /// completion through a COM event, which needs a Windows message loop on the calling
    /// thread. <see cref="ComApartment"/> is a work queue and pumps no messages, so the event
    /// would never arrive.
    /// </summary>
    public Task<MailSearchResult> SearchMailAsync(
        string query,
        int limit = 20,
        DateTime? since = null,
        DateTime? until = null,
        CancellationToken cancellationToken = default)
    {
        string[] words = Words(query);

        // Nothing searchable was given. Answered without touching Outlook, because a filter
        // built from no words matches the whole mailbox -- a search that returns everything
        // is worse than one that returns nothing, because it looks like it worked.
        if (words.Length == 0)
            return Task.FromResult(new MailSearchResult(MailPage.Empty, MailSearchPath.Index, 0, 0));

        return apartment.InvokeAsync<MailSearchResult>(() =>
        {
            dynamic? outlook = null;
            dynamic? session = null;

            try
            {
                outlook = Com.GetOrStart("Outlook.Application", out bool startedOutlook);

                if (startedOutlook)
                    WasStarted = true;

                session = outlook.Session;

                List<dynamic> folders = Scope(session);
                var hits = new List<MailSummary>();
                string filter = SearchFilter(words, since, until);

                try
                {
                    foreach (dynamic folder in folders)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Harvest(folder, filter, hits, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A store Windows Search has not indexed refuses the ci_ operators
                    // outright. That is not an error to report: it is precisely the case the
                    // fallback exists for, so whatever was gathered is kept and the walk runs.
                    hits.Clear();
                }

                if (hits.Count > 0)
                    return Page(hits, limit, MailSearchPath.Index, folders.Count, scanned: 0);

                // Nothing in the index. Look properly.
                int scanned = 0;

                foreach (int folder in new[] { FolderInbox, FolderSentMail })
                {
                    Walk((object)session!, folder, cancellationToken, item =>
                    {
                        scanned++;

                        MailSummary mail = ReadMail(item);

                        if (Matches(mail, Str(() => item.Body), words, since, until))
                            hits.Add(mail);

                        return hits.Count >= limit ? Walking.Stop : Walking.Continue;
                    });
                }

                return Page(hits, limit, MailSearchPath.Scan, folders.Count, scanned);
            }
            finally
            {
                Com.ReleaseAll(outlook, session);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// The DASL filter for a content search.
    ///
    /// <b>The date format here is FIXED, and that is the opposite of the bracket syntax.</b>
    /// <see cref="ListFilter"/> writes its dates in the user's culture because Outlook's
    /// bracket syntax reads them that way; a DASL <c>@SQL</c> comparison does not, and writing
    /// a German date into one is as wrong as writing an invariant one into the other. The two
    /// are pinned separately in the harness so that neither can be "unified" into the other.
    ///
    /// Public for the harness: this string is where a search silently becomes a search for
    /// something else, so it is checked without Outlook.
    /// </summary>
    public static string SearchFilter(IReadOnlyList<string> words, DateTime? since, DateTime? until)
    {
        var sb = new StringBuilder("@SQL=");
        var clauses = new List<string>();

        // Each word must appear SOMEWHERE -- in the subject or in the body. Words are ANDed
        // and not treated as one phrase: "ftp kunde" should find the mail that says both, not
        // only the one that says them adjacently.
        foreach (string word in words)
        {
            string safe = Quote(word);

            clauses.Add(
                $"(\"urn:schemas:httpmail:subject\" ci_phrasematch '{safe}'"
                + $" OR \"urn:schemas:httpmail:textdescription\" ci_phrasematch '{safe}')");
        }

        if (since is { } from)
        {
            clauses.Add(
                "\"urn:schemas:httpmail:datereceived\" >= '"
                + from.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "'");
        }

        if (until is { } to)
        {
            clauses.Add(
                "\"urn:schemas:httpmail:datereceived\" < '"
                + to.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "'");
        }

        return sb.Append(string.Join(" AND ", clauses)).ToString();
    }

    /// <summary>
    /// Split a query into words, dropping what would match everything.
    ///
    /// A one-character word is dropped rather than searched: <c>ci_phrasematch</c> on "a"
    /// matches most of a mailbox, and a search that returns everything is as useless as one
    /// that returns nothing while looking more convincing.
    /// </summary>
    public static string[] Words(string? query) =>
        (query ?? string.Empty)
            .Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1)
            .Take(6)
            .ToArray();

    /// <summary>Double an apostrophe, or a name like O'Brien rewrites the filter.</summary>
    private static string Quote(string word) => word.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// The folders a search looks in: the inbox, its immediate children, and sent mail.
    ///
    /// Deliberately not every store. An attached archive PST can take minutes to search, and
    /// a tool that occasionally hangs for minutes is one nobody uses; one level of inbox
    /// subfolders covers how mail is actually filed. The count is reported so the answer says
    /// how wide it looked.
    /// </summary>
    private static List<dynamic> Scope(dynamic session)
    {
        const int MaxFolders = 24;

        var folders = new List<dynamic>();

        dynamic inbox = session.GetDefaultFolder(FolderInbox);
        folders.Add(inbox);

        try
        {
            dynamic children = inbox.Folders;
            int count = (int)children.Count;

            for (int i = 1; i <= count && folders.Count < MaxFolders; i++)
                folders.Add(children[i]);
        }
        catch (Exception)
        {
            // A mailbox with no subfolders, or one that refuses to enumerate them. The inbox
            // alone is still a useful search.
        }

        try
        {
            folders.Add(session.GetDefaultFolder(FolderSentMail));
        }
        catch (Exception)
        {
            // Some profiles have no sent folder of their own.
        }

        return folders;
    }

    /// <summary>
    /// Read one folder's matching messages.
    ///
    /// <b>Restrict rather than GetTable, and the table was tried first.</b> A table is much
    /// cheaper -- it returns chosen columns without touching the messages -- and it hands back
    /// the wrong identity. Measured: asked for a hit's <c>EntryID</c>, a table row gave 48 hex
    /// characters where the same message through <c>Items</c> gives 140. Asking instead for
    /// PR_ENTRYID by its property tag (0x0FFF0102) changed nothing; the first twenty bytes
    /// agree, so it is the same store, but the short form is a table-lifetime id and
    /// <c>mail_read</c> cannot open it. Every hit would have carried an id that does not work.
    ///
    /// The harness found it by searching for a word out of a message it had just listed and
    /// then not finding that message -- which is the only reason this is not shipped.
    ///
    /// So the items themselves are read, through <see cref="ReadMail"/>, and a search hit is
    /// then exactly the same shape as every other mail line in this application, preview
    /// included. It costs a COM round trip per hit, which the cap bounds.
    /// </summary>
    private static void Harvest(
        dynamic folder,
        string filter,
        List<MailSummary> into,
        CancellationToken cancellationToken)
    {
        dynamic? items = null;
        dynamic? matches = null;

        try
        {
            items = folder.Items;
            matches = items.Restrict(filter);
            matches.Sort("[ReceivedTime]", true);

            int take = Math.Min(RowsPerFolder, (int)matches.Count);

            for (int i = 1; i <= take; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                dynamic? item = null;

                try
                {
                    item = matches[i];
                    into.Add(ReadMail(item));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unreadable hit must not lose the rest, the same allowance the
                    // listing and the walk make.
                }
                finally
                {
                    Com.Release(item);
                }
            }
        }
        finally
        {
            Com.ReleaseAll(items, matches);
        }
    }

    /// <summary>Does this message match, when the index is not the one deciding.</summary>
    private static bool Matches(
        MailSummary mail,
        string body,
        IReadOnlyList<string> words,
        DateTime? since,
        DateTime? until)
    {
        if (since is { } from && mail.Received < from)
            return false;

        if (until is { } to && mail.Received >= to)
            return false;

        foreach (string word in words)
        {
            bool found =
                mail.Subject.Contains(word, StringComparison.OrdinalIgnoreCase)
                || mail.From.Contains(word, StringComparison.OrdinalIgnoreCase)
                || body.Contains(word, StringComparison.OrdinalIgnoreCase);

            if (!found)
                return false;
        }

        return true;
    }

    /// <summary>Newest first, cut to the limit, and honest about what was cut.</summary>
    private static MailSearchResult Page(
        List<MailSummary> hits,
        int limit,
        MailSearchPath path,
        int folders,
        int scanned)
    {
        // Sorted here rather than in Outlook: a table can be sorted server-side, but the rows
        // come from several folders and have to be merged anyway, so one sort of a bounded
        // list is simpler than one sort per folder plus a merge.
        hits.Sort((a, b) => b.Received.CompareTo(a.Received));

        var shown = hits.Count <= limit ? hits : hits.GetRange(0, limit);

        return new MailSearchResult(
            new MailPage(
                shown,
                Matching: hits.Count,

                // No meaningful folder total for a search: it spans several folders, and
                // "how many messages exist in all of them" is not a number that helps anyone
                // judge a search. Left at zero rather than filled with the match count, which
                // would read as a fact about the mailbox.
                InFolder: 0,
                Newest: shown.Count > 0 ? shown[0].Received : null,
                Oldest: shown.Count > 0 ? shown[^1].Received : null),
            path,
            folders,
            scanned);
    }
}
