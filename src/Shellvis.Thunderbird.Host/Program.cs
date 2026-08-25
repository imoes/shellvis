using System.IO.Pipes;
using Shellvis.Contracts;

namespace Shellvis.Thunderbird.Host;

/// <summary>
/// Relays between Thunderbird and Shellvis.
///
/// Thunderbird has no COM interface and no scripting surface reachable from outside, so
/// the only supported route in is a MailExtension. An extension can talk to a native
/// messaging host over stdio -- and only to a process THUNDERBIRD spawned. Shellvis is a
/// separate long-running application, so something has to sit in between:
///
///     Thunderbird ──stdio (native messaging)── this host ──named pipe── Shellvis
///
/// The host understands nothing about mail. It moves framed messages between two
/// transports and keeps the two lifetimes independent, which is the whole job: Thunderbird
/// may be open for days with Shellvis started and stopped many times, or the other way
/// round.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Thunderbird passes the extension origin as the first argument. Kept for the log
        // rather than trusted: the allowed_extensions list in the manifest is what
        // actually restricts who may launch this.
        string origin = args.FirstOrDefault() ?? "(no origin)";

        Log($"host started for {origin}");

        using var stopping = new CancellationTokenSource();

        Stream toThunderbird = Console.OpenStandardOutput();
        Stream fromThunderbird = Console.OpenStandardInput();

        // A single in-flight request. The extension answers one at a time anyway, and a
        // correlation layer for a channel that carries a few messages a minute would be
        // machinery without a purpose.
        var pending = new SemaphoreSlim(1, 1);

        try
        {
            await ServeAsync(toThunderbird, fromThunderbird, pending, stopping.Token)
                .ConfigureAwait(false);

            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task ServeAsync(
        Stream toThunderbird,
        Stream fromThunderbird,
        SemaphoreSlim pending,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(
                ThunderbirdProtocol.PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

            Log("Shellvis connected");

            try
            {
                await RelayAsync(pipe, toThunderbird, fromThunderbird, pending, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // A dropped client is ordinary: Shellvis closed its window. The host keeps
                // listening, because Thunderbird is still open and will not respawn it.
                Log($"client gone: {ex.Message}");
            }

            Log("Shellvis disconnected");
        }
    }

    /// <summary>
    /// Serve one connected client until it goes away.
    /// </summary>
    private static async Task RelayAsync(
        NamedPipeServerStream pipe,
        Stream toThunderbird,
        Stream fromThunderbird,
        SemaphoreSlim pending,
        CancellationToken cancellationToken)
    {
        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            MailRequest? request = await ThunderbirdProtocol
                .ReadAsync<MailRequest>(pipe, cancellationToken)
                .ConfigureAwait(false);

            if (request is null)
                return;

            Log($"[{request.RequestId}] {request.Operation} -> Thunderbird");

            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);

            MailResponse response;

            try
            {
                await ThunderbirdProtocol
                    .WriteAsync(toThunderbird, request, cancellationToken)
                    .ConfigureAwait(false);

                // Bounded. If the extension is unloaded or wedged, Thunderbird never
                // answers and an unbounded wait would leave Shellvis hanging on a mail
                // call with no way to tell why.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));

                MailResponse? answer;

                try
                {
                    answer = await ThunderbirdProtocol
                        .ReadAsync<MailResponse>(fromThunderbird, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    answer = MailResponse.Failed(
                        "Thunderbird did not answer within 30s. Is the Shellvis extension "
                        + "enabled?",
                        request.RequestId);
                }

                response = answer ?? MailResponse.Failed(
                    "Thunderbird closed the native messaging port.", request.RequestId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response = MailResponse.Failed(
                    $"{ex.GetType().Name}: {ex.Message}", request.RequestId);
            }
            finally
            {
                pending.Release();
            }

            Log($"[{request.RequestId}] {(response.Ok ? "ok" : "error: " + response.Error)} -> Shellvis");

            await ThunderbirdProtocol
                .WriteAsync(pipe, response, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Log to a file only.
    ///
    /// Never to stdout: stdout IS the native messaging channel, and one stray line of text
    /// there is read as a length prefix. The resulting failure is a silent hang, which is
    /// the least debuggable outcome available -- so this rule is absolute in this process.
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Shellvis");

            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "thunderbird-host.log"),
                $"{DateTimeOffset.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
