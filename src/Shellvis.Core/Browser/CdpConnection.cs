using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shellvis.Core.Browser;

/// <summary>
/// A Chrome DevTools Protocol connection: JSON-RPC over one WebSocket.
///
/// Written directly rather than taken from Playwright, and the reason is the runtime
/// constraint this project set for itself. Playwright's .NET package drives a bundled
/// Node.js process; that is precisely the class of runtime dependency Shellvis was
/// meant not to have. CDP is a stable, documented protocol and the part of it a
/// browsing agent needs -- navigate, read the page, click, type, screenshot -- is a few
/// dozen methods. What Playwright buys on top of that is auto-waiting and a large
/// selector engine, neither of which helps a model that addresses elements by
/// reference from a snapshot it was just given.
///
/// One connection speaks to the browser AND to its pages. CDP calls that "flat" mode:
/// a page is addressed by putting its sessionId on the message rather than by opening a
/// second socket. That matters because a page can be replaced under you at any moment
/// (a navigation, a crash, a tab closing), and a per-page socket would then have to be
/// torn down and rebuilt while commands are in flight.
/// </summary>
internal sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _shutdown = new();

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();

    /// <summary>
    /// Failure reason once the receive loop has stopped.
    ///
    /// Held so that a command issued after the browser went away fails with what
    /// actually happened rather than timing out and reporting nothing.
    /// </summary>
    private volatile string? _closedBecause;

    private Task? _receiveLoop;
    private int _nextId;

    /// <summary>Raised for every protocol event, with its session id when it has one.</summary>
    public event Action<string, string?, JsonNode?>? EventReceived;

    public static async Task<CdpConnection> ConnectAsync(
        Uri webSocketUrl, CancellationToken cancellationToken)
    {
        var connection = new CdpConnection();

        // The default receive buffer is generous enough for control frames but a
        // getFullAXTree response is megabytes; the reader below reassembles fragments
        // so the buffer size only affects syscall count.
        connection._socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        await connection._socket.ConnectAsync(webSocketUrl, cancellationToken).ConfigureAwait(false);

        connection._receiveLoop = Task.Run(connection.ReceiveAsync);

        return connection;
    }

    /// <summary>
    /// Issue a command and wait for its reply.
    /// </summary>
    /// <param name="sessionId">
    /// The page to address, or null for the browser itself. This single parameter is
    /// what flat mode buys: the same socket reaches every target.
    /// </param>
    public async Task<JsonNode?> SendAsync(
        string method,
        JsonObject? parameters = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (_closedBecause is { } reason)
            throw new InvalidOperationException($"The browser connection is closed: {reason}");

        int id = Interlocked.Increment(ref _nextId);

        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
        };

        if (parameters is not null)
            message["params"] = parameters;

        if (sessionId is not null)
            message["sessionId"] = sessionId;

        var completion = new TaskCompletionSource<JsonNode?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[id] = completion;

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());

            await _socket
                .SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);

            using CancellationTokenRegistration registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();

        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await _socket
                    .ReceiveAsync(buffer, _shutdown.Token)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Fail("the browser closed the connection");
                    return;
                }

                message.Write(buffer, 0, result.Count);

                // A single CDP reply routinely arrives in many frames -- a screenshot or
                // a large DOM easily runs to megabytes -- so it must be reassembled
                // before parsing rather than parsed per frame.
                if (!result.EndOfMessage)
                    continue;

                message.Position = 0;
                JsonNode? node = JsonNode.Parse(message);
                message.SetLength(0);

                Dispatch(node);
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void Dispatch(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return;

        if (obj["id"]?.GetValue<int>() is { } id)
        {
            if (!_pending.TryRemove(id, out TaskCompletionSource<JsonNode?>? completion))
                return;

            if (obj["error"] is JsonObject error)
            {
                // The protocol's own message is far more useful than anything this layer
                // could invent: "Cannot find context with specified id" tells a model
                // its snapshot is stale, which is exactly the next thing to fix.
                string text = error["message"]?.GetValue<string>() ?? "unknown protocol error";
                string? data = error["data"]?.GetValue<string>();

                completion.TrySetException(new CdpException(
                    data is { Length: > 0 } ? $"{text}: {data}" : text));

                return;
            }

            completion.TrySetResult(obj["result"]);
            return;
        }

        if (obj["method"]?.GetValue<string>() is { } method)
            EventReceived?.Invoke(method, obj["sessionId"]?.GetValue<string>(), obj["params"]);
    }

    /// <summary>
    /// Tear down every waiter with a real reason.
    ///
    /// Without this, losing the browser turns every in-flight command into a hang, and
    /// the agent looks stuck rather than disconnected.
    /// </summary>
    private void Fail(string reason)
    {
        _closedBecause = reason;

        foreach (KeyValuePair<int, TaskCompletionSource<JsonNode?>> pair in _pending)
        {
            pair.Value.TrySetException(
                new CdpException($"The browser connection dropped: {reason}"));
        }

        _pending.Clear();
    }

    public bool IsOpen => _closedBecause is null && _socket.State == WebSocketState.Open;

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, "shellvis", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Closing a socket that is already gone is not worth reporting.
        }

        if (_receiveLoop is not null)
        {
            // Bounded: a receive loop blocked on a dead socket must not hold up window
            // close.
            await Task.WhenAny(_receiveLoop, Task.Delay(TimeSpan.FromSeconds(2)))
                .ConfigureAwait(false);
        }

        _socket.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>A failure the browser itself reported.</summary>
internal sealed class CdpException(string message) : Exception(message);
