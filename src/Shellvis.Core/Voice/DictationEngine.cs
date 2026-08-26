using System.Globalization;
using System.Speech.Recognition;

namespace Shellvis.Core.Voice;

/// <summary>What the dictation engine is doing.</summary>
public enum DictationState
{
    Idle,

    Listening,

    /// <summary>Stopped because nothing usable was heard.</summary>
    Silent,

    Failed,
}

/// <summary>
/// Push-to-talk dictation using the recognizer already installed on the machine.
///
/// <b>Local, with no cloud path at all.</b> That was a requirement, not a preference, so
/// the choice of API follows from it: <c>System.Speech</c> drives the in-box SAPI
/// recognizer, which is provably on-device. The WinRT <c>SpeechRecognizer</c> would give
/// the newer DNN engine and better accuracy, but its free-form dictation has historically
/// depended on the "Online speech recognition" privacy setting -- and an agent that
/// quietly sends the user's voice to a service because a toggle was on is exactly what
/// this had to avoid.
///
/// A deliberate deviation from the plan: no NAudio. <see cref="SpeechRecognitionEngine"/>
/// opens the capture device itself and reports levels through
/// <see cref="SpeechRecognitionEngine.AudioLevelUpdated"/>, so a separate WASAPI capture
/// stage would add a dependency, a format negotiation and a buffer copy to arrive at the
/// same place.
/// </summary>
public sealed class DictationEngine : IDisposable
{
    private readonly object _gate = new();

    private SpeechRecognitionEngine? _engine;
    private CultureInfo? _culture;
    private bool _disposed;

    /// <summary>Text recognised so far in this session.</summary>
    private readonly List<string> _phrases = [];

    /// <summary>Raised as phrases arrive, so the caller can show progress.</summary>
    public event Action<string>? PartialText;

    /// <summary>Raised with the microphone level, 0-100, for the recording indicator.</summary>
    public event Action<int>? Level;

    /// <summary>Raised when listening ends, with the full text and why it stopped.</summary>
    public event Action<string, DictationState>? Finished;

    public DictationState State { get; private set; } = DictationState.Idle;

    /// <summary>
    /// The recognizers Windows has installed, with their languages.
    ///
    /// Worth surfacing rather than hiding: recognition is per language, and on this
    /// machine only German is installed. A user dictating English would otherwise get
    /// confident nonsense rather than a message.
    /// </summary>
    public static IReadOnlyList<string> InstalledRecognizers()
    {
        try
        {
            LastError = null;

            return [.. SpeechRecognitionEngine.InstalledRecognizers()
                .Select(r => $"{r.Culture.Name}  {r.Description}")];
        }
        catch (Exception ex)
        {
            // Recorded, not swallowed. An earlier revision returned an empty list here and
            // the symptom was "no recognizer is installed" on a machine that had one --
            // the real cause was an assembly load failure this catch was hiding. A
            // catch-all that discards the reason turns a diagnosable fault into a mystery,
            // which is a mistake this project has now made three times.
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return [];
        }
    }

    /// <summary>Why enumeration failed, when it did.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Whether dictation can work at all on this machine.</summary>
    public static bool IsAvailable => InstalledRecognizers().Count > 0;

    /// <summary>
    /// Pick a recognizer for a language, or explain what is available instead.
    /// </summary>
    /// <param name="language">
    /// A culture name such as de-DE, or null for the machine's own UI language.
    /// </param>
    private static RecognizerInfo? Choose(string? language, out string? problem)
    {
        problem = null;

        List<RecognizerInfo> installed;

        try
        {
            installed = [.. SpeechRecognitionEngine.InstalledRecognizers()];
        }
        catch (Exception ex)
        {
            problem = $"the Windows speech stack is not usable: {ex.Message}";
            return null;
        }

        if (installed.Count == 0)
        {
            problem = "no speech recognizer is installed. Add a speech language under "
                + "Settings > Time & language > Speech.";

            return null;
        }

        string wanted = language is { Length: > 0 }
            ? language
            : CultureInfo.CurrentUICulture.Name;

        // Exact match first, then the language without the region: a de-AT request should
        // be served by a de-DE recognizer rather than refused.
        RecognizerInfo? match =
            installed.FirstOrDefault(r => r.Culture.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(r =>
                r.Culture.TwoLetterISOLanguageName.Equals(
                    wanted.Split('-')[0], StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            return match;

        // Falling back to a recognizer for a DIFFERENT language would produce confident
        // nonsense, which is worse than refusing: the user would see plausible words that
        // are not what they said.
        problem = $"no recognizer for '{wanted}'. Installed: "
            + string.Join(", ", installed.Select(r => r.Culture.Name))
            + ". Add the language under Settings > Time & language > Speech.";

        return null;
    }

    /// <summary>
    /// The recording devices Windows offers, in the order their index refers to.
    ///
    /// Exposed so the user can pick one. Without a list, "choose device 2" is a guess.
    /// </summary>
    public static IReadOnlyList<string> InputDevices()
    {
        try
        {
            var names = new List<string>();

            for (int i = 0; i < NAudio.Wave.WaveInEvent.DeviceCount; i++)
                names.Add(NAudio.Wave.WaveInEvent.GetCapabilities(i).ProductName);

            return names;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Which device the current session opened, for the transcript.</summary>
    public string? DeviceName { get; private set; }

    private NAudio.Wave.WaveInEvent? _waveIn;

    /// <summary>Automatic level correction, reset with each capture session.</summary>
    private CaptureGain _gain = new();
    private BlockingAudioStream? _capture;

    /// <summary>
    /// Open a specific recording device and pipe it into a blocking stream.
    /// </summary>
    private string? StartCapture(int deviceIndex)
    {
        try
        {
            if (deviceIndex >= NAudio.Wave.WaveInEvent.DeviceCount)
            {
                IReadOnlyList<string> devices = InputDevices();

                return $"there is no recording device {deviceIndex}. Available: "
                    + (devices.Count == 0
                        ? "none"
                        : string.Join("; ", devices.Select((d, i) => $"{i}={d}")));
            }

            _capture = new BlockingAudioStream();

            // A fresh one per session: the gain it settled on for the last microphone at
            // the last distance is not a useful starting point for this one.
            _gain = new CaptureGain();

            _waveIn = new NAudio.Wave.WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new NAudio.Wave.WaveFormat(16000, 16, 1),
                // 100 ms buffers: short enough that the level meter reacts as the user
                // speaks, long enough not to wake the callback constantly.
                BufferMilliseconds = 100,
            };

            _waveIn.DataAvailable += (_, e) =>
            {
                // Amplified in place FIRST, so the recogniser gets the boosted samples and
                // the meter reports what it actually hears rather than what the device
                // delivered. A microphone that reads 42 out of 100 is not broken, but it is
                // quiet enough that recognition suffers, and the device level belongs to the
                // user and to every other application sharing it.
                OnLevel(this, _gain.Apply(e.Buffer, e.BytesRecorded));

                _capture?.Push(e.Buffer.AsSpan(0, e.BytesRecorded));

                // The level is computed here rather than taken from the engine: with a
                // stream input, SpeechRecognitionEngine reports no audio level at all,
                // so the meter and the "was the microphone silent" diagnosis would both
                // be blind.

            };

            _waveIn.RecordingStopped += (_, _) => _capture?.Finish();
            _waveIn.StartRecording();

            return null;
        }
        catch (Exception ex)
        {
            return $"could not open recording device {deviceIndex}: {ex.Message}";
        }
    }



    private void OnLevel(object? sender, int level)
    {
        if (level > PeakLevel)
            PeakLevel = level;

        Level?.Invoke(level);
    }

    /// <summary>
    /// Start listening. Returns null on success, or the reason it could not start.
    /// </summary>
    /// <param name="deviceIndex">
    /// Recording device to open, or -1 for the Windows default.
    /// </param>
    public string? Start(string? language = null, int deviceIndex = -1)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (State == DictationState.Listening)
                return "already listening.";

            RecognizerInfo? recognizer = Choose(language, out string? problem);

            if (recognizer is null)
                return problem;

            try
            {
                Stop(quiet: true);

                _engine = new SpeechRecognitionEngine(recognizer);
                _culture = recognizer.Culture;

                // A free-form dictation grammar rather than a word list: the point is to
                // capture whatever the user says to the agent, which cannot be enumerated.
                _engine.LoadGrammar(new DictationGrammar());

                // Either the Windows default, or a named device.
                //
                // The default is what System.Speech can do on its own, and it is right
                // when there is one microphone. This machine has three active ones, and a
                // default pointing at a laptop mic array while the user speaks into a
                // headset produces exactly the reported symptom: listening starts,
                // nothing is recognised, and nothing says why. So a device can be chosen,
                // which needs a capture stage of our own.
                if (deviceIndex >= 0)
                {
                    string? captureProblem = StartCapture(deviceIndex);

                    if (captureProblem is not null)
                    {
                        State = DictationState.Failed;
                        Stop(quiet: true);
                        return captureProblem;
                    }

                    // 16 kHz mono 16-bit: what the desktop recognizer is trained on.
                    // Feeding it 44.1 kHz stereo works but recognises measurably worse,
                    // and the resampling would happen inside the engine anyway.
                    _engine.SetInputToAudioStream(
                        _capture!,
                        new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                            16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                            System.Speech.AudioFormat.AudioChannel.Mono));

                    DeviceName = InputDevices().ElementAtOrDefault(deviceIndex) ?? $"device {deviceIndex}";
                }
                else
                {
                    _engine.SetInputToDefaultAudioDevice();
                    DeviceName = "Windows default recording device";
                }

                _engine.SpeechRecognized += OnRecognized;
                _engine.AudioLevelUpdated += OnLevel;
                _engine.RecognizeCompleted += OnCompleted;

                // The missing handler, and the reason dictation reported "nothing was
                // recognised" after someone had clearly spoken. SpeechRecognized fires
                // only for results the engine ACCEPTS; anything below its confidence
                // threshold raises SpeechRecognitionRejected instead and was being
                // dropped in silence. With free-form dictation, rejection is common --
                // an accent, background noise, a technical word.
                //
                // A rejected result still carries the engine's best guess, and this text
                // goes into an editable box for the user to read. A wrong word costs one
                // correction; discarding the sentence costs the thought. So the guess is
                // kept, and marked.
                _engine.SpeechRecognitionRejected += OnRejected;

                // Generous end silence: this is push-to-talk, so the USER decides when the
                // utterance is over. A short timeout would cut someone off mid-thought
                // while they worked out how to phrase a command.
                _engine.EndSilenceTimeout = TimeSpan.FromSeconds(2);
                _engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(2);
                _engine.InitialSilenceTimeout = TimeSpan.FromSeconds(10);

                _phrases.Clear();
                _rejected = 0;
                PeakLevel = 0;
                State = DictationState.Listening;

                // Multiple, not Single: a dictated instruction is usually several
                // sentences, and Single would stop after the first.
                _engine.RecognizeAsync(RecognizeMode.Multiple);

                return null;
            }
            catch (InvalidOperationException ex)
            {
                // The characteristic failure: no capture device, or one held exclusively
                // by another application.
                State = DictationState.Failed;
                Stop(quiet: true);

                return $"could not open the microphone: {ex.Message}";
            }
            catch (Exception ex)
            {
                State = DictationState.Failed;
                Stop(quiet: true);

                return $"dictation could not start: {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Stop listening and report what was heard.
    /// </summary>
    /// <param name="quiet">Tear down without raising <see cref="Finished"/>.</param>
    public string Stop(bool quiet = false)
    {
        SpeechRecognitionEngine? engine;
        string text;
        DictationState outcome;

        lock (_gate)
        {
            engine = _engine;
            _engine = null;

            text = string.Join(" ", _phrases).Trim();

            outcome = State switch
            {
                DictationState.Failed => DictationState.Failed,
                _ when text.Length == 0 => DictationState.Silent,
                _ => DictationState.Idle,
            };

            if (State != DictationState.Failed)
                State = DictationState.Idle;
        }

        if (engine is not null)
        {
            engine.SpeechRecognized -= OnRecognized;
            engine.AudioLevelUpdated -= OnLevel;
            engine.RecognizeCompleted -= OnCompleted;
            engine.SpeechRecognitionRejected -= OnRejected;

            // Capture is torn down BEFORE the engine, so the engine's reader thread sees
            // end-of-stream and returns instead of blocking on a device that is gone.
            StopCapture();

            try
            {
                // Cancel, not Stop: Stop waits for the current utterance to finish, which
                // means the microphone stays open after the user has already released.
                engine.RecognizeAsyncCancel();
            }
            catch (Exception)
            {
            }

            try
            {
                engine.Dispose();
            }
            catch (Exception)
            {
                // The engine can throw while tearing down its audio device; that must not
                // propagate into a UI event handler.
            }
        }

        if (!quiet)
            Finished?.Invoke(text, outcome);

        return text;
    }

    /// <summary>Discard whatever was heard, for an explicit cancel.</summary>
    public void Cancel()
    {
        lock (_gate)
            _phrases.Clear();

        Stop(quiet: true);

        lock (_gate)
            State = DictationState.Idle;
    }

    private void StopCapture()
    {
        NAudio.Wave.WaveInEvent? device = _waveIn;
        _waveIn = null;

        if (device is not null)
        {
            try
            {
                device.StopRecording();
            }
            catch (Exception)
            {
            }

            device.Dispose();
        }

        BlockingAudioStream? stream = _capture;
        _capture = null;

        // Finish rather than Dispose first: a reader blocked inside Read has to be
        // released before the stream goes away, or it waits out its full timeout.
        stream?.Finish();
        stream?.Dispose();
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        string text = e.Result?.Text ?? string.Empty;

        if (text.Length == 0)
            return;

        // A low-confidence result is kept rather than dropped. The text goes into the
        // input box for the user to read and correct, not straight to the model, so a
        // wrong word costs an edit -- whereas dropping a whole sentence costs the thought.
        lock (_gate)
            _phrases.Add(text);

        PartialText?.Invoke(text);
    }

    /// <summary>
    /// Keep the engine's best guess even when it rejects it.
    ///
    /// Marked with a trailing "(?)" so the user can see which part the engine was unsure
    /// about rather than trusting it silently.
    /// </summary>
    private void OnRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        Interlocked.Increment(ref _rejected);

        string? guess = e.Result?.Alternates is { Count: > 0 } alternates
            ? alternates.OrderByDescending(a => a.Confidence).First().Text
            : e.Result?.Text;

        if (string.IsNullOrWhiteSpace(guess))
            return;

        lock (_gate)
            _phrases.Add(guess.Trim() + " (?)");

        PartialText?.Invoke(guess.Trim());
    }

    private int _rejected;

    /// <summary>
    /// The loudest level seen this session.
    ///
    /// Reported when nothing was recognised, because it separates the two very different
    /// causes: a peak of zero means the wrong capture device was opened -- this machine
    /// has five microphones and System.Speech can only ever use the Windows default --
    /// while a healthy peak means the engine heard sound and could not parse it.
    /// </summary>
    public int PeakLevel { get; private set; }

    /// <summary>The multiplier the capture gain settled on, for the diagnostic line.</summary>
    public double Gain => _gain.Current;

    /// <summary>Whether it had to amplify noticeably, which points at the device level.</summary>
    public bool IsBoosting => _gain.IsBoosting;

    /// <summary>How many utterances the engine heard but rejected.</summary>
    public int RejectedCount => Volatile.Read(ref _rejected);

    private void OnLevel(object? sender, AudioLevelUpdatedEventArgs e)
    {
        if (e.AudioLevel > PeakLevel)
            PeakLevel = e.AudioLevel;

        Level?.Invoke(e.AudioLevel);
    }

    private void OnCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        // Reached when the engine stops on its own -- the initial silence timeout, or an
        // audio device disappearing. The caller has to be told, or the pill sits showing a
        // recording indicator over a microphone that is no longer listening.
        if (State != DictationState.Listening)
            return;

        if (e.Error is not null)
        {
            lock (_gate)
                State = DictationState.Failed;
        }

        Stop();
    }

    /// <summary>The language actually in use, once started.</summary>
    public string? Language => _culture?.Name;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop(quiet: true);
    }
}
