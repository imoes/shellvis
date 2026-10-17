using Shellvis.Contracts;
using Shellvis.Core.Broker;

namespace Shellvis.Core.Tools;

/// <summary>
/// The privileged operations, as tools.
///
/// Every one of them is <see cref="SideEffect.AlwaysAsk"/>, including the read-only ones.
/// That is a deliberate departure from the classifier's usual rule that provable reads run
/// silently: what makes these different is not what they do but where they run. A broker
/// call executes as LocalSystem, and "the agent asked the privileged service for
/// something" is worth a human's attention even when the something is harmless. A
/// "do not ask again" answer must never cover this boundary.
/// </summary>
public sealed class BrokerTools(BrokerClient client)
{
    private readonly BrokerClient _client = client;

    [ShellvisTool(
        "broker_status",
        SideEffect.AlwaysAsk,
        Description =
            "Check whether the privileged broker service is reachable and whether it "
            + "actually holds administrator rights. Use this before planning anything "
            + "that needs elevation, so you find out now rather than mid-task.",
        Glyph = "shield")]
    public async Task<string> Status(CancellationToken cancellationToken = default)
    {
        BrokerResponse response = await _client
            .SendAsync(BrokerOperation.Ping, [], cancellationToken)
            .ConfigureAwait(false);

        return response.Ok ? response.Output : response.Error ?? "the broker did not answer.";
    }

    [ShellvisTool(
        "broker_run_elevated",
        SideEffect.AlwaysAsk,
        Description =
            "Run a PowerShell script with administrator rights through the broker "
            + "service. Only for things that genuinely need elevation -- use "
            + "powershell_run for everything else, which is faster and needs no "
            + "privilege.",
        PreviewParameter = "script",
        Glyph = "shield")]
    public async Task<string> RunElevated(
        string script,
        int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(script))
            return "Give a script to run.";

        BrokerResponse response = await _client.SendAsync(
            BrokerOperation.RunElevated,
            new Dictionary<string, string>
            {
                ["script"] = script,
                ["timeoutSeconds"] = timeoutSeconds.ToString(),
            },
            cancellationToken).ConfigureAwait(false);

        return Render(response);
    }

    [ShellvisTool(
        "service_list",
        SideEffect.AlwaysAsk,
        Description =
            "List Windows services and their state through the broker. Pass a filter to "
            + "narrow it; an unfiltered machine has several hundred.",
        PreviewParameter = "filter",
        Glyph = "shield")]
    public async Task<string> ServiceList(
        string? filter = null, CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, string>();

        if (filter is { Length: > 0 })
            arguments["filter"] = filter;

        return Render(await _client
            .SendAsync(BrokerOperation.ServiceList, arguments, cancellationToken)
            .ConfigureAwait(false));
    }

    [ShellvisTool(
        "service_control",
        SideEffect.AlwaysAsk,
        Description =
            "Start, stop, restart or query a Windows service through the broker. "
            + "Stopping a service can take a machine off the network or end someone's "
            + "session, so say which service and why before asking.",
        PreviewParameter = "service",
        Glyph = "shield")]
    public async Task<string> ServiceControl(
        string service,
        string action = "status",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(service))
            return "Give a service name.";

        return Render(await _client.SendAsync(
            BrokerOperation.ServiceControl,
            new Dictionary<string, string> { ["service"] = service, ["action"] = action },
            cancellationToken).ConfigureAwait(false));
    }

    [ShellvisTool(
        "registry_read_hklm",
        SideEffect.AlwaysAsk,
        Description =
            "Read a machine-wide registry key or value under HKLM or HKCR through the "
            + "broker. Leave the value name empty to list the key's contents.",
        PreviewParameter = "path",
        Glyph = "shield")]
    public async Task<string> RegistryRead(
        string path,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new Dictionary<string, string> { ["path"] = path };

        if (name is { Length: > 0 })
            arguments["name"] = name;

        return Render(await _client
            .SendAsync(BrokerOperation.RegistryRead, arguments, cancellationToken)
            .ConfigureAwait(false));
    }

    [ShellvisTool(
        "registry_write_hklm",
        SideEffect.AlwaysAsk,
        Description =
            "Write a machine-wide registry value under HKLM or HKCR through the broker. "
            + "Kind is 'string' or 'dword'. Startup, service and security-policy "
            + "locations are refused by the broker regardless of approval.",
        PreviewParameter = "path",
        Glyph = "shield")]
    public async Task<string> RegistryWrite(
        string path,
        string name,
        string value,
        string kind = "string",
        CancellationToken cancellationToken = default)
    {
        return Render(await _client.SendAsync(
            BrokerOperation.RegistryWrite,
            new Dictionary<string, string>
            {
                ["path"] = path,
                ["name"] = name,
                ["value"] = value,
                ["kind"] = kind,
            },
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Turn a response into text for the model.
    ///
    /// A refusal is prefixed so it cannot be mistaken for output. The broker refuses for
    /// policy reasons -- a forbidden registry path, self-preservation -- and a model that
    /// read the reason as a result would report the action as done.
    /// </summary>
    private static string Render(BrokerResponse response)
    {
        if (response.Ok)
            return response.Output.Length > 0 ? response.Output : "done.";

        return "The broker did not do this: " + (response.Error ?? "no reason given.");
    }
}
