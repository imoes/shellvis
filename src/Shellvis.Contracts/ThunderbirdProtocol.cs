using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shellvis.Contracts;

/// <summary>What the extension is being asked to do.</summary>
public enum MailOperation
{
    /// <summary>Are you there.</summary>
    Ping,

    ListFolders,

    ListMessages,

    ReadMessage,

    /// <summary>Compose a reply and leave it in Drafts.</summary>
    DraftReply,

    /// <summary>Compose a new message and leave it in Drafts.</summary>
    DraftMessage,
}

/// <summary>A request travelling to the Thunderbird extension.</summary>
public sealed record MailRequest(
    MailOperation Operation,
    Dictionary<string, string> Arguments,
    string RequestId)
{
    public string? Get(string name) =>
        Arguments.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>A reply travelling back.</summary>
/// <param name="Payload">
/// Operation-specific JSON, left as a string. The host is a relay and has no business
/// understanding the shapes it carries -- parsing them there would mean a schema change in
/// the extension breaking the host as well.
/// </param>
public sealed record MailResponse(bool Ok, string? Payload, string? Error, string RequestId)
{
    public static MailResponse Failed(string reason, string requestId = "") =>
        new(false, null, reason, requestId);
}

/// <summary>
/// Names and framing for the Thunderbird bridge.
///
/// Thunderbird has no COM interface, so the only supported way in is a MailExtension
/// talking to a native messaging host. That protocol is not JSON lines: each message is a
/// **4-byte little-endian length** followed by exactly that many bytes of UTF-8 JSON.
/// Getting the framing wrong does not produce an error -- it produces a hang, because the
/// reader waits for bytes that will never come. That is why it lives here, written once,
/// and is exercised directly by a probe.
/// </summary>
public static class ThunderbirdProtocol
{
    /// <summary>
    /// The pipe Shellvis connects to.
    ///
    /// The host serves and Shellvis connects, not the other way round: Thunderbird spawns
    /// the host whenever the extension loads, which may be long before -- or entirely
    /// without -- Shellvis running. The side that is always there should be the listener.
    /// </summary>
    public const string PipeName = "Shellvis.Thunderbird";

    /// <summary>Identity used in the native messaging manifest and by the extension.</summary>
    public const string HostName = "media.ippen.shellvis";

    /// <summary>
    /// Thunderbird's own ceiling on a single native message.
    ///
    /// One megabyte, and exceeding it kills the connection rather than truncating. A mail
    /// with a long HTML body reaches this easily, so the extension truncates and the host
    /// refuses anything larger instead of writing a frame that would tear down the port.
    /// </summary>
    public const int MaxMessageBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Write one native message: length prefix, then the JSON.</summary>
    public static async Task WriteAsync<T>(
        Stream stream, T message, CancellationToken cancellationToken = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);

        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException(
                $"the message is {payload.Length} bytes, over Thunderbird's "
                + $"{MaxMessageBytes} byte limit. Exceeding it disconnects the port "
                + "rather than truncating, so it is refused here.");
        }

        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);

        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read one native message, or null at end of stream.
    ///
    /// ReadExactlyAsync, not ReadAsync: a pipe delivers what it happens to have, and a
    /// partial read treated as a whole frame desynchronises the stream for good -- every
    /// later message is then misparsed, which looks like corruption rather than a framing
    /// bug.
    /// </summary>
    public static async Task<T?> ReadAsync<T>(
        Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] prefix = new byte[4];

        try
        {
            await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            // The other side closed. Normal: Thunderbird shuts the port down when the
            // extension unloads.
            return default;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);

        if (length < 0 || length > MaxMessageBytes)
        {
            // A nonsense length is how a desynchronised stream first shows itself, and
            // reading it would allocate whatever the bytes happened to say.
            throw new InvalidDataException(
                $"the message length prefix says {length} bytes, which is outside "
                + $"0..{MaxMessageBytes}. The stream is out of step.");
        }

        if (length == 0)
            return default;

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<T>(payload, Json);
    }

    /// <summary>
    /// The native messaging manifest Thunderbird reads to find the host.
    ///
    /// Produced here rather than shipped as a file, because the path to the executable is
    /// only known at install time and a manifest with a stale path fails silently: the
    /// extension simply never connects.
    /// </summary>
    public static string BuildManifest(string hostExecutablePath, string extensionId) =>
        JsonSerializer.Serialize(
            new
            {
                name = HostName,
                description = "Shellvis bridge to Thunderbird",
                path = hostExecutablePath,
                type = "stdio",
                allowed_extensions = new[] { extensionId },
            },
            new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Where the manifest goes for a per-user Thunderbird install.</summary>
    public static string ManifestRegistryKey =>
        $@"SOFTWARE\Mozilla\NativeMessagingHosts\{HostName}";

    internal static string Describe(byte[] payload) => Encoding.UTF8.GetString(payload);
}
