using Microsoft.UI.Xaml;
using Shellvis.Core.Voice;

namespace Shellvis.Shell.Views;

/// <summary>
/// Push-to-talk dictation in the pill.
///
/// The recognised text lands in the input box; it is never submitted. That is the whole
/// difference between dictation and a voice assistant: a misheard word costs one edit
/// instead of one wrong action on the machine. Recognition is imperfect by nature, and the
/// agent here can delete files.
/// </summary>
public sealed partial class PillWindow
{
    private const string GlyphMic = "\uE720"; // U+E720
    private const string GlyphMicOff = "\uE74F"; // U+E74F

    private DictationEngine? _dictation;

    /// <summary>What the input box held before dictation started, so text is appended.</summary>
    private string _beforeDictation = string.Empty;

    private void ToggleDictation()
    {
        if (_dictation?.State == DictationState.Listening)
        {
            _dictation.Stop();
            return;
        }

        StartDictation();
    }

    private void StartDictation()
    {
        if (_dictation is null)
        {
            _dictation = new DictationEngine();

            // Every handler hops to the UI thread: the recognition engine raises its
            // events on its own audio thread, and touching XAML from there throws.
            _dictation.PartialText += text => DispatcherQueue.TryEnqueue(() => OnDictated(text));
            _dictation.Level += level => DispatcherQueue.TryEnqueue(() => OnLevel(level));

            _dictation.Finished += (text, state) =>
                DispatcherQueue.TryEnqueue(() => OnDictationFinished(text, state));
        }

        _beforeDictation = PromptBox.Text;

        // Listed once, the first time dictation is used. With three active microphones on
        // this machine, "which one is it listening to" is the first question when nothing
        // is recognised -- so the answer is in the transcript before it is asked.
        if (!_devicesListed)
        {
            _devicesListed = true;
            IReadOnlyList<string> devices = DictationEngine.InputDevices();

            if (devices.Count > 1)
            {
                AddRow(GlyphMic,
                    $"{devices.Count} recording devices: "
                    + string.Join("; ", devices.Select((d, i) => $"{i}={d}"))
                    + ". Set voice.deviceIndex in config.yaml to choose one.",
                    "voice");
            }
        }

        string? problem = _dictation.Start(DictationLanguage(), DictationDevice());

        if (problem is not null)
        {
            // Reported in the console rather than as a dialog. Dictation is a convenience;
            // interrupting the user with a modal because a microphone is busy would be out
            // of proportion.
            AddRow(GlyphWarning, "Dictation: " + problem, "voice");
            OpenConsoleIfShut();

            return;
        }

        MicButton.Content = GlyphMicOff;
        StatusText.Text = "Listening... Ctrl+Alt+D or Escape to stop.";

        AddRow(GlyphMic,
            $"Listening ({_dictation.Language}) on {_dictation.DeviceName}. "
            + "Nothing is sent anywhere; recognition runs on this machine.",
            "voice");

        OpenConsoleIfShut();
    }

    /// <summary>
    /// Which language to dictate in.
    ///
    /// The machine's own UI language rather than a fixed choice: the recognizer that is
    /// installed is almost always the one matching the Windows language, and a hard-coded
    /// locale would refuse on every machine that is not that one.
    /// </summary>
    private static string DictationLanguage() =>
        Shellvis.Core.Config.ConfigStore.Load().Config.Voice.Language is { Length: > 0 } configured
            ? configured
            : System.Globalization.CultureInfo.CurrentUICulture.Name;

    /// <summary>Which recording device to open, from the config.</summary>
    private static int DictationDevice() =>
        Shellvis.Core.Config.ConfigStore.Load().Config.Voice.DeviceIndex;

    private bool _devicesListed;

    private void OnDictated(string text)
    {
        // Appended, so a second sentence does not overwrite the first, and so text typed
        // before starting is kept.
        string separator = _beforeDictation.Length > 0
            && !_beforeDictation.EndsWith(' ')
            ? " "
            : string.Empty;

        _beforeDictation = _beforeDictation + separator + text;
        PromptBox.Text = _beforeDictation;

        // The caret follows the text, or the next spoken phrase appears to land in the
        // middle of what was already there.
        PromptBox.SelectionStart = PromptBox.Text.Length;
    }

    private void OnLevel(int level)
    {
        // A coarse meter in the status line rather than a control of its own: the pill has
        // no room for one, and what the user needs to know is only whether the microphone
        // is hearing anything at all.
        int bars = Math.Clamp(level / 12, 0, 8);

        StatusText.Text = "Listening " + new string('█', bars).PadRight(8, '░');
    }

    private void OnDictationFinished(string text, DictationState state)
    {
        MicButton.Content = GlyphMic;
        StatusText.Text = ShellvisVoice.Standby;

        switch (state)
        {
            case DictationState.Silent:
                // "Nothing was recognised" on its own was useless -- it gave the user no
                // way to tell a wrong microphone from an unparsed sentence, which are the
                // two causes and have completely different remedies. The peak level
                // separates them.
                int peak = _dictation?.PeakLevel ?? 0;
                int rejected = _dictation?.RejectedCount ?? 0;

                if (peak == 0)
                {
                    AddRow(GlyphWarning,
                        "The microphone stayed silent (peak level 0). Windows speech uses "
                        + "the DEFAULT recording device -- check which one that is under "
                        + "Settings > System > Sound, and that it is not muted.",
                        "voice");
                }
                else if (rejected > 0)
                {
                    AddRow(GlyphWarning,
                        $"Heard you (peak {peak}/100) but could not make out "
                        + $"{rejected} utterance(s). Speak a little slower, or dictate "
                        + "shorter phrases.",
                        "voice");
                }
                else
                {
                    AddRow(GlyphWarning,
                        $"Sound arrived (peak {peak}/100) but no words were recognised. "
                        + "The recognizer's language is "
                        + $"{_dictation?.Language ?? DictationLanguage()}.",
                        "voice");
                }

                break;

            case DictationState.Failed:
                AddRow(GlyphWarning, "Dictation stopped unexpectedly. Check the microphone.", "voice");
                break;

            default:
                if (text.Length > 0)
                {
                    // Said explicitly, because the text is sitting in the box and doing
                    // nothing: the user has to press Enter, and that is the point.
                    AddRow(GlyphMic, $"Heard: \"{text}\" -- edit it and press Enter to send.", "voice");
                }

                break;
        }
    }

    /// <summary>Abandon dictation and put the input box back as it was.</summary>
    private void CancelDictation()
    {
        if (_dictation?.State != DictationState.Listening)
            return;

        _dictation.Cancel();

        MicButton.Content = GlyphMic;
        StatusText.Text = ShellvisVoice.Standby;
        AddRow(GlyphWarning, "Dictation cancelled.", "voice");
    }

    private void OpenConsoleIfShut()
    {
        if (!_consoleOpen)
            ToggleConsole();
    }
}
