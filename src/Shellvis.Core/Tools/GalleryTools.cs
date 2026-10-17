using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Shellvis.Core.Shell;

namespace Shellvis.Core.Tools;

/// <summary>
/// The PowerShell Gallery: thousands of modules, searchable and installable on demand.
///
/// Search goes over the OData v2 feed rather than through Find-PSResource. Two reasons,
/// both established by testing against the live gallery: the NuGet v3 index the
/// gallery advertises returns 403, so v2 is the only usable API; and querying it
/// directly works without a registered repository and allows proper sorting by
/// download count, which is the single most useful relevance signal a gallery search
/// has.
///
/// Installing is the most dangerous single capability in the whole application. The
/// gallery is not curated, anyone may publish, and importing a module executes its
/// code. So installation is classified <see cref="SideEffect.AlwaysAsk"/>: it prompts
/// in every mode, including yolo, exactly like a recursive delete.
/// </summary>
public sealed class GalleryTools(PowerShellHost host)
{
    private const string ODataBase = "https://www.powershellgallery.com/api/v2";

    /// <summary>Atom and OData namespaces, needed to read the feed.</summary>
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace DataServices = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace Metadata = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

    /// <summary>
    /// Where agent-installed modules go.
    ///
    /// Not the user's Documents\WindowsPowerShell\Modules: on this machine that path is
    /// redirected into OneDrive, which means sync conflicts, locked files, latency, and
    /// a path containing both spaces and a non-ASCII character. A dedicated directory
    /// also keeps what the agent installed separate from what the user installed.
    /// </summary>
    private static string ModuleRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shellvis", "Modules");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    /// <summary>
    /// Publishers whose modules get a shorter prompt. Everything else is shown in full.
    /// </summary>
    private static readonly HashSet<string> TrustedPublishers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Corporation", "Microsoft", "PowerShell Team",
        "VMware, Inc.", "Amazon.com, Inc", "Amazon Web Services",
    };

    [ShellvisTool(
        "psgallery_search",
        SideEffect.ReadOnly,
        Description =
            "Search the PowerShell Gallery for modules by keyword. Returns name, "
            + "version, author and download count, most downloaded first. Use it to "
            + "find a module that provides a capability this machine does not have yet.",
        PreviewParameter = "query",
        Glyph = "search")]
    public async Task<string> Search(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "error: a search term is required.";

        int take = Math.Clamp(limit, 1, 40);

        string url = $"{ODataBase}/Search()"
            + $"?$filter=IsLatestVersion"
            + $"&searchTerm='{Uri.EscapeDataString(query)}'"
            + $"&$top={take}"
            + "&$orderby=DownloadCount%20desc";

        (XDocument? feed, string? error) = await FetchFeedAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
            return error;

        var sb = new StringBuilder();
        int found = 0;

        foreach (XElement entry in feed!.Descendants(Atom + "entry"))
        {
            Package package = ReadPackage(entry);
            found++;

            sb.Append("  ")
              .Append(package.Id.PadRight(34))
              .Append(" v").Append(package.Version.PadRight(12))
              .Append(FormatDownloads(package.Downloads).PadLeft(9))
              .Append("  ").Append(Truncate(package.Authors, 28))
              .AppendLine();

            if (package.Summary.Length > 0)
                sb.Append("      ").AppendLine(Truncate(package.Summary, 96));
        }

        if (found == 0)
            return $"no module in the PowerShell Gallery matches '{query}'.";

        sb.AppendLine()
          .AppendLine("Use psgallery_info for details, or psgallery_install to install one.");

        return $"{found} result(s) for '{query}', most downloaded first:\n" + sb;
    }

    [ShellvisTool(
        "psgallery_info",
        SideEffect.ReadOnly,
        Description =
            "Show details of one PowerShell Gallery module: version, author, "
            + "description, project and licence links, publication date and "
            + "dependencies. Worth reading before installing anything.",
        PreviewParameter = "moduleName",
        Glyph = "read")]
    public async Task<string> Info(
        string moduleName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
            return "error: a module name is required.";

        string url = $"{ODataBase}/Search()"
            + "?$filter=IsLatestVersion"
            + $"&searchTerm='{Uri.EscapeDataString(moduleName)}'"
            + "&$top=5";

        (XDocument? feed, string? error) = await FetchFeedAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null)
            return error;

        Package? exact = feed!.Descendants(Atom + "entry")
            .Select(ReadPackage)
            .FirstOrDefault(p => p.Id.Equals(moduleName, StringComparison.OrdinalIgnoreCase));

        if (exact is null)
            return $"no module named exactly '{moduleName}' in the gallery. Try psgallery_search.";

        var sb = new StringBuilder();
        sb.Append(exact.Id).Append("  v").AppendLine(exact.Version);
        sb.Append("  author:      ").AppendLine(exact.Authors);
        sb.Append("  downloads:   ").AppendLine(exact.Downloads.ToString("N0", CultureInfo.InvariantCulture));
        sb.Append("  published:   ").AppendLine(exact.Published);

        if (exact.ProjectUrl.Length > 0)
            sb.Append("  project:     ").AppendLine(exact.ProjectUrl);

        if (exact.LicenseUrl.Length > 0)
            sb.Append("  licence:     ").AppendLine(exact.LicenseUrl);

        if (exact.Dependencies.Length > 0)
            sb.Append("  depends on:  ").AppendLine(Truncate(exact.Dependencies, 200));

        if (exact.Description.Length > 0)
            sb.AppendLine().AppendLine(Truncate(exact.Description, 1200));

        string squat = DetectTyposquat(exact.Id, feed);
        if (squat.Length > 0)
            sb.AppendLine().Append("NOTE: ").AppendLine(squat);

        return sb.ToString();
    }

    [ShellvisTool(
        "psgallery_install",
        SideEffect.AlwaysAsk,
        Description =
            "Install a module from the PowerShell Gallery and import it, then list the "
            + "commands it makes available. Always requires confirmation. State a "
            + "version when you know which one you want.",
        PreviewParameter = "moduleName",
        Glyph = "package")]
    public async Task<string> Install(
        string moduleName,
        string? version = null,
        bool allUsers = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
            return "error: a module name is required.";

        if (allUsers)
        {
            // AllUsers writes into Program Files, which needs elevation. That belongs
            // to the broker service, which does not exist yet; claiming to support it
            // would just produce a confusing access-denied deep inside PowerShell.
            return "error: installing for all users needs administrator rights, which are "
                + "not available yet. Install for the current user instead.";
        }

        Directory.CreateDirectory(ModuleRoot);

        string versionArgument = string.IsNullOrWhiteSpace(version)
            ? string.Empty
            : $" -Version '{Escape(version)}'";

        // Install-PSResource ships with PowerShell 7.4+ and needs no NuGet provider
        // bootstrap, unlike the PowerShellGet 1.0 that is all this machine has under
        // Windows PowerShell 5.1.
        //
        // -TrustRepository is essential rather than convenient: PSGallery is registered
        // as Untrusted, so without it the cmdlet asks for confirmation on its own -- and
        // in a hosted runspace with no console that prompt can never be answered, so the
        // call would hang forever. The confirmation the user actually sees comes from
        // Shellvis's own approval gate instead.
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $target = '{{Escape(ModuleRoot)}}'
            Install-PSResource -Name '{{Escape(moduleName)}}'{{versionArgument}} `
                -Repository PSGallery -TrustRepository -Scope CurrentUser `
                -Path $target -Reinstall:$false
            $installed = Get-PSResource -Name '{{Escape(moduleName)}}' -Path $target |
                Sort-Object Version -Descending | Select-Object -First 1
            'installed {0} v{1} into {2}' -f $installed.Name, $installed.Version, $target
            """;

        ShellResult result = await host
            .RunAsync(script, TimeSpan.FromMinutes(5), cancellationToken)
            .ConfigureAwait(false);

        if (result.HadErrors)
            return result.ToToolText();

        var sb = new StringBuilder(result.ToToolText());

        // Record what was added, so it stays auditable which modules the agent put on
        // the machine and at which version.
        await AppendToLockfileAsync(moduleName, result.Output, cancellationToken).ConfigureAwait(false);

        ShellResult import = await host.RunAsync(
            $"Import-Module -Name '{Escape(moduleName)}' -ErrorAction Stop; "
            + $"Get-Command -Module '{Escape(moduleName)}' -CommandType Cmdlet,Function | "
            + "Sort-Object Name | Select-Object -First 50 | ForEach-Object { '  ' + $_.Name }",
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);

        if (import.Output.Trim().Length > 0)
        {
            sb.AppendLine().AppendLine()
              .Append("Commands now available from '").Append(moduleName).AppendLine("':")
              .Append(import.Output.TrimEnd());
        }

        return sb.ToString();
    }

    [ShellvisTool(
        "psgallery_installed",
        SideEffect.ReadOnly,
        Description =
            "List the gallery modules Shellvis has installed, with versions. Use it to "
            + "check whether a capability is already present before installing again.",
        Glyph = "package")]
    public async Task<string> Installed(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ModuleRoot))
            return "Shellvis has not installed any gallery modules yet.";

        ShellResult result = await host.RunAsync(
            $"Get-ChildItem -Path '{Escape(ModuleRoot)}' -Directory -ErrorAction SilentlyContinue | "
            + "ForEach-Object { $v = (Get-ChildItem $_.FullName -Directory | "
            + "Sort-Object Name -Descending | Select-Object -First 1).Name; "
            + "'  {0}  v{1}' -f $_.Name, $v }",
            TimeSpan.FromMinutes(1),
            cancellationToken).ConfigureAwait(false);

        string listing = result.Output.Trim();
        return listing.Length == 0
            ? "Shellvis has not installed any gallery modules yet."
            : $"Installed into {ModuleRoot}:\n{listing}";
    }

    // ------------------------------------------------------------------ internals

    private static async Task<(XDocument? Feed, string? Error)> FetchFeedAsync(
        string url, CancellationToken cancellationToken)
    {
        try
        {
            string xml = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return (XDocument.Parse(xml), null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"error: the PowerShell Gallery could not be reached: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (null, "error: the PowerShell Gallery did not respond in time.");
        }
        catch (System.Xml.XmlException ex)
        {
            return (null, $"error: the gallery returned something that is not a feed: {ex.Message}");
        }
    }

    /// <summary>
    /// Read one feed entry.
    ///
    /// The properties are TYPED in the feed (m:type="Edm.Int32" and so on). Treating
    /// them loosely is how a download count ends up rendering as "System.Xml.XmlElement",
    /// which is exactly what happened the first time this feed was queried by hand.
    /// </summary>
    private static Package ReadPackage(XElement entry)
    {
        XElement? props = entry.Element(Metadata + "properties");

        return new Package(
            Id: entry.Element(Atom + "title")?.Value ?? "(unknown)",
            Version: Property(props, "Version"),
            Authors: Property(props, "Authors"),
            Summary: Property(props, "Summary"),
            Description: Property(props, "Description"),
            ProjectUrl: Property(props, "ProjectUrl"),
            LicenseUrl: Property(props, "LicenseUrl"),
            Published: Property(props, "Published") is { Length: > 10 } p ? p[..10] : string.Empty,
            Dependencies: Property(props, "Dependencies"),
            Downloads: long.TryParse(Property(props, "DownloadCount"), out long d) ? d : 0);
    }

    private static string Property(XElement? properties, string name) =>
        properties?.Element(DataServices + name)?.Value ?? string.Empty;

    /// <summary>
    /// Warn when a name is suspiciously close to a much more popular one.
    ///
    /// Typosquatting is the documented attack on the PowerShell Gallery, and it works
    /// precisely because "Az.Acounts" looks right at a glance. Comparing against the
    /// other search hits is enough to catch it: the real module is the one with orders
    /// of magnitude more downloads.
    /// </summary>
    private static string DetectTyposquat(string name, XDocument feed)
    {
        Package? subject = feed.Descendants(Atom + "entry").Select(ReadPackage)
            .FirstOrDefault(p => p.Id.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (subject is null)
            return string.Empty;

        foreach (Package other in feed.Descendants(Atom + "entry").Select(ReadPackage))
        {
            if (other.Id.Equals(subject.Id, StringComparison.OrdinalIgnoreCase))
                continue;

            if (EditDistance(other.Id, subject.Id) > 2)
                continue;

            // An order of magnitude more downloads means the near-identical name is
            // very probably the one that was meant.
            if (other.Downloads > subject.Downloads * 10)
            {
                return $"'{subject.Id}' ({FormatDownloads(subject.Downloads)} downloads) is "
                    + $"nearly identical to '{other.Id}' ({FormatDownloads(other.Downloads)}). "
                    + "Confirm which one you actually want before installing.";
            }
        }

        return string.Empty;
    }

    /// <summary>Levenshtein distance, capped early since only small values matter here.</summary>
    private static int EditDistance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
            return 3;

        int[,] d = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++)
            d[i, 0] = i;

        for (int j = 0; j <= b.Length; j++)
            d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }

    private async Task AppendToLockfileAsync(
        string moduleName, string output, CancellationToken cancellationToken)
    {
        try
        {
            string path = Path.Combine(ModuleRoot, "shellvis-installed.log");
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}  {1}  {2}{3}",
                DateTime.Now, moduleName, output.Trim().ReplaceLineEndings(" "), Environment.NewLine);

            await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // An audit line failing to write must not fail the install that succeeded.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FormatDownloads(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:F1}M",
        >= 1_000 => $"{count / 1_000.0:F0}k",
        _ => count.ToString(CultureInfo.InvariantCulture),
    };

    private static string Truncate(string text, int max)
    {
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 3)] + "...";
    }

    /// <summary>
    /// Escape for a single-quoted PowerShell string. Doubling the quote is the entire
    /// rule there, because nothing expands inside single quotes; using double quotes
    /// would let a module name carry a subexpression.
    /// </summary>
    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record Package(
        string Id,
        string Version,
        string Authors,
        string Summary,
        string Description,
        string ProjectUrl,
        string LicenseUrl,
        string Published,
        string Dependencies,
        long Downloads);
}
