namespace Shellvis.Core.Tools;

/// <summary>
/// How much damage a tool can do, which decides whether it runs silently.
///
/// The polarity is deliberate and load-bearing: a tool is only allowed to run without
/// asking when it can be *proven* harmless. There is no "probably fine" tier. Anything
/// whose effect cannot be established up front lands in <see cref="Mutating"/> and
/// prompts, which is why the default for a new tool must never be
/// <see cref="ReadOnly"/>.
/// </summary>
public enum SideEffect
{
    /// <summary>
    /// Observes without changing anything: reading a file, listing windows, searching.
    /// Runs silently in the conditional auto mode. This is also the flag that decides
    /// whether a batch of tool calls may execute in parallel.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Changes state somewhere: writes a file, clicks a button, sends a mail, switches
    /// a light. Prompts in every mode except yolo.
    /// </summary>
    Mutating,

    /// <summary>
    /// Prompts unconditionally, yolo included. Reserved for actions whose blast radius
    /// justifies overriding the user's own "stop asking me" setting: installing code
    /// from the internet, elevating to admin, anything matching the dangerous-command
    /// patterns. A mode switch is a convenience; these are not.
    /// </summary>
    AlwaysAsk,
}
