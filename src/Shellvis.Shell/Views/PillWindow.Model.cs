using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Shellvis.Core.Providers;

namespace Shellvis.Shell.Views;

/// <summary>
/// Choosing the provider and the model.
///
/// Two stages rather than one flat list, and the stages are not symmetrical. Providers
/// are known up front -- they are a table in the catalog. Models are not: names change
/// weekly, and a local llama.cpp or Ollama serves whatever the user happens to have
/// pulled. So the provider list is built from data and the model list is ASKED FOR, once
/// a provider has been chosen. Fetching all nineteen providers' models to populate one
/// menu would mean nineteen network calls for a menu the user will click once.
/// </summary>
public sealed partial class PillWindow
{
    private void ShowModelMenu()
    {
        if (_session is null)
            return;

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

        // The current provider first and marked, so the menu answers "what am I on?"
        // before it asks "what do you want?".
        string currentId = _session.Provider.Id;

        // From the resolver, not the catalog: an entry the config overrides shows its
        // overridden name and base URL, and one defined only in the config appears at all.
        foreach (ProviderProfile profile in Agent.AgentSession.AvailableProviders())
        {
            var item = new MenuFlyoutItem
            {
                Text = profile.Id == currentId
                    ? $"{profile.DisplayName}  (current)"
                    : profile.DisplayName,
                Tag = profile,
            };

            // The second parameter is named rather than discarded with _, because a lambda
            // parameter called _ shadows the discard: "_ = Foo()" then assigns to the
            // event args instead of discarding the task, which the compiler rejects with
            // a message about RoutedEventArgs that says nothing about the real cause.
            item.Click += (sender, args) =>
            {
                if (sender is MenuFlyoutItem { Tag: ProviderProfile chosen })
                {
                    // Not awaited: the flyout must close now, and the second stage opens
                    // its own menu when the listing comes back.
                    _ = ShowModelsForAsync(chosen);
                }
            };

            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Settings for the provider in use, and a way to add one that is not listed. Both
        // at the bottom, because picking is the common act and configuring is the rare one.
        var configure = new MenuFlyoutItem { Text = $"Settings for {_session.Provider.DisplayName}..." };
        configure.Click += (_, args) => _ = ConfigureProviderAsync(_session.Provider.Id);
        flyout.Items.Add(configure);

        var add = new MenuFlyoutItem { Text = "Add a provider..." };
        add.Click += (_, args) => _ = ConfigureProviderAsync(null);
        flyout.Items.Add(add);

        flyout.ShowAt(ModelButton);
    }

    /// <summary>
    /// Second stage: what this provider will serve.
    ///
    /// The listing is announced in the transcript when it comes back short or empty. A
    /// menu that silently offers only a default would leave the user thinking the
    /// provider has one model, when the truth is that the endpoint could not be reached
    /// or refused the key.
    /// </summary>
    private async Task ShowModelsForAsync(ProviderProfile profile)
    {
        if (_session is null)
            return;

        AddRow(GlyphTool, $"asking {profile.DisplayName} which models it serves...", "model");

        ModelListing listing = await ModelDirectory.ListAsync(profile).ConfigureAwait(true);

        if (listing.Note is { Length: > 0 } note)
            AddRow(GlyphWarning, $"{profile.DisplayName}: {note}", "model");

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };

        // Always offered, and always first: the catalog's default is the one name known
        // to be right for this provider even when the endpoint would not talk.
        var fallback = new MenuFlyoutItem
        {
            Text = $"default  ({profile.DefaultModel})",
            Tag = profile.DefaultModel,
        };

        fallback.Click += (sender, _) => Apply(profile, (sender as MenuFlyoutItem)?.Tag as string);
        flyout.Items.Add(fallback);

        if (listing.Models.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());

            foreach (string model in listing.Models)
            {
                // The default is already at the top; listing it twice makes the menu look
                // like it has duplicates.
                if (string.Equals(model, profile.DefaultModel, StringComparison.OrdinalIgnoreCase))
                    continue;

                var item = new MenuFlyoutItem { Text = model, Tag = model };
                item.Click += (sender, _) => Apply(profile, (sender as MenuFlyoutItem)?.Tag as string);
                flyout.Items.Add(item);
            }
        }

        flyout.ShowAt(ModelButton);
    }

    private void Apply(ProviderProfile profile, string? model)
    {
        if (_session is null)
            return;

        // The result is a sentence either way: a switch that failed for want of an API key
        // has to say so, because the alternative is a label that changed while the
        // requests kept going to the old endpoint.
        AddRow(GlyphSpeaker, _session.SetModel(profile, model), "model", isAnnouncement: true);

        RefreshModelLabel();
    }

    /// <summary>
    /// Put the provider and model on the header button.
    ///
    /// Shortened from the front, not the back: model names are distinguished by their
    /// tail ("...-instruct", "...-70b"), so trimming the end removes exactly the part
    /// that tells two of them apart.
    /// </summary>
    private void RefreshModelLabel()
    {
        if (_session is null)
            return;

        string model = _session.ModelName;

        if (model.Length > 22)
            model = "..." + model[^19..];

        ModelButton.Content = $"{_session.Provider.Id} / {model}";
    }
}
