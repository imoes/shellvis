using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shellvis.Contracts;

/// <summary>
/// What the broker can be asked to do.
///
/// A closed set, not a free-form command string. That distinction is the whole point of
/// having a broker: a pipe that accepted "run this" would be a privilege-escalation
/// service with a nice name, reachable by anything that can open the pipe. Every entry
/// here is a deliberate decision that this specific capability is worth exposing across
/// the trust boundary.
/// </summary>
public enum BrokerOperation
{
    /// <summary>Health check. Costs nothing and needs no approval.</summary>
    Ping,

    /// <summary>Run a PowerShell script with the service's rights.</summary>
    RunElevated,

    /// <summary>Start, stop or restart a Windows service.</summary>
    ServiceControl,

    /// <summary>Read a value under HKLM.</summary>
    RegistryRead,

    /// <summary>Write a value under HKLM.</summary>
    RegistryWrite,

    /// <summary>List the Windows services and their state.</summary>
    ServiceList,
}

/// <summary>One request across the pipe.</summary>
/// <param name="Operation">Which capability.</param>
/// <param name="Arguments">
/// Operation-specific values. A dictionary rather than a union type because the wire
/// format has to survive a version mismatch between shell and service: an unknown key is
/// ignorable, a changed record shape is not.
/// </param>
/// <param name="RequestId">Correlates the reply, and appears in the broker's log.</param>
public sealed record BrokerRequest(
    BrokerOperation Operation,
    Dictionary<string, string> Arguments,
    string RequestId)
{
    public string? Get(string name) =>
        Arguments.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>One reply.</summary>
/// <param name="Ok">Whether the operation was carried out.</param>
/// <param name="Output">What it produced, for the model to read.</param>
/// <param name="Error">Why it did not happen. Never contains a credential.</param>
public sealed record BrokerResponse(bool Ok, string Output, string? Error = null)
{
    public static BrokerResponse Failed(string reason) => new(false, string.Empty, reason);

    public static BrokerResponse Succeeded(string output) => new(true, output);
}

/// <summary>
/// Shared names and framing.
///
/// Both sides read these constants from the same assembly, so a rename cannot leave the
/// shell talking to a pipe the service is not listening on -- a failure that looks like
/// "the service is not running" and takes an afternoon to trace.
/// </summary>
public static class BrokerProtocol
{
    /// <summary>
    /// The pipe name.
    ///
    /// No "Global\" prefix: the broker serves the interactive user of this machine, and
    /// a global name would be visible from every session including other users'.
    /// </summary>
    public const string PipeName = "Shellvis.Broker";

    /// <summary>Bumped when the wire format changes incompatibly.</summary>
    public const int Version = 1;

    /// <summary>Name of the Windows service, for install and for self-preservation.</summary>
    public const string ServiceName = "ShellvisBroker";

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>
    /// Frame a message as one line of JSON.
    ///
    /// Newline-delimited rather than length-prefixed because a pipe carries a stream and
    /// the reader must know where a message ends; a newline is enough since the payload
    /// is JSON, which escapes its own newlines.
    /// </summary>
    public static string Frame<T>(T message) =>
        JsonSerializer.Serialize(message, Json).ReplaceLineEndings(string.Empty) + "\n";

    public static T? Parse<T>(string line) =>
        string.IsNullOrWhiteSpace(line) ? default : JsonSerializer.Deserialize<T>(line, Json);
}
