using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Shellvis.Core.Config;
using Shellvis.Core.Connectors;

namespace Shellvis.Shell.Views;

/// <summary>
/// Configuring a connector, in a dialog.
///
/// <b>Why a dialog and not a tool argument.</b> <c>connector_configure(name, url, user,
/// password)</c> is the obvious design and it is wrong: a tool's arguments pass through the
/// model and are written into the session transcript, which this application keeps in SQLite
/// with full-text search. A password given that way would be readable off the disk long after
/// the conversation, and the model would have seen it for nothing. Here the value goes from
/// the keyboard to the DPAPI store and nowhere else. The model providers' API keys have
/// always worked this way; connectors simply had no equivalent, which is why they could not
/// be configured at all without editing the environment by hand.
///
/// <b>Why an environment variable is shown as owning the value.</b> Resolution is environment
/// first, store second -- someone who exported a variable for their whole shell expects it to
/// win. So a field whose variable is already set in the environment is disabled and says so,
/// rather than accepting a value that would be silently ignored.
/// </summary>
public sealed partial class PillWindow : IConnectorConfigurator
{
    /// <summary>
    /// Ask for what a connector needs, store it, and load the connector again.
    /// </summary>
    public Task<string> ConfigureAsync(string connector, CancellationToken cancellationToken) =>
        DispatchAsync(() => ConfigureConnectorAsync(connector));

    /// <summary>Run a UI task on the UI thread and hand back its answer.</summary>
    private Task<string> DispatchAsync(Func<Task<string>> work)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                done.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                done.TrySetResult($"the dialog could not be shown: {ex.Message}");
            }
        }))
        {
            done.TrySetResult("the window is closing; nothing was changed.");
        }

        return done.Task;
    }

    /// <summary>
    /// The installed connectors and what each one still needs, session or no session.
    ///
    /// <b>Why this does not go through the session.</b> It used to, and the report was that
    /// the connectors had disappeared: the settings menu said "still starting..." where Jira
    /// and the service desk should have been, and configuring one answered "the session is
    /// still starting; try again in a moment". On a freshly installed copy that window is wide
    /// enough to walk into -- four hundred and seventy files that Windows Defender wants to
    /// look at before anything runs -- so the first thing a new user saw was a menu that could
    /// not do the one thing it exists for.
    ///
    /// The dependency was never real. A connector's manifests are files on disk and its
    /// settings are in the DPAPI store; neither needs a model, a provider or a conversation.
    /// The loader only wanted a registry to put tools in, and one built for the purpose is
    /// enough to read what is installed. When a session does exist it is asked instead, so
    /// the state shown is the state of the session actually running.
    /// </summary>
    private IReadOnlyList<ConnectorNeeds> InstalledConnectors()
    {
        if (_session is not null)
            return _session.ConnectorNeeds();

        try
        {
            // A registry of its own, thrown away with the loader. The tools registered into
            // it are never called; what is wanted is the manifests it read on the way.
            var loader = new ConnectorLoader(new Core.Tools.ToolRegistry());
            loader.LoadAll();

            return loader.Needs();
        }
        catch (Exception)
        {
            // Reading the connectors must not be able to break the menu. An empty list is
            // reported as "none installed", which is at least the truth about this attempt.
            return [];
        }
    }

    private async Task<string> ConfigureConnectorAsync(string connector)
    {
        IReadOnlyList<ConnectorNeeds> installed = InstalledConnectors();

        ConnectorNeeds? needs = installed
            .FirstOrDefault(n => n.Name.Equals(connector, StringComparison.OrdinalIgnoreCase));

        if (needs is null)
        {
            IEnumerable<string> known = installed.Select(n => n.Name);
            string list = string.Join(", ", known);

            return $"there is no connector called '{connector}'."
                + (list.Length == 0 ? " None are installed." : $" Installed: {list}.");
        }

        if (needs.Variables.Count == 0)
        {
            return $"'{needs.Name}' needs nothing configured: it declares no address and no "
                + "credential.";
        }

        var fields = new List<SettingsField>();

        foreach (ConnectorVariable variable in needs.Variables)
        {
            string label = $"{variable.Label} ({variable.Name})";

            if (variable.FromEnvironment)
            {
                // Shown, not hidden, and not editable. A field that quietly accepted a value
                // the resolver would then ignore is worse than no field at all.
                fields.Add(new SettingsField(
                    variable.Name,
                    label,
                    Value: "set by an environment variable, which wins",
                    Enabled: false));

                continue;
            }

            bool stored = SecretStore.Has(variable.Name);

            fields.Add(variable.Secret
                ? new SettingsField(
                    variable.Name,
                    label,
                    Placeholder: stored ? "stored; blank keeps it" : "encrypted for this account",
                    Secret: true)
                : new SettingsField(
                    variable.Name,
                    label,
                    Placeholder: variable.Label == "Address" ? "https://host" : string.Empty,

                    // Read back, because an address is not a secret and being unable to see
                    // what is configured is how somebody types it in twice.
                    Value: SecretStore.Get(variable.Name) ?? string.Empty));
        }

        SettingsResult answer = await SettingsWindow.ShowAsync(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            needs.Title is { Length: > 0 } title ? title : needs.Name,
            needs.Ready
                ? "This connector already works. A value typed here replaces the stored one, "
                    + "and is encrypted to this Windows account -- never written to config.yaml."
                : needs.Detail + " Values are encrypted to this Windows account.",
            fields,
            ["Save", "Cancel"]);

        if (answer.Button != "Save")
            return $"'{needs.Name}' was left as it was.";

        var written = new List<string>();

        foreach (ConnectorVariable variable in needs.Variables)
        {
            if (!answer.Values.TryGetValue(variable.Name, out string? value)
                || string.IsNullOrWhiteSpace(value))
            {
                // A blank field means "leave it alone", never "set it to empty". Otherwise
                // opening the dialog and pressing Save would wipe a working configuration.
                continue;
            }

            SecretStore.Set(variable.Name, value.Trim());
            written.Add(variable.Name);
        }

        if (written.Count == 0)
            return $"nothing was filled in; '{needs.Name}' is unchanged.";

        // Saved either way; reloaded only if there is something to reload into.
        //
        // The values are in the DPAPI store the moment Save is pressed, which is the part
        // that matters and the part that needs no session. Without one there is nothing to
        // register the tools with yet, and the session that is starting will read the store
        // as it comes up -- so this is a statement of fact rather than a failure.
        if (_session is null)
        {
            return $"saved {string.Join(", ", written)} for '{needs.Name}'. The session is "
                + "still starting and will pick them up as it does; nothing else is needed.";
        }

        ConnectorStatus after = _session.ReloadConnector(needs.Name);

        AddRow(
            after.Ready ? GlyphTool : GlyphWarning,
            $"connector '{after.Name}': {after.Detail}",
            "connector",
            isWarning: !after.Ready);

        return after.Ready
            ? $"'{after.Name}' is configured and loaded: {after.Detail}. Its tools are "
                + "available now, without a restart."
            : $"saved {string.Join(", ", written)}, but '{after.Name}' is still not usable: "
                + after.Detail;
    }
}
