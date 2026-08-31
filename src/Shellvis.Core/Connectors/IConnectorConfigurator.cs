namespace Shellvis.Core.Connectors;

/// <summary>What one connector still needs before it can be used.</summary>
/// <param name="Name">The connector's key, as the directory names it.</param>
/// <param name="Title">A human name, when the manifest gives one.</param>
/// <param name="Ready">Whether its tools are registered.</param>
/// <param name="Detail">Why not, when they are not.</param>
/// <param name="Variables">
/// The names it resolves, in the order they should be asked for. Names only: the manifest
/// cannot hold a value, and neither can this.
/// </param>
public sealed record ConnectorNeeds(
    string Name,
    string? Title,
    bool Ready,
    string Detail,
    IReadOnlyList<ConnectorVariable> Variables);

/// <summary>One thing a connector needs to be told, by name.</summary>
/// <param name="Name">The variable or stored-secret name.</param>
/// <param name="Label">What to call it in front of a person.</param>
/// <param name="Secret">Whether it must be typed into a password field and never shown back.</param>
/// <param name="FromEnvironment">
/// Whether an environment variable of this name is already set. When it is, that value wins
/// over anything stored, and the dialog has to say so -- otherwise somebody types a new
/// password, is told it was saved, and nothing changes.
/// </param>
public sealed record ConnectorVariable(string Name, string Label, bool Secret, bool FromEnvironment);

/// <summary>
/// Asking the person in front of the machine to configure a connector.
///
/// <b>Why this is an interface and not a tool argument.</b> The obvious design is
/// <c>connector_configure(name, url, user, password)</c>, and it is wrong: a tool's arguments
/// pass through the model and are written into the session transcript, which this application
/// persists to SQLite with full-text search. A password given that way would be recoverable
/// from disk long after the conversation, and the model would have seen it for no reason. So
/// the tool opens a dialog and the value travels from the keyboard to the DPAPI store without
/// passing through either.
///
/// The same reasoning already governs the model providers: their API keys are typed into a
/// dialog, never into a prompt.
/// </summary>
public interface IConnectorConfigurator
{
    /// <summary>
    /// Put the dialog for one connector in front of the user and apply what they enter.
    /// </summary>
    /// <returns>A sentence to report, whatever the outcome -- including "they cancelled".</returns>
    Task<string> ConfigureAsync(string connector, CancellationToken cancellationToken);
}
