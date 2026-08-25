using System.Text.Json;
using Shellvis.Core.Mcp;
using Shellvis.Core.Tools;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Verifies the MCP client against a real server over real stdio.
///
/// The happy path is the least interesting part. What this actually checks is that the
/// three trust boundaries hold, because an MCP server is remote code that describes its
/// own capabilities:
///
///  - a server cannot shadow a built-in tool name
///  - a tool description carrying an injection marker is refused, not merely logged
///  - a stdio child does not inherit the parent's secrets
///
/// The test server is built to fail all three deliberately.
/// </summary>
internal static class McpProbe
{
    public static async Task<int> RunAsync(string? serverPath)
    {
        string command = serverPath ?? DefaultServerPath();

        if (!File.Exists(command))
        {
            Console.Error.WriteLine($"test MCP server not found at {command}");
            Console.Error.WriteLine("build tools/Shellvis.TestMcpServer first.");
            return 1;
        }

        // A canary in the parent environment. If the child can read it, the client is
        // passing the whole environment through and would leak real API keys the same way.
        Environment.SetEnvironmentVariable("SHELLVIS_SECRET_CANARY", "leaked-if-visible");

        var registry = new ToolRegistry();

        // A built-in tool set is registered FIRST so the collision is real: the server
        // offers powershell_run, and something must already hold that name.
        var host = new PowerShellHostHolder();
        registry.RegisterFrom(host.Tools);
        int builtIn = registry.Count;

        Console.WriteLine($"{builtIn} built-in tool(s) registered before connecting\n");

        await using var mcp = new McpHost(registry);

        var config = new McpServerConfig(
            Name: "probe",
            Transport: McpTransport.Stdio,
            Command: command,
            // Only "echo" is granted silent execution, and only from the local config.
            TrustReadOnly: ["echo"]);

        McpServerStatus status = await mcp.ConnectAsync(config).ConfigureAwait(false);
        Console.WriteLine($"connect: {status}");
        Console.WriteLine($"detail:  {status.Detail}\n");

        int failures = 0;

        if (!status.Connected)
        {
            Console.Error.WriteLine("could not connect; nothing further can be checked.");
            return 1;
        }

        // ------------------------------------------------------- what got registered
        List<string> mcpTools = registry.Tools
            .Where(t => t.Name.StartsWith("mcp_probe_", StringComparison.Ordinal))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine("registered MCP tools:");
        foreach (string name in mcpTools)
        {
            ToolEntry entry = registry.Find(name)!;
            Console.WriteLine($"  {name,-28} {entry.SideEffect}");
        }

        Console.WriteLine();

        failures += Expect(mcpTools.Contains("mcp_probe_echo"), "echo is registered");
        failures += Expect(mcpTools.Contains("mcp_probe_add"), "add is registered");
        failures += Expect(
            !mcpTools.Contains("mcp_probe_helper"),
            "the injection-marker description is REFUSED");
        failures += Expect(
            registry.Find("powershell_run")!.SideEffect != SideEffect.ReadOnly,
            "the built-in powershell_run still owns its name");
        // The impostor IS registered, under its namespaced name, and that is correct:
        // namespacing is the protection. mcp_probe_powershell_run cannot be confused
        // with powershell_run by the dispatcher, and the prefix makes the provenance
        // visible to the model. The security property is that the built-in name is
        // untouched, not that the server's tool is suppressed.
        failures += Expect(
            registry.Find("mcp_probe_powershell_run") is not null,
            "the impostor is namespaced away rather than suppressed");

        // Namespaced registration keeps the built-in intact, and the server's colliding
        // tool must not have replaced it.
        failures += Expect(
            registry.Find("powershell_run")!.Description.Contains("persist", StringComparison.OrdinalIgnoreCase),
            "powershell_run still has its own description, not the impostor's");

        // ------------------------------------------------------------ side effects
        failures += Expect(
            registry.Find("mcp_probe_echo")!.SideEffect == SideEffect.ReadOnly,
            "echo is read-only because the LOCAL config said so");
        failures += Expect(
            registry.Find("mcp_probe_add")!.SideEffect == SideEffect.Mutating,
            "add defaults to mutating, since the server does not get to decide");

        // -------------------------------------------------------- actually call one
        using var args = JsonDocument.Parse("""{"text":"Shellvis"}""");
        string echoed = await registry.InvokeAsync("mcp_probe_echo", args.RootElement).ConfigureAwait(false);
        Console.WriteLine($"\ncall mcp_probe_echo -> {echoed.Trim()}");
        failures += Expect(echoed.Contains("Shellvis", StringComparison.Ordinal), "the round trip works");

        // ----------------------------------------------------------- secret leakage
        using var empty = JsonDocument.Parse("{}");
        string environment = await registry
            .InvokeAsync("mcp_probe_read_env", empty.RootElement)
            .ConfigureAwait(false);

        Console.WriteLine($"call mcp_probe_read_env -> {environment.Trim()}");
        failures += Expect(
            !environment.Contains("LEAKED", StringComparison.Ordinal),
            "the stdio child cannot see the parent's secrets");

        // ------------------------------------------------------------- disconnect
        string removed = await mcp.DisconnectAsync("probe").ConfigureAwait(false);
        Console.WriteLine($"\n{removed}");
        failures += Expect(
            registry.Find("mcp_probe_echo") is null,
            "disconnecting removes the server's tools");

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: MCP tools merge into the catalog, and all three trust boundaries hold."
            : $"\n{failures} check(s) failed.");

        host.Dispose();
        return failures == 0 ? 0 : 1;
    }

    private static string DefaultServerPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "Shellvis.TestMcpServer",
        "bin", "Debug", "net10.0", "Shellvis.TestMcpServer.exe"));

    private static int Expect(bool condition, string what)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }

    /// <summary>Keeps the PowerShell host alive for the duration of the probe.</summary>
    private sealed class PowerShellHostHolder : IDisposable
    {
        private readonly Core.Shell.PowerShellHost _host = new();

        public PowerShellTools Tools { get; }

        public PowerShellHostHolder() => Tools = new PowerShellTools(_host);

        public void Dispose() => Tools.Dispose();
    }
}
