using System.Text;

using Shellvis.Core.Config;
using Shellvis.Core.Connectors;

namespace Shellvis.Core.Tools;

/// <summary>
/// Managing connectors from inside a conversation.
///
/// Two tools and no more, because the third one -- "download a connector from a url" -- is a
/// different question. A package brings tool descriptions that go into the system prompt and
/// paths that get called with the user's credentials; fetching one from the network is a
/// decision about trust in a source, and this application has no way to say anything true
/// about a source. Installing from a directory the user can open and read keeps the judgement
/// with the person who can make it.
/// </summary>
public sealed class ConnectorTools(ConnectorLoader loader, IConnectorConfigurator? configurator = null)
{
    [ShellvisTool(
        "connector_list",
        SideEffect.ReadOnly,
        Description =
            "List the installed connectors and whether each one is usable. A connector that "
            + "is present but not configured says which variable is missing.",
        Glyph = "plug")]
    public string List()
    {
        IReadOnlyList<ConnectorStatus> status = loader.Status;

        if (status.Count == 0)
        {
            return "no connectors are installed. A connector is a directory holding a "
                + $"connector.yaml, under {ShellvisPaths.ConnectorsDirectory}.";
        }

        var sb = new StringBuilder();
        int ready = status.Count(s => s.Ready);

        sb.Append(ready).Append(" of ").Append(status.Count).AppendLine(" connector(s) ready:");

        foreach (ConnectorStatus one in status.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            sb.Append("  ").Append(one.Ready ? "ready  " : "off    ").Append(one.Name).Append(": ").AppendLine(one.Detail);

        return sb.ToString();
    }

    [ShellvisTool(
        "connector_configure",
        SideEffect.Mutating,
        Description =
            "Open the settings dialog for a connector so the user can fill in its address, "
            + "account and password. Use this whenever a connector reports that it is not "
            + "configured, or when the user asks to set one up. "
            + "IMPORTANT: never ask the user for a password in the conversation and never "
            + "pass one to a tool -- it would be written into the transcript. This dialog "
            + "takes it from the keyboard straight into the encrypted store. The connector is "
            + "reloaded afterwards, so its tools appear without a restart.",
        PreviewParameter = "name",
        Glyph = "plug")]
    public async Task<string> Configure(string name, CancellationToken cancellationToken = default)
    {
        if (configurator is null)
        {
            // A scheduled run reaches this: there is no window to put a dialog in, and no
            // person to type into it. Saying so is better than opening nothing.
            return "a connector can only be configured with somebody at the machine; there is "
                + "no window here. Ask again from an interactive session.";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            IEnumerable<string> installed = loader.Status.Select(s => s.Name);
            string list = string.Join(", ", installed);

            return list.Length == 0
                ? "no connectors are installed."
                : $"which one? Installed: {list}.";
        }

        return await configurator.ConfigureAsync(name.Trim(), cancellationToken)
            .ConfigureAwait(false);
    }

    [ShellvisTool(
        "connector_install",
        SideEffect.AlwaysAsk,
        Description =
            "Install a connector package from a local directory holding a connector.yaml. "
            + "Copies it into the user's connectors directory and loads it. Always asks "
            + "first, naming the directory; a package carrying a credential is refused "
            + "before it is copied. Tell the user what the manifest contains before you "
            + "call this -- they are approving a file, and they should know what is in it.",
        PreviewParameter = "source",
        Glyph = "plug")]
    public string Install(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return "error: give the directory holding the connector.";

        string directory = Directory.Exists(source)
            ? source
            : Path.GetDirectoryName(source) ?? string.Empty;

        string manifestFile = Path.Combine(directory, "connector.yaml");

        if (!File.Exists(manifestFile))
            return $"error: no connector.yaml in {directory}.";

        string text;

        try
        {
            text = File.ReadAllText(manifestFile, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            return $"error: {manifestFile} could not be read: {ex.Message}";
        }

        // Checked before the copy, not after. A refused package that has already been
        // written into the connectors directory would load on the next start.
        if (ConnectorLoader.FindCredential(text) is { } pattern)
        {
            return $"refused: {manifestFile} appears to contain {pattern}. A connector names "
                + "a variable and never holds its value. Replace it with ${VARIABLE_NAME} "
                + "and store the value with the secret store or an environment variable.";
        }

        string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string target = Path.Combine(ShellvisPaths.ConnectorsDirectory, name);

        if (Directory.Exists(target))
        {
            return $"'{name}' is already installed at {target}. Remove that directory first "
                + "if you mean to replace it.";
        }

        try
        {
            Directory.CreateDirectory(target);

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(directory, file);
                string destination = Path.Combine(target, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }
        catch (IOException ex)
        {
            return $"error: could not install into {target}: {ex.Message}";
        }

        // Loaded here as well, so the answer is about this machine rather than about the
        // file: "installed, but JIRA_USER is not set" is the sentence worth having.
        ConnectorStatus status = loader.Load(Path.Combine(target, "connector.yaml"));

        return status.Ready
            ? $"installed '{status.Name}' with {status.ToolCount} tool(s). They are available now."
            : $"installed '{status.Name}' into {target}, but it is not usable yet: {status.Detail}";
    }
}
