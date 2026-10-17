using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Shellvis.Contracts;

namespace Shellvis.Broker;

/// <summary>
/// The privileged half of Shellvis.
///
/// It exists because two things need different rights and cannot live in one process.
/// Office automation is explicitly unsupported from a Windows service (KB 257757: Office
/// opens modal dialogs that nobody in session 0 can close, and the thread hangs forever),
/// while writing HKLM or controlling a service needs rights the interactive app must not
/// hold. So the interactive app holds no privilege at all and asks for the few things it
/// needs across a pipe.
///
/// The security of the whole arrangement is the pipe ACL. A named pipe with a permissive
/// DACL running as LocalSystem is a privilege-escalation service for anything on the
/// machine, which is exactly the shape of a well-known class of vulnerability. So the
/// pipe grants precisely two identities and nothing else.
/// </summary>
public sealed class BrokerServer(string? allowedUserSid, Action<string> log)
{
    private readonly BrokerOperations _operations = new(log);

    /// <summary>
    /// Build the pipe's access control.
    ///
    /// Two grants: the user who installed Shellvis, and the local Administrators group.
    /// Nothing for Everyone, Authenticated Users, or NETWORK -- and no SYSTEM grant is
    /// needed because the owner already has access. On a multi-user machine this is what
    /// stops another logged-in account from driving the privileged half.
    /// </summary>
    private PipeSecurity BuildSecurity()
    {
        var security = new PipeSecurity();

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        security.AddAccessRule(new PipeAccessRule(
            administrators, PipeAccessRights.FullControl, AccessControlType.Allow));

        if (allowedUserSid is { Length: > 0 })
        {
            try
            {
                var user = new SecurityIdentifier(allowedUserSid);

                // ReadWrite, not FullControl: the client needs to talk, not to change
                // the pipe's own security.
                security.AddAccessRule(new PipeAccessRule(
                    user, PipeAccessRights.ReadWrite, AccessControlType.Allow));

                log($"pipe grants {user.Translate(typeof(NTAccount))} and Administrators");
            }
            catch (Exception ex)
            {
                // A bad SID must not silently widen the ACL to "administrators only" in
                // a way nobody notices -- it is logged, and the broker still starts so
                // an administrator can fix the configuration.
                log($"WARNING: allowed user SID '{allowedUserSid}' is unusable ({ex.Message}); "
                    + "only administrators can reach the broker.");
            }
        }
        else
        {
            log("no allowed user configured; only administrators can reach the broker.");
        }

        // Explicitly deny the network. A named pipe is reachable over SMB by default,
        // and a privileged pipe that answers remote callers is a different product.
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);

        security.AddAccessRule(new PipeAccessRule(
            network, PipeAccessRights.FullControl, AccessControlType.Deny));

        return security;
    }

    /// <summary>Serve until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        log($"broker listening on \\\\.\\pipe\\{BrokerProtocol.PipeName} (protocol v{BrokerProtocol.Version})");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ServeOneAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad client must not take the broker down: it would then need an
                // administrator to restart, and the interactive app would report "the
                // service is not running" for a reason nobody can see.
                log($"connection failed: {ex.GetType().Name}: {ex.Message}");

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        log("broker stopped");
    }

    private async Task ServeOneAsync(CancellationToken cancellationToken)
    {
        using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
            BrokerProtocol.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            BuildSecurity());

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        string who = "unknown";

        try
        {
            // Logged, not enforced -- the ACL is the enforcement. But an audit trail of
            // who asked for a privileged operation is the first thing anyone wants after
            // an incident.
            who = pipe.GetImpersonationUserName();
        }
        catch (Exception)
        {
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 64 * 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
        {
            AutoFlush = true,
        };

        // One request per connection. A long-lived multiplexed connection would need
        // correlation and back-pressure for a channel that carries a handful of calls an
        // hour; the cost of reconnecting is microseconds.
        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        if (line is null)
            return;

        BrokerRequest? request;

        try
        {
            request = BrokerProtocol.Parse<BrokerRequest>(line);
        }
        catch (Exception ex)
        {
            await writer.WriteAsync(BrokerProtocol.Frame(
                BrokerResponse.Failed($"malformed request: {ex.Message}"))).ConfigureAwait(false);

            return;
        }

        if (request is null)
        {
            await writer.WriteAsync(BrokerProtocol.Frame(
                BrokerResponse.Failed("empty request"))).ConfigureAwait(false);

            return;
        }

        log($"[{request.RequestId}] {request.Operation} from {who}");

        BrokerResponse response = await _operations
            .ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        log($"[{request.RequestId}] {(response.Ok ? "ok" : "refused: " + response.Error)}");

        await writer.WriteAsync(BrokerProtocol.Frame(response)).ConfigureAwait(false);

        // Flush before the pipe is torn down, or the client reads end-of-stream instead
        // of the reply it is waiting for.
        try
        {
            pipe.WaitForPipeDrain();
        }
        catch (Exception)
        {
        }
    }
}
