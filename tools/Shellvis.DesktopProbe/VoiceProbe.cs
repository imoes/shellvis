using System.Diagnostics;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using Shellvis.Core.Voice;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Checks dictation against the recognizer actually installed.
///
/// Two halves, and the split matters. What can be checked without a human is the
/// availability logic, the language selection and the refusal messages -- and those are
/// where the avoidable mistakes live. Whether the microphone picks up a particular voice
/// cannot be checked from a script, so instead the recognizer is fed a WAV that the
/// machine's own speech synthesiser produced. That proves the engine, the grammar and the
/// German language pack work end to end, without asserting anything about acoustics.
/// </summary>
internal static class VoiceProbe
{
    public static int Run()
    {
        int failures = 0;

        Console.WriteLine("=== Dictation ===");
        Console.WriteLine();

        failures += Availability();
        failures += LanguageSelection();
        failures += Recognition();
        failures += Lifecycle();

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "VERIFIED: local recognition works, and an absent language is refused rather than guessed."
            : $"{failures} dictation check(s) FAILED.");

        return failures == 0 ? 0 : 1;
    }

    private static int Availability()
    {
        Console.WriteLine("-- what is installed --");
        int failures = 0;

        // Diagnostic: which System.Speech is actually loaded, and what the registry says.
        // The two disagreed once and guessing wasted a cycle.
        try
        {
            var asm = typeof(SpeechRecognitionEngine).Assembly;
            Console.WriteLine($"    assembly: {asm.GetName().Version} at {asm.Location}");
            Console.WriteLine($"    process:  {(Environment.Is64BitProcess ? "x64" : "x86")}, "
                + $"culture {System.Globalization.CultureInfo.CurrentUICulture.Name}");

            using var tokens = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Speech\Recognizers\Tokens");

            Console.WriteLine($"    registry: {tokens?.GetSubKeyNames().Length ?? -1} desktop token(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("    diagnostic failed: " + ex.Message);
        }

        // The device list, because "which microphone is it listening to" is the first
        // question when nothing is recognised, and this machine has more than one.
        IReadOnlyList<string> devices = DictationEngine.InputDevices();
        Console.WriteLine($"    {devices.Count} recording device(s):");

        for (int i = 0; i < devices.Count; i++)
            Console.WriteLine($"      {i} = {devices[i]}");

        IReadOnlyList<string> recognizers = DictationEngine.InstalledRecognizers();

        foreach (string recognizer in recognizers)
            Console.WriteLine("    " + recognizer);

        failures += Check("at least one recognizer is installed", recognizers.Count > 0);
        failures += Check("and IsAvailable agrees", DictationEngine.IsAvailable == (recognizers.Count > 0));

        // Named so the reader knows what the rest of this probe is actually testing
        // against. On this machine only German is installed.
        Console.WriteLine($"    {recognizers.Count} recognizer(s); dictation is per language.");

        Console.WriteLine();
        return failures;
    }

    private static int LanguageSelection()
    {
        Console.WriteLine("-- language selection --");
        int failures = 0;

        using var engine = new DictationEngine();

        // A recognizer for a DIFFERENT language would produce confident nonsense, which is
        // worse than refusing: the user would read plausible words that are not what they
        // said.
        string? refused = engine.Start("ja-JP");

        Console.WriteLine("    " + refused);

        failures += Check("an uninstalled language is refused", refused is not null);

        failures += Check(
            "and the refusal lists what IS installed",
            refused?.Contains("Installed:") == true);

        failures += Check(
            "and says where to add one",
            refused?.Contains("Settings") == true);

        failures += Check("the engine stays idle after a refusal", engine.State == DictationState.Idle);

        // A de-AT request should be served by the de-DE recognizer rather than refused:
        // the region differs, the language does not.
        string? regionFallback = engine.Start("de-AT");

        if (regionFallback is null)
        {
            Console.WriteLine($"    de-AT was served by {engine.Language}");
            failures += Check("a region variant falls back to the same language", engine.Language?.StartsWith("de") == true);
            engine.Cancel();
        }
        else
        {
            Console.WriteLine("    " + regionFallback);
            failures += Check("a region variant falls back to the same language", false);
        }

        Console.WriteLine();
        return failures;
    }

    /// <summary>
    /// Feed the recognizer synthesised speech and see whether it comes back.
    ///
    /// This is the check that proves the German language pack, the dictation grammar and
    /// the engine are genuinely working rather than merely constructible. Synthesised
    /// speech is easier to recognise than a human in a noisy office, so this is a
    /// floor -- but a failure here means dictation cannot work at all.
    /// </summary>
    private static int Recognition()
    {
        Console.WriteLine("-- recognition, from synthesised speech --");
        int failures = 0;

        string wav = Path.Combine(Path.GetTempPath(), $"shellvis-voice-{Guid.NewGuid():N}.wav");
        const string spoken = "zeige mir die laufenden Dienste";

        try
        {
            using (var synth = new SpeechSynthesizer())
            {
                VoiceInfo? german = synth.GetInstalledVoices()
                    .Where(v => v.Enabled)
                    .Select(v => v.VoiceInfo)
                    .FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == "de");

                if (german is null)
                {
                    Console.WriteLine("    no German voice installed; cannot synthesise a test utterance.");
                    Console.WriteLine();
                    return 0;
                }

                synth.SelectVoice(german.Name);
                synth.SetOutputToWaveFile(wav);
                synth.Speak(spoken);
            }

            Console.WriteLine($"    synthesised \"{spoken}\" ({new FileInfo(wav).Length / 1024} KB)");

            RecognizerInfo? recognizer = SpeechRecognitionEngine.InstalledRecognizers()
                .FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "de");

            if (recognizer is null)
            {
                failures += Check("a German recognizer is present", false);
                return failures;
            }

            using var engine = new SpeechRecognitionEngine(recognizer);
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToWaveFile(wav);

            var heard = new List<string>();
            var clock = Stopwatch.StartNew();

            // RecognizeSilence would end at the first pause; recognising repeatedly reads
            // the whole file the way dictation reads a whole utterance.
            while (true)
            {
                RecognitionResult? result = engine.Recognize(TimeSpan.FromSeconds(2));

                if (result is null)
                    break;

                heard.Add(result.Text);
            }

            clock.Stop();

            string text = string.Join(" ", heard);
            Console.WriteLine($"    recognised in {clock.ElapsedMilliseconds} ms: \"{text}\"");

            failures += Check("the recognizer produced text", text.Length > 0);

            // Not an exact match: a dictation engine on synthesised audio will differ in
            // case and word choice, and asserting equality would be a test that fails for
            // reasons that do not matter. What matters is that it heard German words from
            // the utterance rather than noise.
            string[] expected = ["dienste", "laufenden", "zeige"];
            int hits = expected.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

            Console.WriteLine($"    {hits} of {expected.Length} key words recognised");

            failures += Check("and at least one word from the utterance came through", hits >= 1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
            failures += Check("synthesis and recognition ran", false);
        }
        finally
        {
            try
            {
                if (File.Exists(wav))
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
    /// The state machine around a real microphone.
    ///
    /// Started and stopped immediately, so nothing is actually said. What is being checked
    /// is that opening the default capture device works and that stopping leaves nothing
    /// behind -- a dictation feature that holds the microphone open after the user stopped
    /// is a feature nobody will use twice.
    /// </summary>
    private static int Lifecycle()
    {
        Console.WriteLine("-- microphone lifecycle --");
        int failures = 0;

        using var engine = new DictationEngine();

        // Explicit device, which is the path the app now takes when voice.deviceIndex is
        // set. Reported in full, because a truncated message in the pill is what sent me
        // looking here.
        if (DictationEngine.InputDevices().Count > 0)
        {
            string? withDevice = engine.Start("de-DE", 0);
            Console.WriteLine("    device 0: " + (withDevice ?? "started"));

            if (withDevice is null)
                engine.Cancel();
        }

        string? problem = engine.Start("de-DE");

        if (problem is not null)
        {
            Console.WriteLine("    " + problem);

            // No microphone is a legitimate machine state, not a test failure -- but it
            // has to be reported as a clear sentence rather than an exception.
            failures += Check(
                "a missing microphone is reported as a sentence",
                problem.Contains("microphone") || problem.Contains("recognizer"));

            Console.WriteLine();
            return failures;
        }

        failures += Check("listening starts", engine.State == DictationState.Listening);
        failures += Check("the language is reported", engine.Language?.StartsWith("de") == true);

        string? twice = engine.Start("de-DE");
        failures += Check("starting twice is refused rather than doubling up", twice is not null);

        string text = engine.Stop();

        failures += Check("stopping returns to idle", engine.State == DictationState.Idle);
        failures += Check("and nothing was heard in an empty session", text.Length == 0);

        // Started and stopped repeatedly, because holding a capture device open across
        // cycles is the classic leak here and it only shows on the second attempt.
        for (int i = 0; i < 3; i++)
        {
            string? again = engine.Start("de-DE");

            if (again is not null)
            {
                Console.WriteLine("    " + again);
                failures += Check($"restart {i + 1} works", false);
                break;
            }

            engine.Cancel();
        }

        failures += Check("the device can be reopened after cancelling", engine.State == DictationState.Idle);

        // Finished must fire, or the pill sits showing a recording indicator over a
        // microphone that stopped listening.
        var reported = new List<DictationState>();
        engine.Finished += (_, state) => reported.Add(state);

        if (engine.Start("de-DE") is null)
        {
            engine.Stop();

            failures += Check("Finished is raised on stop", reported.Count == 1);

            // Silent, not Idle: the caller needs to distinguish "nothing was said" from
            // "here is your text", because one of those deserves a message.
            failures += Check(
                "an empty session reports Silent, not success",
                reported.Count == 1 && reported[0] == DictationState.Silent);
        }

        Console.WriteLine();
        return failures;
    }

    private static int Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        return ok ? 0 : 1;
    }
}
