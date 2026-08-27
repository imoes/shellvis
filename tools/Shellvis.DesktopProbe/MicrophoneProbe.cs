using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Shellvis.DesktopProbe;

/// <summary>
/// Which recording device is which, and which one actually hears anything.
///
/// This exists because "the Windows default recording device" turned out to be an ambiguous
/// phrase. Windows keeps <b>two</b> defaults for capture -- the multimedia default and the
/// communications default -- and they are routinely different endpoints: a headset is often
/// the communications default while the laptop's own array stays the multimedia one. NAudio's
/// WAVE_MAPPER resolves to the multimedia default, so routing dictation through it can open a
/// different microphone than the one Teams uses, and the symptom is a peak level of zero on a
/// machine whose microphone demonstrably works.
///
/// So the two defaults are printed side by side, and then every device is opened in turn and
/// its actual level measured. Speak while it runs and the answer is unambiguous: the device
/// that shows a level is the one to put in voice.deviceIndex.
/// </summary>
internal static class MicrophoneProbe
{
    public static int Run(int seconds)
    {
        Console.WriteLine("-- what Windows considers default --");

        string multimedia = "unknown";
        string communications = "unknown";

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            multimedia = Describe(enumerator, Role.Multimedia);
            communications = Describe(enumerator, Role.Communications);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    could not ask WASAPI: {ex.Message}");
        }

        Console.WriteLine($"    multimedia default     : {multimedia}");
        Console.WriteLine($"    communications default : {communications}");

        if (!multimedia.Equals(communications, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine("    These are DIFFERENT devices. WAVE_MAPPER -- which is what");
            Console.WriteLine("    voice.deviceIndex: -1 opens -- follows the multimedia one,");
            Console.WriteLine("    while Teams and other communication apps follow the other.");
            Console.WriteLine("    That difference is enough to make dictation deaf on a machine");
            Console.WriteLine("    where the microphone plainly works.");
        }

        Console.WriteLine();
        Console.WriteLine($"-- every capture device, {seconds}s each --");
        Console.WriteLine("   Speak now if you can; a device that hears you shows a level.");
        Console.WriteLine();

        int count = WaveInEvent.DeviceCount;

        if (count == 0)
        {
            Console.WriteLine("    Windows reports no capture device at all.");
            return 1;
        }

        var best = (Index: -1, Peak: 0, Name: string.Empty);

        // -1 first, deliberately: it is what the shipped default does, so its row is the one
        // that explains the user's experience rather than a hypothetical.
        for (int device = -1; device < count; device++)
        {
            string name = device < 0
                ? "WAVE_MAPPER (whatever Windows calls default)"
                : Capabilities(device);

            (int peak, string? problem) = Measure(device, seconds);

            if (problem is not null)
            {
                Console.WriteLine($"    [{device,2}] {name,-46} {problem}");
                continue;
            }

            string verdict = peak switch
            {
                0 => "SILENT - nothing at all",
                < 3 => "almost silent - room noise only",
                < 15 => "quiet - speech would be hard to recognise",
                _ => "hears you",
            };

            Console.WriteLine($"    [{device,2}] {name,-46} peak {peak,3}/100  {verdict}");

            if (device >= 0 && peak > best.Peak)
                best = (device, peak, name);
        }

        Console.WriteLine();

        if (best.Index >= 0 && best.Peak >= 3)
        {
            Console.WriteLine($"Loudest was device {best.Index} ({best.Name}).");
            Console.WriteLine($"To use it for dictation, put this in config.yaml:");
            Console.WriteLine($"    voice:");
            Console.WriteLine($"      deviceIndex: {best.Index}");
        }
        else
        {
            // Not dressed up as a result. Nobody may have been speaking, and saying "no
            // microphone works" on the strength of a silent room would be wrong.
            Console.WriteLine("No device showed a usable level. Either nothing was spoken while");
            Console.WriteLine("this ran, or the devices are muted in Windows. Both are worth");
            Console.WriteLine("checking before changing anything.");
        }

        return 0;
    }

    private static string Describe(MMDeviceEnumerator enumerator, Role role)
    {
        try
        {
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);

            return device.FriendlyName;
        }
        catch (Exception)
        {
            // A machine with no capture endpoint at all throws here, which is a state and
            // not a fault.
            return "none";
        }
    }

    private static string Capabilities(int device)
    {
        try
        {
            return WaveInEvent.GetCapabilities(device).ProductName;
        }
        catch (Exception)
        {
            return $"device {device}";
        }
    }

    /// <summary>
    /// Open one device and report the loudest sample it delivers.
    ///
    /// Raw, with no gain applied. The point is what the hardware hands over, and running it
    /// through the automatic gain would show every working device at roughly the same level
    /// while hiding the one measurement that matters here.
    /// </summary>
    private static (int Peak, string? Problem) Measure(int device, int seconds)
    {
        WaveInEvent? capture = null;
        int peak = 0;

        try
        {
            capture = new WaveInEvent
            {
                DeviceNumber = device,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100,
            };

            capture.DataAvailable += (_, e) =>
            {
                for (int at = 0; at + 1 < e.BytesRecorded; at += 2)
                {
                    int sample = Math.Abs((short)(e.Buffer[at] | (e.Buffer[at + 1] << 8)));

                    if (sample > peak)
                        peak = sample;
                }
            };

            capture.StartRecording();
            Thread.Sleep(seconds * 1000);
            capture.StopRecording();

            // A moment for the last buffer to arrive; stopping is asynchronous and reading
            // the peak immediately can miss the loudest part of what was just said.
            Thread.Sleep(200);

            return (Math.Min(100, peak * 100 / 32768), null);
        }
        catch (Exception ex)
        {
            return (0, ex.Message.Length > 40 ? ex.Message[..40] : ex.Message);
        }
        finally
        {
            capture?.Dispose();
        }
    }
}
