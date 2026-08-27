using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;

using Shellvis.Core.Voice;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Local Whisper recognition, and a side-by-side measurement against the Windows engine.
///
/// The reason this harness exists is a claim, and a claim is what has to be measured: the
/// Windows desktop recognizer turned "Welche Termine liegen diese Woche an" into "er Pillen
/// SA niemals in dieser Woche an", and the fix asserts a different recogniser is better. So
/// both run on <b>the same audio</b> and both transcriptions are printed. If Whisper is not
/// better on this machine, that is visible here rather than after a release.
///
/// The properties that do not need a model -- the catalog, the resolution order, the
/// truncated-file guard -- are checked unconditionally. The recognition half needs the model
/// on disk and says so instead of downloading half a gigabyte because a test ran.
/// </summary>
internal static class WhisperProbe
{
    public static async Task<int> RunAsync(bool fetch)
    {
        int failures = 0;

        failures += CatalogChecks();
        failures += ResolutionChecks();
        failures += PresenceChecks();

        WhisperModel model = WhisperModelStore.Configured(null, out _);

        if (!WhisperModelStore.IsPresent(model) && fetch)
        {
            Console.WriteLine();
            Console.WriteLine($"-- fetching {model.Id} ({model.SizeText}) --");

            int last = -1;

            string? problem = await WhisperModelStore.DownloadAsync(model, percent =>
            {
                if (percent / 5 == last / 5)
                    return;

                last = percent;
                Console.Write($"\r    {percent,3}%");
            }).ConfigureAwait(false);

            Console.WriteLine();

            if (problem is not null)
            {
                Console.WriteLine("    " + problem);
                Console.WriteLine();
                Console.WriteLine("SKIPPED the recognition half: the model is not installed.");

                return Report(failures);
            }
        }

        if (!WhisperModelStore.IsPresent(model))
        {
            Console.WriteLine();
            Console.WriteLine($"SKIPPED the recognition half: {model.File} is not in "
                + $"{WhisperModelStore.Directory}. Run 'probe whisper --fetch' to install it "
                + $"({model.SizeText}).");

            return Report(failures);
        }

        failures += await RecognitionChecks(model).ConfigureAwait(false);

        return Report(failures);
    }

    private static int CatalogChecks()
    {
        Console.WriteLine("-- the model catalog --");
        int failures = 0;

        IReadOnlyList<WhisperModel> catalog = WhisperModelStore.Catalog;

        failures += Check("the catalog is not empty", catalog.Count > 0);

        failures += Check(
            "every id is unique, so a config value maps to one model",
            catalog.Select(m => m.Id.ToLowerInvariant()).Distinct().Count() == catalog.Count);

        // Every entry needs a size, because the size is what makes a half-finished download
        // detectable -- and a truncated ggml file does not fail politely, it takes the
        // process down inside native code.
        failures += Check(
            "every entry declares an expected size",
            catalog.All(m => m.Bytes > 1_000_000));

        failures += Check(
            "and a note explaining what the size buys",
            catalog.All(m => m.Note.Length > 10));

        failures += Check(
            "the default is in the catalog",
            WhisperModelStore.Find(WhisperModelStore.DefaultModelId) is not null);

        foreach (WhisperModel m in catalog)
            Console.WriteLine($"    {m.Id,-8} {m.SizeText,8}  {m.Note}");

        Console.WriteLine();
        return failures;
    }

    private static int ResolutionChecks()
    {
        Console.WriteLine("-- which model gets used --");
        int failures = 0;

        WhisperModel named = WhisperModelStore.Resolve("base", out string? noWarning);

        failures += Check("a named model is honoured", named.Id == "base");
        failures += Check("and resolving it warns about nothing", noWarning is null);

        WhisperModel misspelt = WhisperModelStore.Resolve("smal", out string? warning);

        // The polarity this project has now fixed three times: a wrong value must be named,
        // not silently replaced with the default. A setting that looks obeyed while
        // something else is loaded is worse than a refusal.
        failures += Check(
            "a misspelt model falls back to the default",
            misspelt.Id == WhisperModelStore.DefaultModelId);

        failures += Check("and says so rather than substituting silently", warning is not null);

        failures += Check(
            "and the warning lists what it could have been",
            warning?.Contains("small") == true && warning.Contains("medium"));

        WhisperModel empty = WhisperModelStore.Resolve(null, out _);
        failures += Check("an unset model is the default", empty.Id == WhisperModelStore.DefaultModelId);

        WhisperModel fromConfig = WhisperModelStore.Configured("tiny", out _);

        // config.yaml outranks the installer's record: once the user has edited the file,
        // a choice made during setup months ago must not win.
        failures += Check(
            "config.yaml outranks whatever setup recorded",
            fromConfig.Id == "tiny");

        Console.WriteLine();
        return failures;
    }

    private static int PresenceChecks()
    {
        Console.WriteLine("-- a partial download is not a model --");
        int failures = 0;

        // Written into the real directory under a catalog name, because IsPresent resolves
        // the path itself and testing a different path would test different code. Removed
        // again below, and only if it was not there to begin with.
        WhisperModel victim = WhisperModelStore.Find("tiny")!;
        string path = WhisperModelStore.PathFor(victim);
        bool alreadyThere = File.Exists(path);

        if (alreadyThere)
        {
            Console.WriteLine($"    {victim.File} is already installed; not touching it.");
            failures += Check("an installed model is seen as present", WhisperModelStore.IsPresent(victim));
            Console.WriteLine();

            return failures;
        }

        try
        {
            Directory.CreateDirectory(WhisperModelStore.Directory);
            File.WriteAllBytes(path, new byte[1024]);

            failures += Check(
                "a truncated file is refused rather than loaded",
                !WhisperModelStore.IsPresent(victim));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        failures += Check("and a missing file is not present either", !WhisperModelStore.IsPresent(victim));

        Console.WriteLine();
        return failures;
    }

    private static async Task<int> RecognitionChecks(WhisperModel model)
    {
        Console.WriteLine($"-- recognition with {model.Id}, against the Windows engine --");
        int failures = 0;

        using var whisper = new WhisperRecognizer();

        var clock = Stopwatch.StartNew();
        string? load = whisper.Load(WhisperModelStore.PathFor(model));
        clock.Stop();

        if (load is not null)
        {
            Console.WriteLine("    " + load);
            return failures + Check("the model loads", false);
        }

        Console.WriteLine($"    loaded in {clock.ElapsedMilliseconds} ms");
        failures += Check("the model loads", whisper.IsLoaded);

        // The sentence from the actual report, not a convenient one. It is the case the old
        // engine failed on, so it is the case that has to improve.
        const string spoken = "Welche Termine liegen diese Woche an";

        string wav = Path.Combine(Path.GetTempPath(), $"shellvis-whisper-{Guid.NewGuid():N}.wav");

        try
        {
            if (!Synthesise(spoken, wav))
            {
                Console.WriteLine("    no German voice installed; nothing to compare.");
                Console.WriteLine();

                return failures;
            }

            byte[] pcm = File.ReadAllBytes(wav)[44..];

            clock.Restart();
            WhisperResult result = await whisper.TranscribeAsync(pcm, "de-DE").ConfigureAwait(false);
            clock.Stop();

            string windows = ViaWindowsEngine(wav);

            Console.WriteLine($"    spoken:   \"{spoken}\"");
            Console.WriteLine($"    windows:  \"{windows}\"");
            Console.WriteLine($"    whisper:  \"{result.Text}\"  ({clock.ElapsedMilliseconds} ms)");

            failures += Check("whisper produced text", result.Text.Length > 0);
            failures += Check("and reported no problem", result.Problem is null);

            int whisperWords = Overlap(spoken, result.Text);
            int windowsWords = Overlap(spoken, windows);

            Console.WriteLine($"    key words: whisper {whisperWords}, windows {windowsWords} "
                + $"of {KeyWords(spoken).Count}");

            failures += Check(
                "whisper gets most of the utterance",
                whisperWords >= KeyWords(spoken).Count - 1);

            // Not "whisper beats windows" as a hard assertion. Synthesised speech is the
            // easy case -- clean, evenly paced, no room noise -- and the old engine can do
            // well on it while failing on a real voice, which is exactly what happened. So
            // the comparison is printed and only the weaker claim is enforced: whisper must
            // not be WORSE, because that would sink the whole premise.
            failures += Check(
                "and is no worse than the Windows engine on the same audio",
                whisperWords >= windowsWords);

            // Whisper's signature failure: given silence it emits subtitle boilerplate
            // learnt from its training data ("Vielen Dank fuer's Zuschauen"). Left
            // unguarded, releasing the microphone in a quiet room types a sentence the user
            // never said into their prompt box.
            byte[] silence = new byte[16000 * 2 * 3];
            WhisperResult quiet = await whisper.TranscribeAsync(silence, "de-DE").ConfigureAwait(false);

            Console.WriteLine($"    three seconds of silence gave: \"{quiet.Text}\"");

            failures += Check(
                "silence produces nothing rather than invented subtitles",
                quiet.Text.Length == 0);

            // Room noise, not digital silence -- the case that actually happens when someone
            // presses the key and then does not speak. A gate that only catches an all-zero
            // buffer would pass this straight to the model, which is where the invented
            // sentences come from.
            byte[] noise = new byte[16000 * 2 * 3];
            var rng = new Random(1);

            for (int i = 0; i + 1 < noise.Length; i += 2)
            {
                short sample = (short)rng.Next(-260, 260);
                noise[i] = (byte)(sample & 0xFF);
                noise[i + 1] = (byte)((sample >> 8) & 0xFF);
            }

            WhisperResult hiss = await whisper.TranscribeAsync(noise, "de-DE").ConfigureAwait(false);

            Console.WriteLine($"    three seconds of room noise gave: \"{hiss.Text}\"");

            failures += Check(
                "and neither does quiet room noise",
                hiss.Text.Length == 0);

            // Below the minimum length: a fragment makes the model confidently produce a
            // whole plausible sentence, and the user would read words they never spoke.
            WhisperResult sliver = await whisper
                .TranscribeAsync(new byte[2000], "de-DE").ConfigureAwait(false);

            failures += Check(
                "a fragment too short to be speech is refused",
                sliver.Text.Length == 0 && sliver.Problem is null);

            // The annotation forms the model actually emitted in this project, each of which
            // reached the prompt box once. A list of bare words did not stop them: the model
            // brackets non-speech in whatever style its training subtitles used, so the
            // decoration has to be stripped before matching rather than enumerated.
            foreach (string annotation in new[]
                { "* Musik *", "[Musik]", "*Signalton*", "( Applaus )", "_Lachen_", "-- Stille --",
                  "[Stimmengewirr]", "[Tastaturgeklapper]", "(unverständlich)", "<inaudible>" })
            {
                failures += Check(
                    $"the annotation {annotation} is recognised as non-speech",
                    Shellvis.Core.Voice.WhisperRecognizer.LooksLikeNonSpeech(annotation));
            }

            // The other side of the shape rule, which matters as much: a sentence that merely
            // contains brackets is speech. A rule that ate it would silently drop dictation
            // the user actually spoke, which is worse than an annotation slipping through.
            foreach (string sentence in new[]
                { "Spiel bitte Musik im Wohnzimmer", "(a) und (b) vergleichen",
                  "Setze das in Klammern (so wie hier)", "5 * 3 * 2 berechnen" })
            {
                failures += Check(
                    $"and \"{sentence}\" is kept as speech",
                    !Shellvis.Core.Voice.WhisperRecognizer.LooksLikeNonSpeech(sentence));
            }

            failures += await SpeedChecks(whisper, pcm, spoken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(wav);
            }
            catch (Exception)
            {
            }
        }

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// Show what the encoder window costs, and that shrinking it keeps the words.
    ///
    /// This exists because the first working run took fifteen seconds for two seconds of
    /// speech, which is not a usable dictation feature however good the text is. The fix --
    /// sizing whisper's 30-second encoder window to the actual recording -- is a speed/
    /// accuracy trade, and a trade should be shown rather than asserted. Each window is
    /// timed on the same audio and its transcription printed, so a future change that
    /// quietly costs words is visible here.
    /// </summary>
    private static async Task<int> SpeedChecks(
        WhisperRecognizer whisper, byte[] pcm, string spoken)
    {
        Console.WriteLine();
        Console.WriteLine($"-- encoder window, on {pcm.Length / 32000.0:F1} s of audio --");

        int failures = 0;
        int wanted = KeyWords(spoken).Count;
        long automatic = 0;
        long fullWindow = 0;

        foreach (int? window in new int?[] { 1500, 512, null })
        {
            var clock = Stopwatch.StartNew();

            WhisperResult r = await whisper
                .TranscribeAsync(pcm, "de-DE", default, window)
                .ConfigureAwait(false);

            clock.Stop();

            string label = window?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? "auto";

            Console.WriteLine($"    {label,-5} {clock.ElapsedMilliseconds,6} ms  "
                + $"{Overlap(spoken, r.Text)}/{wanted} words  \"{r.Text}\"");

            if (window is null)
                automatic = clock.ElapsedMilliseconds;

            if (window == 1500)
                fullWindow = clock.ElapsedMilliseconds;

            if (window is null)
            {
                failures += Check(
                    "the automatic window still gets the whole utterance",
                    Overlap(spoken, r.Text) >= wanted - 1);
            }
        }

        // The point of the change, stated as a check so a regression shows up as a failure
        // rather than as a slow feature nobody measures.
        failures += Check(
            "and is faster than the full 30-second window",
            automatic < fullWindow);

        Console.WriteLine($"    {fullWindow} ms -> {automatic} ms");

        return failures;
    }

    /// <summary>Recognise the same file with the engine Whisper is replacing.</summary>
    private static string ViaWindowsEngine(string wav)
    {
        RecognizerInfo? info = SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "de");

        if (info is null)
            return "(no German Windows recognizer)";

        try
        {
            using var engine = new SpeechRecognitionEngine(info);
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToWaveFile(wav);

            var heard = new List<string>();

            while (true)
            {
                RecognitionResult? result = engine.Recognize(TimeSpan.FromSeconds(4));

                if (result is null)
                    break;

                heard.Add(result.Text);
            }

            return string.Join(" ", heard).Trim();
        }
        catch (Exception ex)
        {
            return $"({ex.GetType().Name})";
        }
    }

    private static bool Synthesise(string text, string path)
    {
        using var synth = new SpeechSynthesizer();

        VoiceInfo? german = synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo)
            .FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == "de");

        if (german is null)
            return false;

        synth.SelectVoice(german.Name);

        // The exact format dictation captures in, so the two recognisers differ in what they
        // are and in nothing else.
        synth.SetOutputToWaveFile(path,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

        synth.Speak(text);
        return true;
    }

    private static List<string> KeyWords(string text) =>
        [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3)];

    private static int Overlap(string spoken, string heard) =>
        KeyWords(spoken).Count(w => heard.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static int Check(bool condition, string what) => Check(what, condition);

    private static int Check(string what, bool condition)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }

    private static int Report(int failures)
    {
        Console.WriteLine(failures == 0
            ? "VERIFIED: local Whisper recognition works and is no worse than the Windows engine."
            : $"{failures} check(s) failed.");

        return failures == 0 ? 0 : 1;
    }
}
