using Shellvis.Core.Config;
using Shellvis.Core.Voice;

namespace Shellvis.Shell.Views;

/// <summary>
/// Getting a Whisper model onto the machine and into memory.
///
/// Separate from the dictation code because this is about a file, not about speech, and the
/// two have different failure modes: dictation fails per utterance and this fails once, at
/// which point every later utterance falls back.
/// </summary>
public sealed partial class PillWindow
{
    private Shellvis.Core.Voice.ITranscriber? _whisper;

    /// <summary>Set once, so a failed load is not retried on every key press.</summary>
    private bool _whisperSettled;

    /// <summary>Guard against a second download starting while the first runs.</summary>
    private bool _whisperFetching;

    /// <summary>
    /// Make a Whisper model available if one is wanted, without blocking the caller.
    ///
    /// Everything here is best effort. Whisper is the better recogniser and not a
    /// requirement: the Windows engine stays behind it, so a machine with no model, no
    /// network or no room on disk still dictates.
    /// </summary>
    private void EnsureWhisper()
    {
        if (_whisperSettled || _whisperFetching)
            return;

        ShellvisConfig config = ConfigStore.Load().Config;
        string engine = config.Voice.Engine ?? "auto";

        if (engine.Equals("sapi", StringComparison.OrdinalIgnoreCase))
        {
            _whisperSettled = true;
            return;
        }

        // A hosted service, if one is asked for by name. Never reached by "auto": sending a
        // recording off the machine is not something to arrive at by default, so it takes the
        // user naming the provider in the config.
        if (CloudTranscriber.ServiceFor(engine) is { } service)
        {
            _whisperSettled = true;
            UseCloud(service, config);

            return;
        }

        WhisperModel model = WhisperModelStore.Configured(config.Voice.WhisperModel, out string? warning);

        if (warning is not null)
            AddRow(GlyphWarning, warning, "voice");

        // Setup was asked and said no. Honoured rather than second-guessed: a 465 MB
        // download that starts anyway because the feature would be nicer with it is exactly
        // the behaviour that makes people distrust an agent.
        if (config.Voice.WhisperModel is null
            && engine.Equals("auto", StringComparison.OrdinalIgnoreCase)
            && WhisperModelStore.SetupDeclinedModel())
        {
            _whisperSettled = true;
            return;
        }

        if (WhisperModelStore.IsPresent(model))
        {
            Load(model);
            return;
        }

        _whisperFetching = true;

        AddRow(GlyphTool,
            $"Fetching the {model.Id} speech model ({model.SizeText}) to "
            + $"{WhisperModelStore.Directory}. Dictation uses the older Windows engine "
            + "until it is here. It is downloaded once and never sent anywhere.",
            "voice");

        OpenConsoleIfShut();

        _ = FetchAsync(model);
    }

    private async Task FetchAsync(WhisperModel model)
    {
        int announced = -1;

        string? problem = await WhisperModelStore.DownloadAsync(
            model,
            percent =>
            {
                // Every tenth, not every one: this crosses to the UI thread, and a hundred
                // rows for one download would bury the transcript it is reported in.
                if (percent / 10 == announced / 10)
                    return;

                announced = percent;

                DispatcherQueue.TryEnqueue(() => StatusText.Text =
                    $"Fetching the speech model... {percent}%");
            }).ConfigureAwait(true);

        _whisperFetching = false;
        StatusText.Text = ShellvisVoice.Standby;

        if (problem is not null)
        {
            // Settled, so it is not retried on every dictation. A download that restarts
            // whenever the microphone is pressed would spend the user's bandwidth over and
            // over on something already known to fail.
            _whisperSettled = true;

            AddRow(GlyphWarning,
                $"The {model.Id} speech model was not installed: {problem} Dictation "
                + "continues with the Windows engine. Press the microphone again after "
                + "fixing it, or set voice.engine: sapi to stop asking.",
                "voice");

            return;
        }

        Load(model);
    }

    /// <summary>
    /// Use a hosted recogniser, and say so plainly.
    ///
    /// The announcement is not decoration. Every earlier version of this application printed
    /// "recognition runs on this machine" at the start of every dictation, and the README
    /// promised that no cloud path existed in the code. Both are now conditional, so the
    /// condition has to be visible at the moment it applies -- once in the console when the
    /// provider is picked up, and again in the line that opens every dictation.
    /// </summary>
    private void UseCloud(CloudTranscriber.Service service, ShellvisConfig config)
    {
        // The environment wins over the stored key, the same order the model providers use:
        // someone who exported a key for their whole shell expects that to be what is used.
        string? key = Environment.GetEnvironmentVariable(
            CloudTranscriber.EnvironmentVariableFor(service));

        key ??= SecretStore.Get(CloudTranscriber.SecretNameFor(service));

        if (string.IsNullOrWhiteSpace(key))
        {
            AddRow(GlyphWarning,
                $"voice.engine is set to {service.ToString().ToLowerInvariant()} but no API key "
                + $"is stored. Set {CloudTranscriber.EnvironmentVariableFor(service)} in the "
                + "environment, or put the key in the secret store. Dictation continues locally.",
                "voice");

            // Deliberately falls back rather than refusing. The user asked for the cloud, but a
            // missing key is a setup step, not a decision to stop dictating.
            LoadLocal(config);

            return;
        }

        var cloud = new CloudTranscriber(service, key, config.Voice.AzureRegion ?? string.Empty);
        _whisper = cloud;

        AddRow(GlyphWarning,
            $"Dictation now uses {cloud.Description}. Recordings are SENT TO THAT SERVICE and "
            + "no longer stay on this machine. Set voice.engine back to auto to keep them local.",
            "voice", isAnnouncement: true);

        OpenConsoleIfShut();
    }

    /// <summary>Fall back to the local path when a cloud provider cannot be used.</summary>
    private void LoadLocal(ShellvisConfig config)
    {
        WhisperModel model = WhisperModelStore.Configured(config.Voice.WhisperModel, out _);

        if (WhisperModelStore.IsPresent(model))
            Load(model);
    }

    /// <summary>
    /// Load the model off the UI thread.
    ///
    /// Off-thread because it is slow in a way that was measured, not assumed: whisper.cpp reads
    /// the whole file and allocates its context, which for the small model is about 1.5 seconds
    /// and for the large one four. Doing that inline froze the pill on the first dictation --
    /// and worse, it broke hold-to-talk outright. A low-level keyboard hook is called on the
    /// thread that installed it, so while the UI thread was blocked Windows could not call the
    /// hook and delivered the keystroke normally: spaces leaked into the prompt box for exactly
    /// as long as the load took. One blocking call, two unrelated-looking symptoms.
    /// </summary>
    private void Load(WhisperModel model)
    {
        _whisperSettled = true;

        string path = WhisperModelStore.PathFor(model);
        var recognizer = new WhisperRecognizer();

        _ = Task.Run(() => recognizer.Load(path)).ContinueWith(
            task =>
            {
                string? problem = task.IsFaulted
                    ? task.Exception?.GetBaseException().Message
                    : task.Result;

                DispatcherQueue.TryEnqueue(() => Loaded(recognizer, model, problem));
            },
            TaskScheduler.Default);
    }

    private void Loaded(WhisperRecognizer recognizer, WhisperModel model, string? problem)
    {
        if (problem is not null)
        {
            recognizer.Dispose();

            AddRow(GlyphWarning,
                problem + " Dictation continues with the Windows engine.",
                "voice");

            return;
        }

        _whisper = recognizer;

        AddRow(GlyphSpeaker,
            $"Dictation now uses Whisper ({model.Id}) on this machine -- {model.Note}.",
            "voice", isAnnouncement: true);
    }
}
