using System.Text;

namespace Shellvis.Shell.Views;

/// <summary>Who said it.</summary>
public enum Said
{
    User,
    Assistant,
}

/// <summary>One thing that was said.</summary>
public sealed record Turn(Said By, string Text);

/// <summary>
/// The conversation, as the message window shows it.
///
/// <b>Why this exists as its own thing.</b> The console and the message window kept drifting
/// apart: a live turn put the answer in the window and the question in the console, while a
/// resumed conversation put BOTH in the console and only the last answer in the window. Two
/// paths, two answers to the same question, and neither of them the rule that was intended.
///
/// The rule, now in one place: <b>the console is the log and the window is the conversation.</b>
/// What was asked and what was answered are the conversation. Tool calls, warnings, mode
/// changes and everything else the machine did are the log. Nothing appears in both.
///
/// <b>Why it accumulates.</b> A window that shows only the latest answer is a window that
/// cannot answer "what did I ask before that?" -- which is exactly what someone opening a
/// past conversation from the history wants. So it holds the exchange and renders it in
/// order, and resuming a session simply fills it with what was stored.
/// </summary>
public sealed class Conversation
{
    /// <summary>
    /// How many turns are kept.
    ///
    /// A long conversation is a long document, and this is rendered in full on every delta
    /// while an answer streams. Two hundred exchanges is far more than anyone scrolls back
    /// through and still cheap to lay out; beyond that the oldest go, because the reason to
    /// look at this window is what was said recently.
    /// </summary>
    private const int MaxTurns = 200;

    private readonly List<Turn> _turns = [];

    /// <summary>The answer currently being streamed, which is not a turn until it is done.</summary>
    private string? _pending;

    public bool IsEmpty => _turns.Count == 0 && _pending is null;

    /// <summary>Start over, for a new or a resumed session.</summary>
    public void Clear()
    {
        _turns.Clear();
        _pending = null;
    }

    /// <summary>Record something that was said in full.</summary>
    public void Add(Said by, string text)
    {
        string trimmed = (text ?? string.Empty).Trim();

        if (trimmed.Length == 0)
            return;

        _pending = null;
        _turns.Add(new Turn(by, trimmed));

        while (_turns.Count > MaxTurns)
            _turns.RemoveAt(0);
    }

    /// <summary>
    /// The answer as it arrives, replacing whatever partial text came before.
    ///
    /// Held apart from the finished turns rather than appended and rewritten: the streaming
    /// path re-renders on every delta, and a partial answer that has been committed would
    /// have to be found and replaced, which is how a duplicate ends up in a transcript.
    /// </summary>
    public void Streaming(string text) => _pending = text;

    /// <summary>
    /// Render as the Markdown the message window draws.
    ///
    /// The speaker is a heading rather than a prefix on the line: a prefix disappears into
    /// the paragraph as soon as an answer runs to more than one line, and telling who is
    /// speaking is the one thing this document has to do that a single answer did not.
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();

        foreach (Turn turn in _turns)
            Append(sb, turn.By, turn.Text);

        if (_pending is { Length: > 0 })
            Append(sb, Said.Assistant, _pending);

        return sb.ToString().TrimEnd();
    }

    private static void Append(StringBuilder sb, Said by, string text)
    {
        if (sb.Length > 0)
            sb.AppendLine();

        // The user's own words are set off by a rule and a short label; the assistant's
        // answer is the body text. Weighting them equally would make the document read as a
        // chat log, and what it is meant to be is the answer with its question above it.
        if (by == Said.User)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append("**").Append(Escape(text.ReplaceLineEndings(" ").Trim())).AppendLine("**");
        }
        else
        {
            sb.AppendLine(text);
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Keep a prompt from being read as markup.
    ///
    /// A question can legitimately contain asterisks, backticks and brackets: "what does
    /// `Get-Process **` do" is a reasonable thing to type. Wrapped in bold and handed to the
    /// renderer unescaped, it would close the bold early and swallow the rest of the line.
    /// </summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            if (c is '*' or '_' or '`' or '~' or '[' or ']' or '\\')
                sb.Append('\\');

            sb.Append(c);
        }

        return sb.ToString();
    }
}
