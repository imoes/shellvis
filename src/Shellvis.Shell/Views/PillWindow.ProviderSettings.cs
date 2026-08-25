using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shellvis.Core.Providers;

namespace Shellvis.Shell.Views;

/// <summary>
/// The provider settings dialog: base URL, default model, key.
///
/// Picking a provider and configuring one are different acts and this is the second. The
/// picker answers "which of these do I want", and until now that was all there was -- which
/// left every provider stuck with the endpoint and key variable compiled into the catalog.
/// A company gateway in front of OpenAI, a second llama.cpp on another port, a colleague's
/// vLLM: all configuration, none of it worth a build.
///
/// The same dialog adds a provider that is not listed at all, because the fields are
/// identical and a separate "new provider" form would differ only in which of them start
/// empty.
/// </summary>
public sealed partial class PillWindow
{
    /// <summary>
    /// Show the dialog for an existing provider, or with <paramref name="id"/> null to add
    /// one.
    /// </summary>
    private async Task ConfigureProviderAsync(string? id)
    {
        if (_session is null)
            return;

        bool adding = string.IsNullOrWhiteSpace(id);

        ProviderProfile? existing = adding
            ? null
            : Agent.AgentSession.AvailableProviders()
                .FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        var idBox = new TextBox
        {
            Header = "Id",
            PlaceholderText = "for example: work-gateway",
            Text = existing?.Id ?? string.Empty,
            IsEnabled = adding,
        };

        var nameBox = new TextBox
        {
            Header = "Name",
            PlaceholderText = "shown in the picker",
            Text = existing?.DisplayName ?? string.Empty,
        };

        var urlBox = new TextBox
        {
            Header = "Base URL",
            // The /v1 is spelled out because leaving it off is the mistake everyone makes,
            // and it produces a 404 on the first request that reads like an outage.
            PlaceholderText = "https://host/v1  (include the version path)",
            Text = existing?.BaseUrl ?? string.Empty,
        };

        var modelBox = new TextBox
        {
            Header = "Default model",
            PlaceholderText = "used when none is picked",
            Text = existing?.DefaultModel ?? string.Empty,
        };

        var envBox = new TextBox
        {
            Header = "API key environment variable",
            PlaceholderText = "optional, for example OPENAI_API_KEY",
            Text = existing?.ApiKeyEnvVar ?? string.Empty,
        };

        bool stored = existing is not null && Agent.AgentSession.HasStoredKey(existing.Id);

        var keyBox = new PasswordBox
        {
            Header = "API key",
            PlaceholderText = stored
                ? "a key is stored; leave blank to keep it"
                : "optional, stored encrypted for this Windows account",
        };

        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(idBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);
        panel.Children.Add(modelBox);
        panel.Children.Add(envBox);
        panel.Children.Add(keyBox);

        panel.Children.Add(new TextBlock
        {
            // Said in the dialog, not only in a comment: a user typing a key into a box
            // is owed a straight answer about where it goes.
            Text = "The environment variable wins if it is set. A key typed here is "
                + "encrypted to this Windows account under .shellvis\\secrets and is never "
                + "written to config.yaml.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.75,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = RootHost.XamlRoot,
            Title = adding ? "Add a provider" : $"Settings for {existing?.DisplayName}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result;

        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // WinUI allows exactly one ContentDialog at a time, and an approval prompt may
            // hold it. Reported rather than swallowed, because a settings dialog that
            // simply does not appear looks like a dead menu item.
            AddRow(GlyphWarning, "another dialog is open; close it and try again.", "model");
            return;
        }

        if (result != ContentDialogResult.Primary)
            return;

        string chosenId = adding ? idBox.Text.Trim() : existing!.Id;

        if (chosenId.Length == 0)
        {
            AddRow(GlyphWarning, "a provider needs an id.", "model");
            return;
        }

        AddRow(
            GlyphSpeaker,
            _session.ConfigureProvider(
                chosenId,
                nameBox.Text,
                urlBox.Text,
                modelBox.Text,
                envBox.Text,
                // Null, not empty: an empty box means "keep the stored key", and passing
                // empty through would delete it. Only a typed value changes anything.
                keyBox.Password.Length > 0 ? keyBox.Password : null),
            "model",
            isAnnouncement: true);

        RefreshModelLabel();
    }
}
