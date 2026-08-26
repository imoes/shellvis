using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shellvis.Core.Providers;

namespace Shellvis.Shell.Views;

/// <summary>
/// The provider settings page: endpoint, model, key, on one screen.
///
/// <b>One page, deliberately.</b> The first version stacked six fields in a ScrollViewer and
/// the fields below the fold were simply not seen -- reported as "I still cannot configure
/// the model", which is what a settings page you have to scroll actually means. A
/// configuration form that does not fit is a form whose lower half does not exist. So: two
/// columns, no scrolling, and the note trimmed to one line.
///
/// <b>Picking a provider comes here, not to a model list.</b> Choosing a provider is the
/// start of configuring it -- it needs an endpoint, usually a key, and a model before it can
/// answer anything. Sending that gesture to a list of models first asked the question in the
/// wrong order. The model list is still one menu item away for the case where the provider is
/// already set up and only the model changes.
/// </summary>
public sealed partial class PillWindow
{
    private async Task ConfigureProviderAsync(string? id)
    {
        if (_session is null)
            return;

        bool adding = string.IsNullOrWhiteSpace(id);

        ProviderProfile? existing = adding
            ? null
            : Agent.AgentSession.AvailableProviders()
                .FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        var idBox = Field("Id", "work-gateway", existing?.Id ?? string.Empty, enabled: adding);
        var nameBox = Field("Name", "shown in the picker", existing?.DisplayName ?? string.Empty);

        // No "/v1" in the placeholder any more. It was there because the first request
        // without it returns 404, which reads like an outage rather than like a text box
        // filled in slightly wrong -- but the honest fix was to stop needing it, not to
        // teach the user someone else's URL convention. EndpointUrl adds the scheme and the
        // version segment.
        var urlBox = Field("Endpoint", "host/path -- https and /v1 are added", existing?.BaseUrl ?? string.Empty);

        var modelBox = Field("Model", "name to use", existing?.DefaultModel ?? string.Empty);
        var envBox = Field("Key from variable", "optional, e.g. OPENAI_API_KEY", existing?.ApiKeyEnvVar ?? string.Empty);

        bool stored = existing is not null && Agent.AgentSession.HasStoredKey(existing.Id);

        var keyBox = new PasswordBox
        {
            Header = "API key",
            PlaceholderText = stored ? "stored; blank keeps it" : "optional, encrypted for this account",
        };

        // A grid rather than a StackPanel: paired fields sit side by side, which is what
        // makes six of them fit without scrolling.
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 8, Width = 460 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < 5; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Place(grid, idBox, row: 0, column: 0);
        Place(grid, nameBox, row: 0, column: 1);
        Place(grid, urlBox, row: 1, column: 0, span: 2);
        Place(grid, modelBox, row: 2, column: 0);
        Place(grid, envBox, row: 2, column: 1);
        Place(grid, keyBox, row: 3, column: 0, span: 2);

        var note = new TextBlock
        {
            // Said in the dialog rather than only in a comment: someone typing a key into a
            // box is owed a straight answer about where it goes.
            Text = "The variable wins when set. A key typed here is encrypted to this "
                + "Windows account and never written to config.yaml.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.7,
        };

        Place(grid, note, row: 4, column: 0, span: 2);

        var dialog = new ContentDialog
        {
            XamlRoot = RootHost.XamlRoot,
            Title = adding ? "Add a provider" : existing?.DisplayName ?? "Provider",
            Content = grid,
            PrimaryButtonText = "Use this",
            SecondaryButtonText = "List models",
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
            // WinUI allows exactly one ContentDialog at a time and an approval prompt may
            // hold it. Reported, because a settings page that simply does not appear looks
            // like a dead menu item.
            AddRow(GlyphWarning, "another dialog is open; close it and try again.", "model");
            return;
        }

        if (result == ContentDialogResult.None)
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
                // Null, not empty: an empty box means "keep the stored key". Passing empty
                // through would delete it, so editing an endpoint would silently drop the
                // key that made it work.
                keyBox.Password.Length > 0 ? keyBox.Password : null),
            "model",
            isAnnouncement: true);

        RefreshModelLabel();

        // "List models" saves first and then asks the endpoint what it serves, because
        // asking is only possible once the endpoint and key are in place.
        if (result == ContentDialogResult.Secondary
            && Agent.AgentSession.AvailableProviders()
                .FirstOrDefault(p => p.Id.Equals(chosenId, StringComparison.OrdinalIgnoreCase))
                is { } saved)
        {
            await ShowModelsForAsync(saved).ConfigureAwait(true);
        }
    }

    private static TextBox Field(string header, string placeholder, string text, bool enabled = true) =>
        new()
        {
            Header = header,
            PlaceholderText = placeholder,
            Text = text,
            IsEnabled = enabled,
        };

    private static void Place(Grid grid, FrameworkElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, span);
        grid.Children.Add(element);
    }
}
