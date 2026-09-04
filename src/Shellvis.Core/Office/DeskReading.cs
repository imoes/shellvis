using Shellvis.Core.Desk;

namespace Shellvis.Core.Office;

/// <summary>
/// One look at the desk: the numbers, and the things they were counted from.
///
/// Two results from one walk, and that is the whole reason this type exists. The page needs
/// a count per kind and the cache needs a row per thing; both come from reading the same
/// items, so they are read once. A second pass for the indexing would double the COM traffic
/// on the one operation that runs on a timer.
///
/// <b>Nothing here is stored.</b> Writing to the cache is the caller's business: this
/// assembly reads Outlook and knows nothing about retention, enrichment or what should be
/// linked to what. Handing back a list keeps that line where it is.
/// </summary>
/// <param name="Counts">What the page shows.</param>
/// <param name="Objects">
/// Everything the walk passed, ready to be remembered. Mail carries its ticket key and its
/// conversation, so the links a caller writes are already implied by the fields.
/// </param>
public sealed record DeskReading(
    DeskSnapshot Counts,
    IReadOnlyList<DeskObject> Objects);
