using System.Text;
using Shellvis.Core.Shell;

namespace Shellvis.Core.Tools;

/// <summary>
/// PowerShell as tools, including the on-demand cmdlet catalog.
///
/// The catalog is the point of this class. Windows exposes on the order of ten
/// thousand cmdlets once the common modules are loaded, and every additional module
/// adds more. Putting that list in the system prompt is impossible, and leaving it out
/// means the model only ever uses the handful of cmdlets it happens to remember. So
/// the catalog is searchable on demand, and -- crucially -- anything a script imports
/// is reported back with its new commands attached, so a module becomes usable in the
/// same turn it was loaded.
///
/// All catalog queries run as a SINGLE PowerShell pipeline rather than as a loop of
/// invocations from C#. Get-Help is a per-command lookup, and for a module with
/// seventy cmdlets the difference between one pipeline and seventy round trips is the
/// difference between a second and a minute.
/// </summary>
public sealed class PowerShellTools(PowerShellHost host) : IDisposable
{
    /// <summary>
    /// Cap on commands reported for a newly imported module. A few large modules
    /// export several hundred, and dumping all of them would crowd out the task.
    /// </summary>
    private const int MaxReportedCommands = 60;

    /// <summary>Cap on search results, for the same reason.</summary>
    private const int MaxSearchResults = 30;

    [ShellvisTool(
        "powershell_run",
        SideEffect.Mutating,
        Description =
            "Run a PowerShell script in a session that persists between calls, so "
            + "variables, imported modules and the current directory survive. If the "
            + "script imports a module, the result lists the commands it made "
            + "available. Prefer this over cmd.",
        PreviewParameter = "script",
        Glyph = "terminal")]
    public async Task<string> Run(
        string script,
        int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        ShellResult result = await host
            .RunAsync(script, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 900)), cancellationToken)
            .ConfigureAwait(false);

        var sb = new StringBuilder(result.ToToolText());
        await AppendNewCommandsAsync(sb, result, cancellationToken).ConfigureAwait(false);
        return sb.ToString();
    }

    [ShellvisTool(
        "powershell_modules_list",
        SideEffect.ReadOnly,
        Description =
            "List PowerShell modules available on this machine, with version and how "
            + "many commands each exports. Use it to discover what could be imported. "
            + "Pass a name pattern to narrow it down.",
        PreviewParameter = "namePattern",
        Glyph = "package")]
    public async Task<string> ListModules(
        string? namePattern = null,
        bool loadedOnly = false,
        CancellationToken cancellationToken = default)
    {
        string filter = string.IsNullOrWhiteSpace(namePattern) ? "*" : $"*{namePattern.Trim('*')}*";
        string command = loadedOnly ? "Get-Module" : "Get-Module -ListAvailable";

        // Grouping by name keeps side-by-side versions from tripling the list.
        string script = $$"""
            {{command}} -Name '{{Escape(filter)}}' -ErrorAction SilentlyContinue |
              Group-Object Name |
              ForEach-Object {
                $newest = $_.Group | Sort-Object Version -Descending | Select-Object -First 1
                '{0}  v{1}  ({2} commands)' -f $newest.Name, $newest.Version, $newest.ExportedCommands.Count
              } |
              Sort-Object
            """;

        ShellResult result = await host.RunAsync(script, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result.ToToolText();
    }

    [ShellvisTool(
        "powershell_module_import",
        SideEffect.Mutating,
        Description =
            "Import a PowerShell module into the session and list the commands it "
            + "makes available, with a one-line description each. After this the "
            + "module stays loaded and its cmdlets can be used directly.",
        PreviewParameter = "moduleName",
        Glyph = "package")]
    public async Task<string> ImportModule(
        string moduleName,
        bool useWindowsPowerShell = false,
        CancellationToken cancellationToken = default)
    {
        // WinCompat proxies a module through a background Windows PowerShell 5.1
        // session. Needed for modules that were never ported to PowerShell 7, of
        // which there are still many in Windows administration.
        string compat = useWindowsPowerShell ? " -UseWindowsPowerShell" : string.Empty;

        string script = $"Import-Module -Name '{Escape(moduleName)}'{compat} -ErrorAction Stop -PassThru | "
            + "ForEach-Object { 'imported {0} v{1}' -f $_.Name, $_.Version }";

        ShellResult result = await host
            .RunAsync(script, TimeSpan.FromMinutes(2), cancellationToken)
            .ConfigureAwait(false);

        var sb = new StringBuilder(result.ToToolText());

        if (result.HadErrors)
        {
            sb.AppendLine()
              .AppendLine("The module was not imported. If it targets Windows PowerShell 5.1 only, "
                  + "retry with useWindowsPowerShell set to true.");
            return sb.ToString();
        }

        // The module the caller asked for is the one to describe, even if the diff is
        // empty because it was already loaded.
        await AppendCommandsForAsync(sb, [moduleName], cancellationToken).ConfigureAwait(false);
        return sb.ToString();
    }

    [ShellvisTool(
        "powershell_cmdlets_search",
        SideEffect.ReadOnly,
        Description =
            "Search the cmdlets and functions currently available in the session by "
            + "name or keyword, with a one-line description each. Use this to find the "
            + "right command instead of guessing at one.",
        PreviewParameter = "query",
        Glyph = "search")]
    public async Task<string> SearchCmdlets(
        string query,
        string? moduleName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "error: a search term is required.";

        string pattern = $"*{query.Trim('*')}*";
        string moduleFilter = string.IsNullOrWhiteSpace(moduleName)
            ? string.Empty
            : $" -Module '{Escape(moduleName)}'";

        string script = $$"""
            $found = Get-Command -Name '{{Escape(pattern)}}'{{moduleFilter}} `
                -CommandType Cmdlet,Function -ErrorAction SilentlyContinue |
              Sort-Object Name | Select-Object -First {{MaxSearchResults}}
            if (-not $found) { 'no command matches {{Escape(pattern)}} in the current session'; return }
            $found | ForEach-Object {
              $s = ''
              try { $s = (Get-Help $_.Name -ErrorAction Stop).Synopsis } catch { }
              if ($s -like ('*' + $_.Name + '*')) { $s = '' }
              '{0,-38} {1,-22} {2}' -f $_.Name, $_.ModuleName, $s
            }
            """;

        ShellResult result = await host
            .RunAsync(script, TimeSpan.FromMinutes(2), cancellationToken)
            .ConfigureAwait(false);

        return result.ToToolText();
    }

    [ShellvisTool(
        "powershell_cmdlet_help",
        SideEffect.ReadOnly,
        Description =
            "Show the full help for one cmdlet: purpose, syntax, parameters and "
            + "examples. Use it before calling a command you have not used before.",
        PreviewParameter = "cmdletName",
        Glyph = "read")]
    public async Task<string> CmdletHelp(
        string cmdletName,
        bool examplesOnly = false,
        CancellationToken cancellationToken = default)
    {
        string script = examplesOnly
            ? $"Get-Help -Name '{Escape(cmdletName)}' -Examples -ErrorAction Stop"
            : $"Get-Help -Name '{Escape(cmdletName)}' -Full -ErrorAction Stop";

        ShellResult result = await host
            .RunAsync(script, TimeSpan.FromMinutes(2), cancellationToken)
            .ConfigureAwait(false);

        string text = result.ToToolText();

        // Full help for a complex cmdlet runs to thousands of lines. Truncating with
        // an explicit pointer is better than either flooding the context or silently
        // cutting it off.
        const int limit = 9000;
        return text.Length <= limit
            ? text
            : text[..limit] + $"\n\n... help truncated at {limit} characters. "
                + "Call again with examplesOnly for just the examples.";
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// If the last call loaded new modules, append what they brought with them.
    ///
    /// This is the mechanism that makes a freshly imported module usable in the same
    /// turn. Without it the model would import something and then have no idea what it
    /// can now call, which is exactly the gap that made this feature necessary.
    /// </summary>
    private async Task AppendNewCommandsAsync(
        StringBuilder sb, ShellResult result, CancellationToken cancellationToken)
    {
        if (result.NewModules.Count == 0)
            return;

        await AppendCommandsForAsync(sb, result.NewModules, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendCommandsForAsync(
        StringBuilder sb, IReadOnlyList<string> modules, CancellationToken cancellationToken)
    {
        foreach (string module in modules)
        {
            string script = $$"""
                $cmds = Get-Command -Module '{{Escape(module)}}' -CommandType Cmdlet,Function `
                    -ErrorAction SilentlyContinue | Sort-Object Name
                if (-not $cmds) { return }
                $total = @($cmds).Count
                $cmds | Select-Object -First {{MaxReportedCommands}} | ForEach-Object {
                  $s = ''
                  try { $s = (Get-Help $_.Name -ErrorAction Stop).Synopsis } catch { }
                  if ($s -like ('*' + $_.Name + '*')) { $s = '' }
                  '  {0,-38} {1}' -f $_.Name, $s
                }
                if ($total -gt {{MaxReportedCommands}}) {
                  '  ... and {0} more; use powershell_cmdlets_search to find them' -f ($total - {{MaxReportedCommands}})
                }
                """;

            ShellResult listing = await host
                .RunAsync(script, TimeSpan.FromMinutes(3), cancellationToken)
                .ConfigureAwait(false);

            if (listing.Output.Trim().Length == 0)
                continue;

            sb.AppendLine()
              .AppendLine()
              .Append("Commands now available from module '").Append(module).AppendLine("':")
              .Append(listing.Output.TrimEnd());
        }
    }

    /// <summary>
    /// Escape a value for interpolation into a single-quoted PowerShell string.
    ///
    /// Single quotes are the safe container in PowerShell: nothing inside them
    /// expands, so doubling the quote character is the entire escaping rule. Building
    /// these scripts with double quotes would open the door to subexpression
    /// injection through a module name.
    /// </summary>
    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    public void Dispose() => host.Dispose();
}
