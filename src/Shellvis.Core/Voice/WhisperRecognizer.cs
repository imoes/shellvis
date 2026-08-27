using System.Text;

using Whisper.net;

namespace Shellvis.Core.Voice;

/// <summary>
/// Local speech recognition with a Whisper model, through whisper.cpp.
///
/// <b>Why this exists.</b> The Windows desktop recognizer that <see cref="DictationEngine"/>
/// otherwise uses is a GMM-HMM engine from the Vista era, and its free-form German dictation
/// is poor enough to be unusable: "Welche Termine liegen diese Woche an" came back as "er
/// Pillen SA niemals in dieser Woche an". Note that it got the tail right -- the acoustics
/// were fine, the language model guessed the rest. No amount of gain fixes that.
///
/// <b>Why batch is not a compromise here.</b> Whisper transcribes a finished utterance
/// rather than streaming words as they are spoken, and for push-to-talk that is the better
/// shape: the model sees the whole sentence and can use its end to interpret its beginning,
/// which is exactly what the old engine could not do. The cost is that the text appears when
/// the user releases rather than while they speak, so the UI has to say it is working.
///
/// <b>Why Whisper.net does not break the no-runtime-dependency rule.</b> It carries
/// whisper.cpp as a native library loaded into this process -- not a bundled interpreter
/// driven over a pipe, which is why Playwright was rejected for the browser tools. Nothing
/// new runs, and nothing leaves the machine.
/// </summary>
public sealed class WhisperRecognizer : ITranscriber
{
    private readonly object _gate = new();

    private WhisperFactory? _factory;
    private string? _loadedFrom;
    private bool _disposed;

    /// <summary>Which model file is loaded, for the transcript.</summary>
    public string? LoadedModel { get; private set; }

    /// <summary>Never: this is the whole point of the local model.</summary>
    public bool IsRemote => false;

    public string Description =>
        LoadedModel is { Length: > 0 } model ? $"Whisper ({model})" : "Whisper";

    /// <summary>
    /// Words this application's users say that a general model has no reason to expect.
    ///
    /// Whisper accepts an initial prompt and biases towards its vocabulary. Worth spending,
    /// because the failures that matter here are exactly the domain words: a wrong
    /// everyday word is obvious to the reader, whereas "Kalender" heard as "Kalander" looks
    /// like something the user might have meant.
    ///
    /// Kept short on purpose. The prompt competes for the model's context, and a long list
    /// of terms starts pulling ordinary sentences towards it -- a bias that helps a term
    /// appear also makes it appear when it was not said.
    /// </summary>
    private const string Vocabulary =
        "Shellvis, PowerShell, Cmdlet, Outlook, Kalender, Termine, Dienste, Prozesse, "
        + "Registry, Broker, Skill, Session, Provider, Modell, Diktat.";

    /// <summary>
    /// Load a model file. Returns null on success, or why it could not be loaded.
    ///
    /// Loading is separate from construction, and slow: whisper.cpp reads the whole file
    /// and allocates its context, which for the small model is a second or so and half a
    /// gigabyte of memory. It is therefore done once and kept -- doing it per utterance
    /// would put that in front of every single dictation.
    /// </summary>
    public string? Load(string modelPath)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_factory is not null && _loadedFrom == modelPath)
                return null;

            if (!File.Exists(modelPath))
                return $"the model file is not there: {modelPath}";

            try
            {
                _factory?.Dispose();
                _factory = WhisperFactory.FromPath(modelPath);
                _loadedFrom = modelPath;
                LoadedModel = Path.GetFileName(modelPath);

                return null;
            }
            catch (Exception ex)
            {
                _factory = null;
                _loadedFrom = null;
                LoadedModel = null;

                // The reason is kept rather than reduced to "unavailable". A truncated
                // model, a missing native runtime and an unsupported CPU all land here and
                // have completely different remedies.
                return $"the whisper model could not be loaded: {ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    public bool IsLoaded
    {
        get
        {
            lock (_gate)
                return _factory is not null;
        }
    }

    /// <summary>
    /// The interface method, forwarding to the overload that also takes an encoder window.
    ///
    /// Explicit because the public method carries a fourth, optional parameter for the harness,
    /// and an extra optional parameter does not satisfy an interface member -- the signatures
    /// have to match exactly.
    /// </summary>
    Task<WhisperResult> ITranscriber.TranscribeAsync(
        ReadOnlyMemory<byte> pcm, string? language, CancellationToken cancel) =>
        TranscribeAsync(pcm, language, cancel);

    /// <summary>
    /// Transcribe 16 kHz mono 16-bit PCM.
    /// </summary>
    /// <param name="pcm">The captured utterance, exactly as the microphone delivered it.</param>
    /// <param name="language">A culture name such as de-DE; only the language part is used.</param>
    /// <param name="audioContext">
    /// Encoder window size, or null to size it from the recording. Only for the harness,
    /// which sweeps it to justify the automatic choice with numbers rather than a claim.
    /// </param>
    public async Task<WhisperResult> TranscribeAsync(
        ReadOnlyMemory<byte> pcm, string? language, CancellationToken cancel = default,
        int? audioContext = null)
    {
        WhisperFactory? factory;

        lock (_gate)
            factory = _factory;

        if (factory is null)
            return new WhisperResult(string.Empty, "no whisper model is loaded.");

        // Under a second of audio is not an utterance. Whisper is at its most confidently
        // wrong on very short input: given a fragment it will emit a plausible sentence,
        // and the user would read words they never said.
        if (pcm.Length < 16000 * 2 / 2)
            return new WhisperResult(string.Empty, null);

        try
        {
            float[] samples = ToFloat(pcm.Span);

            // Silence is not transcribed, it is rejected. This model family invents fluent
            // sentences when given nothing -- and shrinking the encoder window to make
            // dictation fast made it markedly worse: the same three seconds of digital
            // silence that produced "" at the full window produced "Das ist der erste Teil
            // der Strecke" at a short one. That is the worst possible failure here, because
            // the invented text lands in the user's prompt box looking like something they
            // said.
            //
            // A blocklist cannot cover this: the hallucination is not a fixed phrase. The
            // only reliable guard is to not ask the question, which also saves the seconds
            // of CPU that an accidental key press would otherwise cost.
            if (!HasSpeech(samples))
                return new WhisperResult(string.Empty, null);

            // Two guards against the failure this model family is known for: on silence or
            // noise it emits subtitle boilerplate ("Vielen Dank fuers Zuschauen") learnt
            // from its training data. The threshold makes it say nothing instead, and the
            // filter below catches what still gets through.
            using WhisperProcessor processor = factory.CreateBuilder()
                .WithLanguage(LanguageOf(language))
                .WithNoSpeechThreshold(0.6f)
                .WithPrompt(Vocabulary)
                .WithThreads(Threads)
                .WithAudioContextSize(audioContext ?? ContextFor(samples.Length))
                .Build();

            var text = new StringBuilder();

            await foreach (SegmentData segment in processor
                .ProcessAsync(samples, cancel).ConfigureAwait(false))
            {
                string piece = segment.Text.Trim();

                if (piece.Length == 0 || IsBoilerplate(piece))
                    continue;

                if (text.Length > 0)
                    text.Append(' ');

                text.Append(piece);
            }

            return new WhisperResult(text.ToString().Trim(), null);
        }
        catch (OperationCanceledException)
        {
            return new WhisperResult(string.Empty, null);
        }
        catch (Exception ex)
        {
            return new WhisperResult(string.Empty, $"transcription failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether this recording plausibly contains speech at all.
    ///
    /// Two measures, because either alone misfires. A peak on its own passes a single click
    /// or a desk knock; loudness averaged over the whole recording on its own fails a short
    /// word inside a long pause. So speech has to be both loud somewhere and sustained
    /// somewhere: at least a tenth of a second of frames carrying real energy.
    ///
    /// The thresholds sit far below speech and far above a quiet room. What arrives here has
    /// already been through <see cref="CaptureGain"/>, which lifts anything above its noise
    /// floor towards 0.6 of full scale -- so a spoken word is around that, while an
    /// unamplified quiet room stays in the thousandths.
    /// </summary>
    private static bool HasSpeech(float[] samples)
    {
        const int frame = 1600;             // 100 ms at 16 kHz
        const double loudEnough = 0.02;     // about -34 dBFS
        const int framesNeeded = 1;

        int loud = 0;
        double peak = 0;

        for (int start = 0; start + frame <= samples.Length; start += frame)
        {
            double sum = 0;

            for (int i = start; i < start + frame; i++)
            {
                double value = samples[i];
                sum += value * value;

                if (Math.Abs(value) > peak)
                    peak = Math.Abs(value);
            }

            if (Math.Sqrt(sum / frame) > loudEnough)
                loud++;
        }

        return loud >= framesNeeded && peak > 0.05;
    }

    /// <summary>
    /// How many threads whisper.cpp may use.
    ///
    /// The library defaults to four, which on a twelve-thread laptop leaves most of the
    /// machine idle while the user waits. Two are left free on purpose: this runs while the
    /// pill is on screen and, on the agent's own machine, possibly while a turn is in
    /// flight -- taking every core would make the UI stutter to shave a fraction off a
    /// wait the user is already looking at.
    /// </summary>
    private static int Threads { get; } = Math.Max(2, Environment.ProcessorCount - 2);

    /// <summary>
    /// The encoder window to use for a recording of this length.
    ///
    /// This is the single biggest lever on how long a dictation takes. Whisper's encoder
    /// always runs over a 30-second window -- 1500 positions -- whether the recording is
    /// thirty seconds or two, so a short utterance pays the full price for silence that was
    /// padded in. Shrinking the window to fit the audio is what turns an unusable wait into
    /// a usable one.
    ///
    /// The margin is deliberate and generous. Too small a window does not degrade
    /// gracefully: it cuts the tail off the transcription, and losing the end of a sentence
    /// is exactly the failure this whole change was meant to remove. So there is a floor,
    /// and anything approaching a full window gets the full window.
    /// </summary>
    private static int ContextFor(int samples)
    {
        const int full = 1500;
        const int positionsPerSecond = full / 30;

        double seconds = samples / 16000.0;

        if (seconds >= 25)
            return full;

        int sized = (int)Math.Ceiling(seconds * positionsPerSecond) + 256;

        return Math.Clamp(sized, 512, full);
    }

    /// <summary>
    /// The language code whisper wants: the two-letter part, or null to auto-detect.
    ///
    /// Given de-DE it must be told "de" -- passed the full culture name it does not match
    /// its own language table and silently detects instead, which on a short German
    /// utterance lands on Dutch often enough to matter.
    /// </summary>
    private static string LanguageOf(string? language) =>
        language is { Length: >= 2 } ? language[..2].ToLowerInvariant() : "auto";

    /// <summary>
    /// Phrases the model produces from silence rather than from speech.
    ///
    /// These come from subtitled video in its training data and appear when the audio holds
    /// nothing to transcribe. Matched whole, not as substrings: someone dictating a message
    /// about a video could legitimately write one of these, and the failure being caught is
    /// a segment that consists of nothing else.
    /// </summary>
    /// <summary>Whether a segment is a non-speech annotation. Public for the harness.</summary>
    public static bool LooksLikeNonSpeech(string text) => IsBoilerplate(text);

    private static bool IsBoilerplate(string text)
    {
        string trimmed = text.Trim();

        if (trimmed.Length == 0)
            return true;

        // A SHAPE rule, not a vocabulary one, and that is the point.
        //
        // A list of literal phrases was defeated three times in a row: "Musik" was listed and
        // "* Musik *" walked past it, then "[Stimmengewirr]" arrived, which no list would have
        // anticipated. What these have in common is not their words but their form -- Whisper
        // wraps non-speech annotations in brackets, asterisks or underscores, learnt from the
        // subtitles it was trained on. A segment that is ENTIRELY wrapped is an annotation
        // whatever the word inside, and that closes the whole class instead of one instance.
        //
        // Entirely, not partly: someone dictating "put it in brackets (like this)" keeps their
        // sentence, because only the segment as a whole counts.
        if (IsWrapped(trimmed))
            return true;

        // The literal list stays for the unwrapped ones. "Vielen Dank fuers Zuschauen" arrives
        // as an ordinary sentence with no decoration at all, so no shape rule can catch it.
        string bare = trimmed.Trim(' ', '\t', '*', '_', '[', ']', '(', ')', '.', '!', '?', '-', '—');

        return Boilerplate.Contains(bare.Trim());
    }

    /// <summary>Whether the whole segment sits inside one pair of annotation marks.</summary>
    private static bool IsWrapped(string text)
    {
        (char Open, char Close)[] pairs =
        [
            ('[', ']'),
            ('(', ')'),
            ('*', '*'),
            ('_', '_'),
            ('<', '>'),
        ];

        foreach ((char open, char close) in pairs)
        {
            if (text.Length < 2 || text[0] != open || text[^1] != close)
                continue;

            string inside = text[1..^1];

            // The closer must belong to the opener. "(a) and (b)" is a sentence, not an
            // annotation, and it starts and ends with the same characters.
            if (inside.Length > 0 && !inside.Contains(close))
                return true;
        }

        return false;
    }

    private static readonly HashSet<string> Boilerplate = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vielen Dank",
        "Vielen Dank für's Zuschauen",
        "Vielen Dank fuer's Zuschauen",
        "Vielen Dank für das Zuschauen",
        "Untertitel im Auftrag des ZDF, 2017",
        "Untertitelung des ZDF, 2020",
        "Untertitel der Deutschen Welle",
        "Copyright WDR",
        "Thanks for watching",
        "Thank you for watching",
        "Subtitles by the Amara.org community",
        "Musik",
        "Music",
        "Applaus",
        "Applause",
        "Signalton",
        "Piepton",
        "Räuspern",
        "Lachen",
        "Stille",
        "Geräusche",
        "SPEAKER_00",
    };

    /// <summary>
    /// 16-bit PCM to the normalised floats whisper.cpp expects.
    ///
    /// Divided by 32768 rather than 32767 so that the scale is exact for negatives and the
    /// result never leaves [-1, 1); the asymmetry of two's complement is why the obvious
    /// constant is the wrong one.
    /// </summary>
    private static float[] ToFloat(ReadOnlySpan<byte> pcm)
    {
        int count = pcm.Length / 2;
        float[] samples = new float[count];

        for (int i = 0; i < count; i++)
        {
            short sample = (short)(pcm[i * 2] | (pcm[(i * 2) + 1] << 8));
            samples[i] = sample / 32768f;
        }

        return samples;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                _factory?.Dispose();
            }
            catch (Exception)
            {
            }

            _factory = null;
        }
    }
}

/// <summary>What a transcription produced, and why it produced nothing when it did.</summary>
/// <param name="Text">The recognised text, or empty.</param>
/// <param name="Problem">
/// A sentence for the transcript, or null. Separate from empty text because "nothing was
/// said" and "the model could not run" need different things from the user.
/// </param>
public sealed record WhisperResult(string Text, string? Problem);
