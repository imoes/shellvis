using System.Text;
using Shellvis.Core.Shell;

namespace Shellvis.Core.Tools;

/// <summary>
/// Administering another machine: PowerShell Remoting, which is what replaced PsExec.
///
/// <b>Why this shape.</b> PsExec worked by copying a service binary to the target's ADMIN$
/// share and running it as SYSTEM. It still works and every endpoint protection product on
/// the market treats it as an attack, because that is precisely the technique attackers use
/// for lateral movement. The supported successors are WS-Man remoting (WinRM), which is
/// Kerberos-authenticated and needs no file copy, and PowerShell Remoting over SSH, which
/// works to Linux and to machines outside the domain. Both are exposed here.
///
/// <b>The sessions live in the runspace, not in this class.</b> Everything runs as script
/// through the existing <see cref="PowerShellHost"/>, and the PSSession objects are kept in
/// a hashtable inside it. That is deliberate: a session is stateful -- a variable set on the
/// remote host, an imported module, a working directory -- and it is the same reason the
/// local runspace is persistent. Holding the sessions here instead would mean reimplementing
/// the remoting client, the timeout, the error streams and the output formatting that the
/// host already gets right.
///
/// <b>The script is passed as base64.</b> Not for obscurity: it removes the quoting problem
/// completely. A remote script is arbitrary text arriving from a model, it will contain
/// quotes of both kinds, backticks and dollar signs, and this project has already been bitten
/// three times by nesting one language's quoting inside another's (cmd in a hook, sc.exe
/// binPath, PowerShell module names). Encoding sidesteps the entire class.
/// </summary>
public sealed class RemoteTools(PowerShellHost host)
{
    /// <summary>The hashtable inside the runspace that holds the open sessions.</summary>
    private const string Sessions = "$global:ShellvisRemoteSessions";

    /// <summary>
    /// Longest a remote call may take. Generous: a remote query crosses a network and may
    /// wait on a service on the far side, and the local default of a few seconds would make
    /// ordinary administration look broken.
    /// </summary>
    private const int DefaultTimeoutSeconds = 120;

    [ShellvisTool(
        "remote_connect",
        SideEffect.Mutating,
        Description =
            "Open a persistent PowerShell session to another machine, the modern "
            + "replacement for PsExec. Transport 'winrm' (default) uses Kerberos in the "
            + "domain and needs no password; 'ssh' reaches machines outside the domain and "
            + "non-Windows hosts, and needs a user name. The session stays open for later "
            + "remote_run calls, so state such as variables and imported modules survives. "
            + "Returns the remote machine's name and OS as proof the session is live.",
        PreviewParameter = "computer",
        Glyph = "remote")]
    public async Task<string> Connect(
        string computer,
        string transport = "winrm",
        string? userName = null,
        int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(computer))
            return "error: a computer name is required.";

        string name = computer.Trim();
        bool ssh = transport.Trim().Equals("ssh", StringComparison.OrdinalIgnoreCase);

        if (ssh && string.IsNullOrWhiteSpace(userName))
            return "error: the ssh transport needs a user name.";

        // The connection is made and then USED, in one script. A session that opens and
        // then cannot run anything is not a working connection, and reporting success on
        // the open alone would leave the model to discover that on its next call.
        // $$""" throughout this file, not $""".
        //
        // A raw string literal does NOT accept doubled braces as an escape -- that rule
        // belongs to ordinary interpolated strings. In a raw literal the number of dollar
        // signs sets how many braces open a hole, so two dollars means {{expr}} is a hole
        // and a single brace is literal text. Which is exactly what embedding a language
        // made of braces wants.
        string script = ssh
            ? $$"""
              {{Ensure()}}
              $s = New-PSSession -HostName '{{Quote(name)}}' -UserName '{{Quote(userName!)}}' -ErrorAction Stop
              {{Store(name)}}
              Invoke-Command -Session $s -ScriptBlock { "$([Environment]::MachineName)  $($PSVersionTable.OS)" }
              """
            : $$"""
              {{Ensure()}}
              $s = New-PSSession -ComputerName '{{Quote(name)}}' -ErrorAction Stop
              {{Store(name)}}
              Invoke-Command -Session $s -ScriptBlock { "$([Environment]::MachineName)  $([Environment]::OSVersion.VersionString)" }
              """;

        ShellResult result = await host
            .RunAsync(script, Budget(timeoutSeconds), cancellationToken)
            .ConfigureAwait(false);

        if (result.HadErrors)
            return Explain(name, ssh, result);

        return $"connected to {name} over {(ssh ? "ssh" : "winrm")}; the remote host reports: "
            + result.Output.Trim()
            + $"\nRun remote_run with computer '{name}' to work on it.";
    }

    [ShellvisTool(
        "remote_run",
        SideEffect.Mutating,
        Description =
            "Run PowerShell on a machine connected with remote_connect. The script runs in "
            + "the remote session, so variables and modules from earlier calls are still "
            + "there. Output comes back formatted the same way as a local run.",
        PreviewParameter = "script",
        Glyph = "remote")]
    public async Task<string> Run(
        string computer,
        string script,
        int timeoutSeconds = DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(computer))
            return "error: which computer? Pass the name used with remote_connect.";

        if (string.IsNullOrWhiteSpace(script))
            return "error: nothing to run.";

        string name = computer.Trim();
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));

        // if/else and a written message, NOT throw.
        //
        // A PowerShell `throw` in this wrapper leaves the host as an exception and escapes
        // the tool, which breaks the convention the whole tool layer rests on: a tool
        // failure is TEXT the model reads and corrects itself from. The probe caught it on
        // its first run -- asked to run against a machine it had not connected to, the
        // harness died instead of being told to connect first.
        string wrapper = $$"""
            {{Ensure()}}
            $s = {{Sessions}}['{{Quote(name)}}']
            if (-not $s) {
                "error: no session for '{{Quote(name)}}'. Call remote_connect first."
            } elseif ($s.State -ne 'Opened') {
                "error: the session to '{{Quote(name)}}' is $($s.State). Call remote_connect again."
            } else {
                $code = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{encoded}}'))
                Invoke-Command -Session $s -ScriptBlock ([ScriptBlock]::Create($code))
            }
            """;

        ShellResult result = await host
            .RunAsync(wrapper, Budget(timeoutSeconds), cancellationToken)
            .ConfigureAwait(false);

        return result.ToToolText();
    }

    [ShellvisTool(
        "remote_sessions",
        SideEffect.ReadOnly,
        Description = "List the machines currently connected, with the state of each session.",
        Glyph = "remote")]
    public async Task<string> List(CancellationToken cancellationToken = default)
    {
        string script = $$"""
            {{Ensure()}}
            if ({{Sessions}}.Count -eq 0) { 'no remote sessions are open.' }
            else {
                {{Sessions}}.GetEnumerator() | ForEach-Object {
                    "$($_.Key)  $($_.Value.State)  $($_.Value.Transport)  $($_.Value.ComputerName)"
                }
            }
            """;

        ShellResult result = await host.RunAsync(script, Budget(30), cancellationToken)
            .ConfigureAwait(false);

        return result.ToToolText();
    }

    [ShellvisTool(
        "remote_disconnect",
        SideEffect.Mutating,
        Description =
            "Close the session to a machine, or to all of them when no name is given. "
            + "Anything held only in that session is lost.",
        PreviewParameter = "computer",
        Glyph = "remote")]
    public async Task<string> Disconnect(
        string? computer = null,
        CancellationToken cancellationToken = default)
    {
        string script = string.IsNullOrWhiteSpace(computer)
            ? $$"""
              {{Ensure()}}
              $n = {{Sessions}}.Count
              {{Sessions}}.Values | ForEach-Object { Remove-PSSession $_ -ErrorAction SilentlyContinue }
              {{Sessions}}.Clear()
              "closed $n session(s)."
              """
            : $$"""
              {{Ensure()}}
              $k = '{{Quote(computer!.Trim())}}'
              if ({{Sessions}}.ContainsKey($k)) {
                  Remove-PSSession {{Sessions}}[$k] -ErrorAction SilentlyContinue
                  {{Sessions}}.Remove($k)
                  "closed the session to $k."
              } else { "there was no session to $k." }
              """;

        ShellResult result = await host.RunAsync(script, Budget(60), cancellationToken)
            .ConfigureAwait(false);

        return result.ToToolText();
    }

    [ShellvisTool(
        "remote_copy",
        SideEffect.Mutating,
        Description =
            "Copy a file or folder to or from a connected machine. Direction 'to' sends the "
            + "local path to the remote one; 'from' fetches it. This goes over the existing "
            + "session rather than a file share, so it needs no ADMIN$ and no second "
            + "authentication.",
        PreviewParameter = "source",
        Glyph = "remote")]
    public async Task<string> Copy(
        string computer,
        string source,
        string destination,
        string direction = "to",
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(computer) || string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(destination))
        {
            return "error: computer, source and destination are all required.";
        }

        bool sending = !direction.Trim().Equals("from", StringComparison.OrdinalIgnoreCase);
        string flag = sending ? "-ToSession" : "-FromSession";

        string script = $$"""
            {{Ensure()}}
            $s = {{Sessions}}['{{Quote(computer.Trim())}}']
            if (-not $s) {
                "error: no session for '{{Quote(computer.Trim())}}'. Call remote_connect first."
            } else {
                Copy-Item -Path '{{Quote(source.Trim())}}' -Destination '{{Quote(destination.Trim())}}' {{flag}} $s -Recurse -Force -ErrorAction Stop
                "copied {{(sending ? "to" : "from")}} {{Quote(computer.Trim())}}."
            }
            """;

        ShellResult result = await host
            .RunAsync(script, Budget(timeoutSeconds), cancellationToken)
            .ConfigureAwait(false);

        return result.ToToolText();
    }

    /// <summary>Create the session table if this is the first call in the runspace.</summary>
    private static string Ensure() =>
        $"if (-not {Sessions}) {{ {Sessions} = @{{}} }}";

    private static string Store(string name) =>
        $"{Sessions}['{Quote(name)}'] = $s";

    /// <summary>
    /// Escape for a SINGLE-quoted PowerShell string, where doubling the quote is the only
    /// rule. Double quotes would allow subexpression injection through a computer name.
    /// </summary>
    private static string Quote(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static TimeSpan Budget(int seconds) =>
        TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 1800));

    /// <summary>
    /// Turn a failed connection into something the user can act on.
    ///
    /// WS-Man errors are famously opaque -- "the client cannot connect" covers a firewall,
    /// a service that was never enabled, a name that does not resolve and a machine outside
    /// the domain, and the fix differs for each. The raw text is kept and the likely cause
    /// named after it, because guessing wrong should not hide the actual message.
    /// </summary>
    private static string Explain(string computer, bool ssh, ShellResult result)
    {
        string raw = result.ToToolText();

        string hint = ssh
            ? "Check that the host runs an SSH server with a PowerShell subsystem "
              + "configured in sshd_config, and that the key or password works from a "
              + "plain ssh session first."
            : raw.Contains("WinRM", StringComparison.OrdinalIgnoreCase)
                    || raw.Contains("cannot connect", StringComparison.OrdinalIgnoreCase)
                ? $"The usual causes, in order of likelihood: remoting was never enabled on "
                  + $"{computer} (an administrator runs Enable-PSRemoting there once), the "
                  + "firewall blocks TCP 5985, the name does not resolve, or the machine is "
                  + "outside this domain and needs the ssh transport or a TrustedHosts entry."
                : "Test-WSMan against the same name will say whether the problem is the "
                  + "transport or the credentials.";

        return $"could not connect to {computer}: {raw}\n{hint}";
    }
}
