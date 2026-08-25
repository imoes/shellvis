namespace Shellvis.Shell;

/// <summary>
/// Shellvis speaks in announcements.
///
/// The persona is deliberate, not decoration: an agent that operates a machine on
/// the user behalf should make its arrivals, departures and state changes audible
/// rather than slipping in and out silently. Every string an operator might read at
/// a lifecycle boundary lives here, so the voice stays consistent instead of being
/// reinvented at each call site.
/// </summary>
internal static class ShellvisVoice
{
    public const string Greeting = "Shellvis has entered the building.";

    /// <summary>Shown when the agent shuts down. The signature sign-off.</summary>
    public const string Farewell = "Shellvis has left the building.";

    public const string Standby = "Shellvis is standing by.";
    public const string Working = "Shellvis is taking the stage.";
    public const string AwaitingApproval = "Shellvis needs your say-so.";
}
