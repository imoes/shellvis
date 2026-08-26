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
    private WhisperRecognizer? _whisper;

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

    private void Load(WhisperModel model)
    {
        _whisperSettled = true;

        var recognizer = new WhisperRecognizer();
        string? problem = recognizer.Load(WhisperModelStore.PathFor(model));

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
