namespace Shellvis.Core.Tools;

/// <summary>
/// Marks a method as a tool the model may call.
///
/// The attribute carries only what cannot be derived from the method itself. The name,
/// parameters and JSON Schema all come from the signature, so they cannot drift out of
/// sync with the code the way a hand-written schema does. What the compiler cannot
/// know is the side effect and how the call should read in the console, and that is
/// exactly what lives here.
/// </summary>
/// <param name="name">
/// Tool name as the model sees it. Lower snake_case by convention, matching the
/// existing ecosystem (<c>read_file</c>, <c>window_list</c>) rather than .NET casing.
/// </param>
/// <param name="sideEffect">
/// How dangerous the call is. Required with no default: forcing an explicit choice is
/// what stops a new tool from quietly inheriting silent execution.
/// </param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ShellvisToolAttribute(string name, SideEffect sideEffect) : Attribute
{
    public string Name { get; } = name;

    public SideEffect SideEffect { get; } = sideEffect;

    /// <summary>
    /// One-line description for the model. Falls back to the XML doc summary when
    /// left unset, but stating it explicitly is better: the model reads this to decide
    /// whether the tool applies, so it should describe *when to use it*, not just what
    /// it does.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Which parameter carries the gist of the call, for the one-line console preview.
    /// Without this the console shows a tool name and nothing about the target, which
    /// is what makes a transcript unreadable.
    /// </summary>
    public string? PreviewParameter { get; init; }

    /// <summary>Glyph shown beside the call in the console transcript.</summary>
    public string? Glyph { get; init; }
}
