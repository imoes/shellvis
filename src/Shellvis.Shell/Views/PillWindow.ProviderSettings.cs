using Shellvis.Core.Config;
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

        // Pre-filled ONLY from what the config file actually holds, with the resolved value as
        // the placeholder behind it.
        //
        // This was a real defect and it wrote nonsense into a user's config. Every box used to
        // be pre-filled from the RESOLVED profile -- catalogue defaults included -- and saving
        // writes whatever is in a box. So opening the dialog and pressing Save froze every
        // inherited default into an explicit override. The catalogue's placeholder model for
        // the private llama.cpp entry is the string "laguna", which is not a model name at
        // all, and it ended up in config.yaml as `defaultModel: laguna` beside a `model.model`
        // that named the real GGUF. The dialog then showed "laguna" in a box labelled Model,
        // which is how it was noticed.
        //
        // A placeholder shows an inherited value without claiming it was set here, and the
        // existing rule -- blank means "leave the built-in alone" -- starts working as
        // written.
        ProviderSection? saved = ConfigStore.Load().Config.Providers
            .FirstOrDefault(p => p.Key.Equals(id, StringComparison.OrdinalIgnoreCase)).Value;

        bool stored = existing is not null && Agent.AgentSession.HasStoredKey(existing.Id);

        // No "/v1" in the endpoint placeholder. It was there because the first request without
        // it returns 404, which reads like an outage rather than like a text box filled in
        // slightly wrong -- but the honest fix was to stop needing it, not to teach the user
        // someone else's URL convention. EndpointUrl adds the scheme and the version segment.
        //
        // "Default model", not "Model": it is the model used when none is picked. The one in
        // use comes from the model picker and lives under model.model, and calling this box
        // Model claimed it was showing that.
        SettingsField[] fields =
        [
            new("id", "Id", "work-gateway", existing?.Id ?? string.Empty, Enabled: adding),
            new("name", "Name", existing?.DisplayName ?? "shown in the picker", saved?.Name ?? string.Empty),
            new("url", "Endpoint", existing?.BaseUrl ?? "host/path -- https and /v1 are added", saved?.BaseUrl ?? string.Empty),
            new("model", "Default model",
                existing?.DefaultModel is { Length: > 0 } inherited ? inherited : "used when none is picked",
                saved?.DefaultModel ?? string.Empty),
            new("env", "Key from variable", existing?.ApiKeyEnvVar ?? "optional, e.g. OPENAI_API_KEY", saved?.ApiKeyEnvVar ?? string.Empty),
            new("key", "API key", stored ? "stored; blank keeps it" : "optional, encrypted for this account", Secret: true),
        ];

        SettingsResult answer = await SettingsWindow.ShowAsync(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            adding ? "Add a provider" : existing?.DisplayName ?? "Provider",

            // Said in the form rather than only in a comment: someone typing a key into a box
            // is owed a straight answer about where it goes.
            "The variable wins when set. A key typed here is encrypted to this Windows "
                + "account and never written to config.yaml. A blank box keeps what is there.",
            fields,
            ["Use this", "List models", "Cancel"]);

        if (answer.Button is null or "Cancel")
            return;

        string Value(string key) => answer.Values.TryGetValue(key, out string? v) ? v : string.Empty;

        string chosenId = adding ? Value("id").Trim() : existing!.Id;

        if (chosenId.Length == 0)
        {
            AddRow(GlyphWarning, "a provider needs an id.", "model");
            return;
        }

        AddRow(
            GlyphSpeaker,
            _session.ConfigureProvider(
                chosenId,
                Value("name"),
                Value("url"),
                Value("model"),
                Value("env"),
                // Null, not empty: an empty box means "keep the stored key". Passing empty
                // through would delete it, so editing an endpoint would silently drop the
                // key that made it work.
                Value("key").Length > 0 ? Value("key") : null),
            "model",
            isAnnouncement: true);

        RefreshModelLabel();

        // "List models" saves first and then asks the endpoint what it serves, because
        // asking is only possible once the endpoint and key are in place.
        if (answer.Button == "List models"
            && Agent.AgentSession.AvailableProviders()
                .FirstOrDefault(p => p.Id.Equals(chosenId, StringComparison.OrdinalIgnoreCase))
                is { } configured)
        {
            await ShowModelsForAsync(configured).ConfigureAwait(true);
        }
    }

}
