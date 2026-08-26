namespace Shellvis.Core.Voice;

/// <summary>
/// Brings quiet microphone input up to a level the recogniser can work with.
///
/// <b>Why this is here and not in the Windows mixer.</b> Raising the device level in Windows
/// would be the "real" fix and is the wrong thing to do: that setting belongs to the user
/// and is shared with every other application, so an agent that turns it up has changed how
/// Teams sounds to the people the user talks to. Gain applied inside this pipeline affects
/// only what the recogniser hears.
///
/// <b>Why automatic rather than a fixed multiplier.</b> A fixed number is right for exactly
/// one microphone at one distance. Headsets differ by more than an order of magnitude, and
/// the same headset differs between someone leaning in and someone sitting back. A
/// multiplier that suits a quiet one clips a loud one, and clipped speech recognises worse
/// than quiet speech.
///
/// <b>What keeps it from making things worse.</b> The gain is derived from the loudest
/// thing heard SO FAR in this utterance, which makes it monotonically non-increasing: it
/// settles within the first syllables and then holds. Two properties fall out of that, and
/// both matter to a recogniser rather than to a listener:
///
/// <list type="bullet">
/// <item>It cannot clip. The multiplier is computed from a peak that already includes the
/// buffer it is about to scale, so that buffer's own loudest sample lands on the target.</item>
/// <item>It cannot modulate. An acoustic model is trained on speech whose loudness moves
/// the way a voice moves; a gain stage that chases the level between syllables changes the
/// envelope of every word, which is a distortion the model has never seen.</item>
/// </list>
///
/// The first revision did chase it, with a fast-down/slow-up smoothing borrowed from audio
/// levelling. That is right for something a person listens to and wrong here -- pleasant
/// loudness and recognisable speech are not the same goal. A silent room is still not
/// amplified, because a buffer below the noise floor does not move the estimate at all.
/// </summary>
internal sealed class CaptureGain
{
    /// <summary>
    /// Where the loudest sample should end up, as a fraction of full scale.
    ///
    /// Not 1.0. Speech is peaky, and aiming at the ceiling means the next syllable that is
    /// louder than the last one clips.
    /// </summary>
    private const double Target = 0.6;

    /// <summary>
    /// The most it will amplify.
    ///
    /// A bound rather than "whatever it takes": beyond this the noise floor comes up with
    /// the speech and nothing is gained. If a microphone needs more than twelve times, the
    /// problem is the device level or the distance, and the diagnostic should say so rather
    /// than being hidden by an ever-growing multiplier.
    /// </summary>
    private const double MaxGain = 12.0;

    /// <summary>
    /// Below this fraction of full scale a buffer is treated as room noise and does not
    /// move the gain. About -46 dBFS: quieter than any speech worth recognising.
    /// </summary>
    private const double NoiseFloor = 0.005;

    private double _gain = 1.0;

    /// <summary>The loudest fraction of full scale seen this session.</summary>
    private double _loudest;

    /// <summary>The multiplier currently in use, for the diagnostic line.</summary>
    public double Current => _gain;

    /// <summary>Whether it has had to amplify at all, so the console can mention it.</summary>
    public bool IsBoosting => _gain > 1.5;

    /// <summary>
    /// Amplify a 16-bit PCM buffer in place and return its peak AFTER the gain, 0 to 100.
    ///
    /// In place because this sits on the capture callback: allocating a second buffer per
    /// 100 ms for the life of a dictation session is avoidable garbage, and the caller has
    /// no further use for the original samples.
    /// </summary>
    public int Apply(byte[] buffer, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        int samples = Math.Min(count, buffer.Length) / 2;

        if (samples == 0)
            return 0;

        // The peak BEFORE gain decides what the gain should become; the peak after it is
        // what the level meter shows.
        int rawPeak = 0;

        for (int i = 0; i < samples; i++)
        {
            int sample = Math.Abs((short)(buffer[i * 2] | (buffer[(i * 2) + 1] << 8)));

            if (sample > rawPeak)
                rawPeak = sample;
        }

        double fraction = rawPeak / 32768.0;

        if (fraction > NoiseFloor)
        {
            // The loudest thing heard so far, this buffer included. Taking the maximum
            // rather than smoothing towards it is what makes the multiplier settle and stay
            // settled: it can only ever be revised downwards, and only when the user turns
            // out to be louder than they have been.
            _loudest = Math.Max(_loudest, fraction);
            _gain = Math.Clamp(Target / _loudest, 1.0, MaxGain);
        }

        if (_gain <= 1.001)
            return Math.Min(100, rawPeak * 100 / 32768);

        int peak = 0;

        for (int i = 0; i < samples; i++)
        {
            int at = i * 2;
            short sample = (short)(buffer[at] | (buffer[at + 1] << 8));

            // Saturating, not wrapping. An int16 overflow turns a loud sample into a loud
            // sample of the opposite sign, which is a click -- and a track full of clicks
            // recognises worse than one that is merely quiet.
            int scaled = (int)Math.Round(sample * _gain);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);

            buffer[at] = (byte)(scaled & 0xFF);
            buffer[at + 1] = (byte)((scaled >> 8) & 0xFF);

            int magnitude = Math.Abs(scaled);

            if (magnitude > peak)
                peak = magnitude;
        }

        return Math.Min(100, peak * 100 / 32768);
    }
}
