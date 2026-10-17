namespace Shellvis.Core.Desktop;

/// <summary>One window that was in front, from when.</summary>
public sealed record Glance(DateTime At, string Program, string Title);

/// <summary>A glance and how long it lasted, as the tool reports it.</summary>
public sealed record GlanceSpan(Glance Glance, TimeSpan For);

/// <summary>
/// Which window was in front, and when: the last hours of the user's attention.
///
/// <b>What it is for.</b> "The mail I just had open", "the document from before the meeting",
/// "what was I doing when the call came" -- questions a person asks an assistant sitting next
/// to them, and that no tool could answer, because nothing remembered what was on screen a
/// minute ago. A window title is usually enough: Outlook titles an opened mail with its
/// subject, Word with the document name, a browser with the page.
///
/// <b>Kept in memory, for hours, and nowhere else.</b> Window titles say a great deal about a
/// person's day. This is the shortest memory that answers the questions above: nothing is
/// written to disk, the process ending forgets it, and anything older than <see cref="Kept"/>
/// is dropped on the next note.
///
/// Thread-safe: the hook notes from the UI thread, the tool reads from the agent's.
/// </summary>
public sealed class AttentionLog
{
    /// <summary>The one log the hook writes and the tool reads.</summary>
    public static AttentionLog Shared { get; } = new();

    /// <summary>How long a glance is kept.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromHours(8);

    /// <summary>A ceiling regardless of age, against a window whose title changes every second.</summary>
    public const int MaxGlances = 4000;

    private readonly object _gate = new();
    private readonly List<Glance> _glances = [];

    /// <summary>
    /// Record that a window came to the front, or that the one in front was retitled.
    /// The same program and title twice in a row is one glance, not two.
    /// </summary>
    public void Note(string program, string title, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(title))
            return;

        string cleanTitle = title.Trim();

        lock (_gate)
        {
            if (_glances.Count > 0
                && _glances[^1].Program == program
                && _glances[^1].Title == cleanTitle)
            {
                return;
            }

            _glances.Add(new Glance(at, program, cleanTitle));

            DateTime cutoff = at - Kept;
            int stale = _glances.FindIndex(g => g.At >= cutoff);

            if (stale > 0)
                _glances.RemoveRange(0, stale);

            if (_glances.Count > MaxGlances)
                _glances.RemoveRange(0, _glances.Count - MaxGlances);
        }
    }

    /// <summary>
    /// The glances since <paramref name="from"/>, oldest first, each with how long it lasted.
    /// The glance in front at <paramref name="from"/> is included: it is what the user was
    /// looking at when the window of time began.
    /// </summary>
    public IReadOnlyList<GlanceSpan> Since(DateTime from, DateTime now)
    {
        lock (_gate)
        {
            int first = _glances.FindLastIndex(g => g.At <= from);

            if (first < 0)
                first = 0;

            var spans = new List<GlanceSpan>();

            for (int i = first; i < _glances.Count; i++)
            {
                DateTime end = i + 1 < _glances.Count ? _glances[i + 1].At : now;
                spans.Add(new GlanceSpan(_glances[i], end - _glances[i].At));
            }

            return spans;
        }
    }

    /// <summary>Forget everything. For the probe, and for anyone who asks.</summary>
    public void Clear()
    {
        lock (_gate)
            _glances.Clear();
    }
}
