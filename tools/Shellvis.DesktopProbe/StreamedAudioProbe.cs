using System.Diagnostics;

using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;

namespace Shellvis.DesktopProbe;

/// <summary>
/// The microphone path, without a microphone.
///
/// This exists because of a defect the existing voice harness could not see. Dictation
/// reported "sound arrived (peak 42/100) but no words were recognised", which is the shape
/// of a problem where the level meter is fed but the recogniser is not: the meter is
/// computed from NAudio's buffer, so it proves NAudio captured audio and says nothing about
/// whether SpeechRecognitionEngine ever read it. And no rejection events fired either --
/// an engine that hears speech it cannot match RAISES a rejection, so zero rejections with
/// audio present points at an engine receiving nothing at all.
///
/// The existing harness feeds the recogniser with SetInputToWaveFile and passes, which is
/// exactly why it missed this: dictation uses SetInputToAudioStream over a hand-written
/// bridge, and that is the part under suspicion. So the same synthesised utterance goes
/// down both paths and the results are compared. No microphone, nobody speaking, and the
/// difference between the two is the answer.
/// </summary>
internal static class StreamedAudioProbe
{
    /// <summary>What NAudio hands over at a time: 100 ms at 16 kHz, 16-bit, mono.</summary>
    private const int ChunkBytes = 16000 * 2 / 10;

    public static int Run()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("-- the same utterance down both paths --");

        RecognizerInfo? recognizer = SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "de");

        if (recognizer is null)
        {
            Console.WriteLine("    no German recognizer installed; nothing to compare.");
            return 0;
        }

        string wav = Path.Combine(Path.GetTempPath(), $"shellvis-stream-{Guid.NewGuid():N}.wav");

        try
        {
            const string spoken = "zeige mir die laufenden Dienste";

            if (!Synthesise(spoken, wav))
            {
                Console.WriteLine("    no German voice installed; nothing to compare.");
                return 0;
            }

            Console.WriteLine($"    synthesised \"{spoken}\" ({new FileInfo(wav).Length / 1024} KB)");

            string fromFile = ViaWaveFile(recognizer, wav);
            Console.WriteLine($"    via SetInputToWaveFile:    \"{fromFile}\"");

            string fromStream = ViaBridge(recognizer, wav);
            Console.WriteLine($"    via the dictation bridge:  \"{fromStream}\"");

            // The file path is the control. If it fails, the machine cannot recognise this
            // utterance at all and the comparison says nothing.
            if (fromFile.Length == 0)
            {
                Console.WriteLine();
                Console.WriteLine("SKIPPED: the recognizer produced nothing even from a wave "
                    + "file, so this machine cannot recognise the test utterance and the "
                    + "comparison would be meaningless.");

                return 0;
            }

            failures += Expect(
                fromStream.Length > 0,
                "the dictation bridge delivers audio the recognizer can hear");

            // Not equality: the same engine on the same audio can still word it slightly
            // differently between runs, and a test that demands identical text fails for a
            // reason that does not matter. What matters is that the stream path is not
            // silent while the file path is not.
            failures += Expect(
                Overlap(fromFile, fromStream) > 0,
                "and hears at least one of the same words");

            Console.WriteLine();
            return Report(failures);
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

        // 16 kHz, 16-bit, mono: the exact format dictation captures in, so the two paths
        // differ in the plumbing and in nothing else.
        synth.SetOutputToWaveFile(path,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

        synth.Speak(text);
        return true;
    }

    private static string ViaWaveFile(RecognizerInfo info, string wav)
    {
        using var engine = new SpeechRecognitionEngine(info);
        engine.LoadGrammar(new DictationGrammar());
        engine.SetInputToWaveFile(wav);

        return Drain(engine);
    }

    /// <summary>
    /// Feed the recogniser through the real bridge, pushed the way NAudio pushes.
    ///
    /// The bridge is internal to Shellvis.Core and stays internal: the harness reaches it
    /// through InternalsVisibleTo rather than by making it public, because widening a
    /// type's visibility to test it changes the thing being tested. Reflection was not an
    /// option either -- Push takes a ReadOnlySpan, and a ref struct cannot be boxed into the
    /// object array that Invoke wants.
    /// </summary>
    private static string ViaBridge(RecognizerInfo info, string wav)
    {
        using var stream = new Shellvis.Core.Voice.BlockingAudioStream();

        // Everything after the 44-byte canonical WAV header is the PCM the microphone
        // would have produced.
        byte[] pcm = File.ReadAllBytes(wav)[44..];

        using var engine = new SpeechRecognitionEngine(info);
        engine.LoadGrammar(new DictationGrammar());
        engine.SetInputToAudioStream(stream,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));

        // Pushed from another thread, in chunks, exactly as a capture callback does. Doing
        // it inline before recognising would fill the queue first and hide any blocking
        // problem, which is half of what is being tested.
        var pump = new Thread(() =>
        {
            for (int at = 0; at < pcm.Length; at += ChunkBytes)
            {
                int take = Math.Min(ChunkBytes, pcm.Length - at);
                stream.Push(new ReadOnlySpan<byte>(pcm, at, take));
                Thread.Sleep(20);
            }

            // A little trailing silence, so the engine sees an end of utterance rather than
            // an abrupt cut.
            for (int i = 0; i < 10; i++)
            {
                stream.Push(new byte[ChunkBytes]);
                Thread.Sleep(20);
            }

            stream.Finish();
        })
        {
            IsBackground = true,
        };

        pump.Start();

        string heard = Drain(engine);

        pump.Join(TimeSpan.FromSeconds(5));

        Console.WriteLine($"    bridge saw {stream.ReadCalls} read(s), handed over "
            + $"{stream.BytesRead} of {pcm.Length} bytes, {stream.SeekCalls} seek(s)");

        return heard;
    }

    private static string Drain(SpeechRecognitionEngine engine)
    {
        var heard = new List<string>();
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(25))
        {
            RecognitionResult? result;

            try
            {
                result = engine.Recognize(TimeSpan.FromSeconds(4));
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (result is null)
                break;

            heard.Add(result.Text);
        }

        return string.Join(" ", heard).Trim();
    }

    private static int Overlap(string a, string b)
    {
        string[] words = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Count(w =>
            w.Length > 3 && b.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static int Expect(bool condition, string what)
    {
        Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}");
        return condition ? 0 : 1;
    }

    private static int Report(int failures)
    {
        Console.WriteLine(failures == 0
            ? "VERIFIED: the dictation bridge carries audio to the recognizer."
            : $"{failures} check(s) failed: the bridge is where dictation loses the audio.");

        return failures == 0 ? 0 : 1;
    }
}
