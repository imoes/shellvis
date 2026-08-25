using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shellvis.Contracts;

namespace Shellvis.Core.Mail;

/// <summary>
/// Thunderbird, reached through the native messaging host.
///
/// Every call is a fresh pipe connection for the same reason the broker client uses one:
/// the alternative needs reconnect logic and keep-alives for a channel carrying a handful
/// of messages a minute, and it would hold a handle open across a Thunderbird restart.
/// </summary>
public sealed class ThunderbirdProvider : IMailProvider
{
    /// <summary>
    /// How long to wait for the host's pipe.
    ///
    /// Short: the usual answer is that Thunderbird is not running at all, and that should
    /// come back immediately rather than as a stall that reads like a hang.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to wait for a reply once connected.
    ///
    /// Longer than the connect, because the host's own timeout to Thunderbird is 30s and
    /// this has to outlast it -- otherwise the client gives up first and the useful error
    /// the host was about to send ("is the extension enabled?") is never read.
    /// </summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(40);

    public string Name => "Thunderbird";

    /// <summary>
    /// How long a liveness check may take.
    ///
    /// Much shorter than a real call, and that distinction was missing at first: the probe
    /// used the full 40 second call timeout, so with the host running but Thunderbird not
    /// answering -- an extension disabled, say -- startup sat there for forty seconds
    /// before reporting anything. An availability check must fail fast; a mail read may
    /// reasonably take its time.
    /// </summary>
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        MailResponse response = await SendAsync(
            MailOperation.Ping, [], cancellationToken, PingTimeout).ConfigureAwait(false);

        return response.Ok;
    }

    public async Task<IReadOnlyList<MailFolder>> ListFoldersAsync(
        CancellationToken cancellationToken = default)
    {
        MailResponse response = await SendAsync(
            MailOperation.ListFolders, [], cancellationToken).ConfigureAwait(false);

        if (!response.Ok || response.Payload is null)
            return [];

        var folders = new List<MailFolder>();

        if (JsonNode.Parse(response.Payload) is not JsonArray array)
            return folders;

        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject obj)
                continue;

            folders.Add(new MailFolder(
                obj["id"]?.GetValue<string>() ?? string.Empty,
                obj["name"]?.GetValue<string>() ?? "(unnamed)",
                obj["total"]?.GetValue<int>() ?? 0,
                obj["unread"]?.GetValue<int>() ?? 0));
        }

        return folders;
    }

    public async Task<IReadOnlyList<MailMessage>> ListMessagesAsync(
        string? folder = null,
        bool unreadOnly = false,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, string>
        {
            ["unreadOnly"] = unreadOnly ? "true" : "false",
            ["limit"] = Math.Clamp(limit, 1, 200).ToString(),
        };

        if (folder is { Length: > 0 })
            arguments["folder"] = folder;

        MailResponse response = await SendAsync(
            MailOperation.ListMessages, arguments, cancellationToken).ConfigureAwait(false);

        if (!response.Ok || response.Payload is null)
            return [];

        var messages = new List<MailMessage>();

        if (JsonNode.Parse(response.Payload) is not JsonArray array)
            return messages;

        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject obj)
                continue;

            // Thunderbird hands out a numeric message id. Carried as a string because the
            // model passes it back verbatim and a number invites it to do arithmetic on
            // something opaque.
            string id = obj["id"]?.ToJsonString().Trim('"') ?? string.Empty;

            DateTimeOffset received = DateTimeOffset.TryParse(
                obj["date"]?.GetValue<string>(), out DateTimeOffset parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            messages.Add(new MailMessage(
                id,
                obj["subject"]?.GetValue<string>() ?? "(no subject)",
                obj["author"]?.GetValue<string>() ?? "(unknown)",
                received,
                obj["unread"]?.GetValue<bool>() ?? false));
        }

        return messages;
    }

    public async Task<string> ReadMessageAsync(
        string messageId, CancellationToken cancellationToken = default)
    {
        MailResponse response = await SendAsync(
            MailOperation.ReadMessage,
            new Dictionary<string, string> { ["id"] = messageId },
            cancellationToken).ConfigureAwait(false);

        if (!response.Ok)
            return Explain(response);

        return response.Payload is null
            ? "the extension returned no body."
            : Unwrap(response.Payload);
    }

    public async Task<string> DraftReplyAsync(
        string messageId,
        string body,
        bool replyAll = false,
        CancellationToken cancellationToken = default)
    {
        MailResponse response = await SendAsync(
            MailOperation.DraftReply,
            new Dictionary<string, string>
            {
                ["id"] = messageId,
                ["body"] = body,
                ["replyAll"] = replyAll ? "true" : "false",
            },
            cancellationToken).ConfigureAwait(false);

        // The result says "draft" explicitly. A model that read "reply created" as "reply
        // sent" would tell the user something untrue about an irreversible action.
        return response.Ok
            ? "Saved as a draft in Thunderbird. Nothing was sent -- review and send it yourself."
            : Explain(response);
    }

    public async Task<string> DraftMessageAsync(
        string to,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        MailResponse response = await SendAsync(
            MailOperation.DraftMessage,
            new Dictionary<string, string>
            {
                ["to"] = to,
                ["subject"] = subject,
                ["body"] = body,
            },
            cancellationToken).ConfigureAwait(false);

        return response.Ok
            ? $"Saved a draft to {to} in Thunderbird. Nothing was sent."
            : Explain(response);
    }

    /// <summary>Send one request over the host's pipe and wait for the reply.</summary>
    private static async Task<MailResponse> SendAsync(
        MailOperation operation,
        Dictionary<string, string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        var request = new MailRequest(operation, arguments, id);

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".", ThunderbirdProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ConnectTimeout);

            try
            {
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return MailResponse.Failed(
                    "the Thunderbird bridge is not running. It starts when Thunderbird "
                    + "loads the Shellvis extension, so check that Thunderbird is open and "
                    + "the extension is enabled.",
                    id);
            }

            using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            call.CancelAfter(timeout ?? CallTimeout);

            await ThunderbirdProtocol.WriteAsync(pipe, request, call.Token).ConfigureAwait(false);

            MailResponse? response = await ThunderbirdProtocol
                .ReadAsync<MailResponse>(pipe, call.Token)
                .ConfigureAwait(false);

            return response ?? MailResponse.Failed(
                "the bridge closed the connection without replying.", id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return MailResponse.Failed("the bridge did not reply in time.", id);
        }
        catch (Exception ex)
        {
            return MailResponse.Failed($"could not reach the bridge: {ex.Message}", id);
        }
    }

    private static string Explain(MailResponse response) =>
        response.Error ?? "the bridge reported a failure with no reason.";

    /// <summary>
    /// A JSON string payload becomes plain text; anything else is passed through.
    ///
    /// Without this a message body reaches the model as a quoted, escaped JSON string --
    /// the same double-encoding mistake that was found once already with tool results, and
    /// it costs tokens on every line.
    /// </summary>
    private static string Unwrap(string payload)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(payload);

            if (node?.GetValueKind() == JsonValueKind.String)
                return node.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        return payload;
    }
}
