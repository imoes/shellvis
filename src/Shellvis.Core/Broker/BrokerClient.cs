using System.IO.Pipes;
using System.Text;
using Shellvis.Contracts;

namespace Shellvis.Core.Broker;

/// <summary>
/// Talks to the privileged half.
///
/// A short-lived connection per call. A persistent one would need reconnect logic,
/// keep-alives and a correlation layer for a channel that carries a handful of calls an
/// hour, and it would hold a handle to a privileged pipe open for the life of the window.
/// Connecting costs microseconds.
/// </summary>
public sealed class BrokerClient
{
    /// <summary>
    /// How long to wait for the pipe.
    ///
    /// Short on purpose. The common case is that no broker is installed at all, and the
    /// answer to that question should be immediate rather than a ten-second stall that
    /// looks like a hang.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Whether a broker is reachable right now.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        BrokerResponse response = await SendAsync(
            BrokerOperation.Ping, [], cancellationToken).ConfigureAwait(false);

        return response.Ok;
    }

    /// <summary>Send one request and wait for the reply.</summary>
    public async Task<BrokerResponse> SendAsync(
        BrokerOperation operation,
        Dictionary<string, string> arguments,
        CancellationToken cancellationToken = default)
    {
        var request = new BrokerRequest(
            operation,
            arguments,
            // Short and readable: this id appears in the broker's log, where it is what
            // ties a privileged action to the request that asked for it.
            Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // "." rather than a host name: the pipe is local by design and the broker
            // explicitly denies the NETWORK sid. Naming a server here would be the first
            // step towards a remote privileged channel.
            using var pipe = new NamedPipeClientStream(
                ".", BrokerProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(ConnectTimeout);

            try
            {
                await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return BrokerResponse.Failed(
                    "no broker is listening. Privileged actions need the Shellvis service, "
                    + "which is chosen while installing: run the installer again and pick the "
                    + "machine-wide option, which needs administrator rights.");
            }

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
            {
                AutoFlush = true,
            };

            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);

            await writer.WriteAsync(BrokerProtocol.Frame(request)).ConfigureAwait(false);

            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                // The broker closing without answering means it crashed or refused at a
                // level below the protocol; saying which is not possible from here, so
                // the message points at the log that does know.
                return BrokerResponse.Failed(
                    "the broker closed the connection without replying. Its log is in "
                    + @"%ProgramData%\Shellvis.");
            }

            return BrokerProtocol.Parse<BrokerResponse>(line)
                ?? BrokerResponse.Failed("the broker sent an unreadable reply.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            // The pipe exists but this account is not on its ACL. That is the mechanism
            // working, and it deserves a message that says so rather than "access denied".
            return BrokerResponse.Failed(
                "the broker refused this account. Its pipe grants only the user Shellvis "
                + "was installed for, and Administrators.");
        }
        catch (Exception ex)
        {
            return BrokerResponse.Failed($"could not reach the broker: {ex.Message}");
        }
    }
}
