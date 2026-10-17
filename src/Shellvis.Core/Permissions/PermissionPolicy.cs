namespace Shellvis.Core.Permissions;

/// <summary>How much the agent may do without asking.</summary>
public enum PermissionMode
{
    /// <summary>
    /// Every action that is not provably a read asks. A shell command asks even when it
    /// only queries, because in this mode the user has said they want to see them.
    /// </summary>
    Ask,

    /// <summary>
    /// The default. An action that can be PROVEN to only read runs silently; everything
    /// else asks. The burden of proof is on "read", which is the whole safety property:
    /// there is no "probably harmless".
    /// </summary>
    AutoRead,

    /// <summary>
    /// Nothing asks, except the handful of tools declared
    /// <see cref="Tools.SideEffect.AlwaysAsk"/> -- gallery installs, privileged broker
    /// calls, arbitrary script in a logged-in browser page. Those are excluded by design:
    /// a mode that says "stop asking" is a statement about routine work, not a waiver on
    /// the three actions whose consequences cannot be undone by the person who allowed
    /// them.
    /// </summary>
    Yolo,
}

/// <summary>
/// The live permission mode.
///
/// Mutable and shared rather than passed by value, because the user changes it in the
/// middle of a session from the pill and the change has to take effect on the very next
/// tool call. A copy captured when the loop was built would leave the control looking
/// like it worked while the old mode kept deciding -- the same class of defect as the
/// config field this replaces, which carried a mode nobody ever read.
///
/// The plan also lists a fourth mode, <c>smart</c>, where a cheap auxiliary model judges
/// the doubtful cases. It is deliberately absent rather than aliased onto one of these:
/// a mode that silently behaves like a different one is worse than a missing mode.
/// </summary>
public sealed class PermissionPolicy
{
    public PermissionMode Mode { get; set; } = PermissionMode.AutoRead;

    /// <summary>The short label the pill shows.</summary>
    public static string Label(PermissionMode mode) => mode switch
    {
        PermissionMode.Ask => "ask",
        PermissionMode.Yolo => "yolo",
        _ => "auto",
    };

    /// <summary>One line explaining the mode, for the flyout and the transcript.</summary>
    public static string Describe(PermissionMode mode) => mode switch
    {
        PermissionMode.Ask =>
            "Ask before anything that is not provably a read, shell queries included.",
        PermissionMode.Yolo =>
            "Do not ask. Installs, privileged and browser-script actions still confirm.",
        _ =>
            "Reads run silently, changes ask. The recommended setting.",
    };

    /// <summary>
    /// Parse the config spelling. An unrecognised value falls back to the default WITH a
    /// warning from the caller rather than silently, because a misspelt mode that quietly
    /// becomes the safe default is still a setting the user believes is in force.
    /// </summary>
    public static bool TryParse(string? value, out PermissionMode mode)
    {
        mode = PermissionMode.AutoRead;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "ask" or "manual":
                mode = PermissionMode.Ask;
                return true;
            case "auto-read" or "auto" or "autoread":
                mode = PermissionMode.AutoRead;
                return true;
            case "yolo":
                mode = PermissionMode.Yolo;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The config spelling, for writing the choice back.</summary>
    public static string ToConfigValue(PermissionMode mode) => mode switch
    {
        PermissionMode.Ask => "ask",
        PermissionMode.Yolo => "yolo",
        _ => "auto-read",
    };
}
