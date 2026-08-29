namespace Shellvis.Core.Markdown;

/// <summary>How a run of text is set.</summary>
/// <remarks>
/// Flags rather than a hierarchy, because the markers combine freely and a model writes
/// them combined: <c>**bold `code`**</c> is ordinary. A tree of nested span types would
/// have to be walked; a flag per marker is what the renderer actually needs to build one
/// run.
/// </remarks>
[Flags]
public enum SpanStyle
{
    None = 0,
    Strong = 1,
    Emphasis = 2,
    Strike = 4,

    /// <summary>A code span. Set on its own; the others do not apply to it.</summary>
    /// <remarks>
    /// Upright and unweighted wherever it appears, including inside an italic paragraph.
    /// Italic monospace is what a slanted terminal font looks like, and a code span is
    /// quoted machine text that should read as machine text whatever surrounds it.
    /// </remarks>
    Code = 8,
}

/// <summary>One run of text with one style.</summary>
public sealed record MarkdownSpan(string Text, SpanStyle Style)
{
    public bool Has(SpanStyle flag) => (Style & flag) != 0;
}

/// <summary>One block of an answer.</summary>
/// <remarks>
/// A closed hierarchy: these are the shapes the renderer can draw, so anything the parser
/// cannot place becomes a paragraph of literal text rather than being dropped. An answer
/// with a hole in it is worse than an answer with an unrendered line in it.
/// </remarks>
public abstract record MarkdownBlock
{
    /// <param name="Level">1 to 6, as written. The renderer decides how many sizes it has.</param>
    public sealed record Heading(int Level, IReadOnlyList<MarkdownSpan> Inlines) : MarkdownBlock;

    /// <param name="Depth">Indent level, already clamped by the parser.</param>
    /// <param name="Marker">The bullet glyph, or the item's own number with its dot.</param>
    public sealed record Bullet(int Depth, string Marker, IReadOnlyList<MarkdownSpan> Inlines)
        : MarkdownBlock;

    public sealed record Paragraph(IReadOnlyList<MarkdownSpan> Inlines) : MarkdownBlock;

    /// <param name="Text">The fence's contents, line breaks intact.</param>
    /// <param name="Closed">
    /// False when the closing fence never arrived. Normal while streaming, and the reason
    /// an unterminated fence is still a code block: a block that stayed invisible until its
    /// last three characters landed would flicker into existence at the end of every answer.
    /// </param>
    public sealed record Code(string Text, bool Closed) : MarkdownBlock;
}

/// <summary>A parsed answer, in the order it should be drawn.</summary>
public sealed record MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks)
{
    public static MarkdownDocument Empty { get; } = new([]);
}
