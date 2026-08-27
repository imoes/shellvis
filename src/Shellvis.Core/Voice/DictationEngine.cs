using System.Globalization;
using System.Speech.Recognition;

namespace Shellvis.Core.Voice;

/// <summary>What the dictation engine is doing.</summary>
public enum DictationState
{
    Idle,

    Listening,

    /// <summary>
    /// The microphone is closed and a Whisper model is working on the recording.
    ///
    /// A state of its own because it is the one moment the user waits with nothing to look
    /// at: the recording indicator is gone and the text has not arrived. Batch recognition
    /// buys the quality, and the price is this gap, so it has to be visible.
    /// </summary>
    Transcribing,

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

    /// <summary>
    /// Whether audio is flowing through this project's own capture stage.
    ///
    /// Exposed because the alternative -- letting the engine open the device itself -- is
    /// silently a different feature: no capture gain, and none of the buffer-filling the
    /// recognizer needs. That difference was invisible for a whole release, and the
    /// harnesses stayed green because they exercised the stage directly instead of asking
    /// whether a real Start actually used it. It is a fact about the wire, so it is
    /// readable rather than inferred.
    /// </summary>
    public bool UsingCaptureStage => _capture is not null;

    private NAudio.Wave.WaveInEvent? _waveIn;

    /// <summary>Automatic level correction, reset with each capture session.</summary>
    private CaptureGain _gain = new();
    private BlockingAudioStream? _capture;

    /// <summary>The Whisper model in use this session, or null for the Windows engine.</summary>
    private ITranscriber? _whisper;

    /// <summary>
    /// The utterance being collected for Whisper.
    ///
    /// Whisper needs the whole recording before it starts, so unlike the Windows engine --
    /// which consumes the stream as it arrives -- the audio has to be held. Held in memory
    /// rather than in a file: at 32 kB per second a long dictation is a few megabytes, and
    /// writing a recording of someone's voice to disk is a decision that should be asked
    /// for rather than made as an implementation detail.
    /// </summary>
    private MemoryStream? _utterance;

    /// <summary>
    /// How much audio is kept, at 16 kHz 16-bit mono: three minutes.
    ///
    /// A bound rather than trust, because the microphone stays open until the user closes
    /// it and a forgotten dictation would otherwise grow without limit. Three minutes is
    /// far past any dictated instruction and still only about 5 MB.
    /// </summary>
    private const int UtteranceLimit = 16000 * 2 * 180;

    /// <summary>Whether the recording was cut short by that limit.</summary>
    private bool _utteranceFull;

    /// <summary>Why the last session produced nothing, when the cause was not silence.</summary>
    public string? LastProblem { get; private set; }

    /// <summary>
    /// Open a recording device and pipe it into a blocking stream.
    /// </summary>
    /// <param name="deviceIndex">
    /// A device index, or -1 for the Windows default. -1 is passed straight through as
    /// WAVE_MAPPER, which is how the default is addressed without giving up this capture
    /// stage -- and this stage is where the gain and the buffer-filling bridge live.
    /// </param>
    private string? StartCapture(int deviceIndex)
    {
        try
        {
            if (NAudio.Wave.WaveInEvent.DeviceCount == 0)
                return "Windows reports no recording device at all";

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
                // Negative values all mean WAVE_MAPPER; normalised so an accidental -2 from
                // a hand-edited config does not reach waveInOpen as a different request.
                DeviceNumber = deviceIndex < 0 ? -1 : deviceIndex,
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

                // Collected for Whisper, which cannot start until the utterance ends. Both
                // sinks are fed from the same amplified buffer rather than one path being
                // "the real one": the whole reason the default device was quiet for a
                // release was a second path that skipped this stage.
                MemoryStream? held = _utterance;

                if (held is not null)
                {
                    lock (held)
                    {
                        if (held.Length + e.BytesRecorded <= UtteranceLimit)
                        {
                            held.Write(e.Buffer, 0, e.BytesRecorded);
                        }
                        else
                        {
                            // Stated, not silently dropped: a transcript that ends mid
                            // sentence with no explanation reads as a recognition failure.
                            _utteranceFull = true;
                        }
                    }
                }

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
            return deviceIndex < 0
                ? $"could not open the default recording device: {ex.Message}"
                : $"could not open recording device {deviceIndex}: {ex.Message}";
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
    /// <param name="whisper">
    /// A loaded Whisper model to recognise with, or null to use the Windows engine. When one
    /// is supplied the Windows recognizer is not involved at all -- which also means
    /// dictation works on a machine that has no Windows speech language installed, the case
    /// the old path had to refuse outright.
    /// </param>
    public string? Start(string? language = null, int deviceIndex = -1, ITranscriber? whisper = null)
    {
        if (whisper is { IsLoaded: true })
            return StartWithWhisper(language, deviceIndex, whisper);

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
                //
                // And the DEFAULT goes through that same stage. This used to branch: a
                // chosen device was captured by us, while -1 went to
                // SetInputToDefaultAudioDevice -- which made the default, the state every
                // machine is in until someone edits the config, the one path with no
                // capture gain and none of the audio-bridge fixes. The reported symptom was
                // exactly that shape: "peak 34/100 but no words were recognised", in a
                // session whose own transcript said "Windows default recording device".
                //
                // NAudio addresses the default as WAVE_MAPPER (-1), so honouring the user's
                // Windows choice and keeping our own capture stage were never in conflict.
                string? captureProblem = StartCapture(deviceIndex);

                if (captureProblem is not null)
                {
                    // A device the user NAMED must fail loudly: silently listening to a
                    // different microphone than the one they configured is the defect this
                    // whole path exists to prevent.
                    if (deviceIndex >= 0)
                    {
                        State = DictationState.Failed;
                        Stop(quiet: true);
                        return captureProblem;
                    }

                    // For the default, fall back to the engine's own device handling. It
                    // dictates without the gain, which is worse -- but refusing outright
                    // would turn a degraded feature into a missing one. The device name
                    // carries the reason so the transcript says which one is in use.
                    StopCapture();

                    _engine.SetInputToDefaultAudioDevice();
                    DeviceName = $"the Windows default device, without gain ({captureProblem})";
                }
                else
                {
                    // 16 kHz mono 16-bit: what the desktop recognizer is trained on.
                    // Feeding it 44.1 kHz stereo works but recognises measurably worse,
                    // and the resampling would happen inside the engine anyway.
                    _engine.SetInputToAudioStream(
                        _capture!,
                        new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                            16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen,
                            System.Speech.AudioFormat.AudioChannel.Mono));

                    DeviceName = deviceIndex >= 0
                        ? InputDevices().ElementAtOrDefault(deviceIndex) ?? $"device {deviceIndex}"
                        : "the Windows default recording device";
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
    /// Start listening with a Whisper model instead of the Windows engine.
    ///
    /// Nothing is recognised while this runs -- the microphone is recorded, and the whole
    /// recording is transcribed on <see cref="Stop"/>. That is the shape of the model, and
    /// for push-to-talk it is the better one: the model reads the end of the sentence
    /// before deciding what its beginning was, which is precisely what the Windows engine
    /// could not do and why it turned "Welche Termine liegen diese Woche an" into
    /// something else.
    /// </summary>
    private string? StartWithWhisper(string? language, int deviceIndex, ITranscriber whisper)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (State == DictationState.Listening)
                return "already listening.";

            if (State == DictationState.Transcribing)
                return "still transcribing the last recording.";

            Stop(quiet: true);

            _utterance = new MemoryStream();
            _utteranceFull = false;
            _whisper = whisper;
            LastProblem = null;

            string? captureProblem = StartCapture(deviceIndex);

            if (captureProblem is not null)
            {
                // No fallback to the Windows engine here, unlike the default-device case.
                // Falling back would silently swap the recogniser the user chose for the one
                // they chose it to get away from, and the text would come back worse with
                // nothing saying why.
                _utterance?.Dispose();
                _utterance = null;
                _whisper = null;
                State = DictationState.Failed;
                StopCapture();

                return captureProblem;
            }

            // Whisper is told the language rather than detecting it. Detection on a short
            // German utterance lands on Dutch often enough to matter, and the user's
            // configured language is a better answer than a guess from two seconds of audio.
            _culture = language is { Length: > 0 }
                ? CultureInfo.GetCultureInfo(language)
                : CultureInfo.CurrentUICulture;

            DeviceName = deviceIndex >= 0
                ? InputDevices().ElementAtOrDefault(deviceIndex) ?? $"device {deviceIndex}"
                : "the Windows default recording device";

            _phrases.Clear();
            _rejected = 0;
            PeakLevel = 0;
            State = DictationState.Listening;

            return null;
        }
    }

    /// <summary>Which recogniser this session is using, for the transcript.</summary>
    public string RecognizerName => _whisper?.Description ?? "the Windows speech recognizer";

    /// <summary>
    /// Whether this session sends the recording off the machine.
    ///
    /// Read by the UI to choose what it says. The sentence "recognition runs on this machine"
    /// was printed unconditionally before there was anything that could make it false, and a
    /// promise that is only true by accident is the kind that gets broken silently.
    /// </summary>
    public bool IsRemote => _whisper?.IsRemote == true;

    /// <summary>
    /// Stop listening and report what was heard.
    /// </summary>
    /// <param name="quiet">Tear down without raising <see cref="Finished"/>.</param>
    public string Stop(bool quiet = false)
    {
        SpeechRecognitionEngine? engine;
        ITranscriber? whisper;
        MemoryStream? utterance;
        bool wasListening;
        string text;
        DictationState outcome;

        lock (_gate)
        {
            engine = _engine;
            _engine = null;

            whisper = _whisper;
            _whisper = null;

            utterance = _utterance;
            _utterance = null;

            wasListening = State == DictationState.Listening;

            text = string.Join(" ", _phrases).Trim();

            outcome = State switch
            {
                DictationState.Failed => DictationState.Failed,
                _ when text.Length == 0 => DictationState.Silent,
                _ => DictationState.Idle,
            };

            // Whisper has not run yet at this point, so the session is not over: it moves to
            // Transcribing and reaches Idle when the model comes back. Reporting Silent here
            // would tell the user nothing was heard while the recording is still being read.
            if (whisper is not null && wasListening && !quiet)
                State = DictationState.Transcribing;
            else if (State != DictationState.Failed)
                State = DictationState.Idle;
        }

        // Unconditionally, and before the engine: with a Whisper session there is no engine
        // to hang the teardown off, and with a Windows one the engine's reader thread has to
        // see end-of-stream rather than block on a device that is gone.
        StopCapture();

        if (engine is not null)
        {
            engine.SpeechRecognized -= OnRecognized;
            engine.AudioLevelUpdated -= OnLevel;
            engine.RecognizeCompleted -= OnCompleted;
            engine.SpeechRecognitionRejected -= OnRejected;

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

        if (whisper is not null)
        {
            if (quiet || !wasListening)
            {
                // Cancelled, or torn down before anything was recorded. The recording is
                // dropped rather than transcribed: Cancel means the user does not want the
                // text, and spending two seconds of CPU to produce it anyway would be work
                // nobody asked for.
                utterance?.Dispose();

                if (!quiet)
                    Finished?.Invoke(string.Empty, DictationState.Silent);

                return string.Empty;
            }

            // Deliberately not awaited. Stop is called from a UI event handler and from the
            // hotkey path, and blocking either for the length of a transcription would
            // freeze the pill. The result arrives through the same events as before, so the
            // caller does not need to know which recogniser produced it.
            _ = TranscribeAsync(whisper, utterance);

            return string.Empty;
        }

        if (!quiet)
            Finished?.Invoke(text, outcome);

        return text;
    }

    /// <summary>
    /// Hand the recording to Whisper and report the result through the usual events.
    /// </summary>
    private async Task TranscribeAsync(ITranscriber whisper, MemoryStream? utterance)
    {
        byte[] pcm;

        if (utterance is null)
        {
            pcm = [];
        }
        else
        {
            lock (utterance)
                pcm = utterance.ToArray();

            utterance.Dispose();
        }

        // Judged on the RAW level, before the gain touched it.
        //
        // The recogniser has its own energy gate, but by the time audio reaches it the gain
        // has already lifted whatever was there towards the target level -- that is the gain's
        // whole job -- so downstream nothing can tell a spoken word from an amplified quiet
        // room. Measured here it still can: speech into a headset peaks well above this,
        // while an office at rest stays in the thousandths.
        const double SpokeAtAll = 0.02;

        WhisperResult result = _gain.LoudestRaw < SpokeAtAll
            ? new WhisperResult(string.Empty, null)
            : await whisper.TranscribeAsync(pcm, _culture?.Name).ConfigureAwait(false);

        lock (_gate)
        {
            LastProblem = result.Problem;

            State = result.Problem is not null
                ? DictationState.Failed
                : DictationState.Idle;

            if (result.Text.Length > 0)
                _phrases.Add(result.Text);
        }

        // The text goes out as a partial first, because that is what puts it in the input
        // box -- the same route the Windows engine's phrases take. Keeping one route means
        // the box is filled by one piece of code rather than two that can disagree.
        if (result.Text.Length > 0)
        {
            PartialText?.Invoke(_utteranceFull
                ? result.Text + " [recording reached the three-minute limit]"
                : result.Text);
        }

        Finished?.Invoke(
            result.Text,
            result.Problem is not null
                ? DictationState.Failed
                : result.Text.Length > 0
                    ? DictationState.Idle
                    : DictationState.Silent);
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
