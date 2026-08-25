using Shellvis.Core.Config;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Verifies the configuration round trip, and especially the one behaviour that would
/// leak secrets if it were wrong.
///
/// A config may reference ${OPENROUTER_API_KEY} so the key never lives in the file.
/// But Shellvis rewrites the file whenever something changes -- an "always allow"
/// answer, a model switch -- and a naive serialize would write the RESOLVED value.
/// The first such write would copy every referenced secret into plaintext, silently,
/// in a file the user has every reason to believe is safe to commit.
///
/// So the round trip is tested with a real secret-looking value and the file is
/// inspected afterwards to confirm the reference survived and the value did not.
/// </summary>
internal static class ConfigProbe
{
    public static int Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "shellvis-config-probe");

        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);

        Directory.CreateDirectory(folder);

        string file = Path.Combine(folder, "config.yaml");
        const string secret = "sk-do-not-write-me-to-disk-12345";
        Environment.SetEnvironmentVariable("SHELLVIS_PROBE_KEY", secret);

        int failures = 0;

        // ----------------------------------------------- a default file is created
        ConfigLoadResult first = ConfigStore.Load(file);
        failures += Expect(first.Created, "a default config is written when none exists");
        failures += Expect(File.Exists(file), "the file is on disk");
        failures += Expect(
            File.ReadAllText(file).Contains("mcpServers", StringComparison.Ordinal),
            "the default file documents the MCP section");
        failures += Expect(
            first.Config.Approvals.Mode == "auto-read",
            "the default approval mode is auto-read, not yolo");

        // ------------------------------------------- a reference is resolved on read
        File.WriteAllText(file, """
            configVersion: 1
            model:
              provider: custom
              model: laguna
              baseUrl: https://example.invalid/llama/v1
              apiKeyEnvVar: SHELLVIS_PROBE_KEY
            agent:
              maxIterations: 7
            approvals:
              mode: ask
              timeoutSeconds: 120
            mcpServers:
              probe:
                transport: http
                url: https://mcp.example.internal/v1
                headers:
                  Authorization: Bearer ${SHELLVIS_PROBE_KEY}
                trustReadOnly: ["ping"]
            """);

        ConfigLoadResult loaded = ConfigStore.Load(file);

        foreach (string warning in loaded.Warnings)
            Console.WriteLine($"       warning: {warning}");

        failures += Expect(loaded.Config.Agent.MaxIterations == 7, "scalars round-trip");
        failures += Expect(loaded.Config.Approvals.Mode == "ask", "the approval mode is read");
        failures += Expect(loaded.Config.McpServers.ContainsKey("probe"), "the MCP server is read");

        McpServerSection server = loaded.Config.McpServers["probe"];
        failures += Expect(
            server.Headers["Authorization"].Contains(secret, StringComparison.Ordinal),
            "the environment reference is RESOLVED in memory");
        failures += Expect(
            server.TrustReadOnly.Contains("ping"),
            "trustReadOnly is read from the local config");

        // ---------------------------------------- and preserved when written back
        loaded.Config.Approvals.CommandAllowlist.Add("Get-Process");
        ConfigStore.Save(loaded.Config, file);

        string written = File.ReadAllText(file);

        failures += Expect(
            !written.Contains(secret, StringComparison.Ordinal),
            "the resolved secret is NOT written to disk");
        failures += Expect(
            written.Contains("${SHELLVIS_PROBE_KEY}", StringComparison.Ordinal),
            "the ${VAR} reference is preserved");
        failures += Expect(
            written.Contains("Get-Process", StringComparison.Ordinal),
            "the programmatic change was actually saved");

        // ------------------------------------------- a broken file must not be fatal
        File.WriteAllText(file, "model:\n  provider: [this is not valid\n");
        ConfigLoadResult broken = ConfigStore.Load(file);

        failures += Expect(broken.Warnings.Count > 0, "a parse error is reported");
        failures += Expect(
            broken.Warnings.Any(w => w.Contains("line", StringComparison.OrdinalIgnoreCase)),
            "the report says WHERE the error is");
        failures += Expect(
            broken.Config.Approvals.Mode == "auto-read",
            "a broken config falls back to defaults rather than refusing to start");

        // ----------------------------------- an unset reference is flagged, not blanked
        File.WriteAllText(file, "model:\n  apiKeyEnvVar: ${SHELLVIS_DEFINITELY_NOT_SET}\n");
        ConfigLoadResult missing = ConfigStore.Load(file);

        failures += Expect(
            missing.Warnings.Any(w => w.Contains("SHELLVIS_DEFINITELY_NOT_SET", StringComparison.Ordinal)),
            "an unset reference is named in a warning");
        failures += Expect(
            missing.Config.Model.ApiKeyEnvVar?.Contains("${", StringComparison.Ordinal) == true,
            "an unset reference stays literal rather than becoming an empty string");

        Console.WriteLine(failures == 0
            ? "\nVERIFIED: config round-trips, and referenced secrets never reach the file."
            : $"\n{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }

    private static int Expect(bool condition, string what)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }
}
