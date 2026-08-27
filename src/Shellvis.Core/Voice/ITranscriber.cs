namespace Shellvis.Core.Voice;

/// <summary>
/// Something that turns a finished recording into text.
///
/// One interface for the local model and the cloud services, so <see cref="DictationEngine"/>
/// captures audio in exactly one way and the choice of recogniser cannot change the capture
/// path. That is not a hypothetical concern here: the last time dictation had two capture paths
/// the default one silently skipped the gain and the audio bridge for a whole release.
/// </summary>
public interface ITranscriber : IDisposable
{
    /// <summary>Whether it is ready to be used.</summary>
    bool IsLoaded { get; }

    /// <summary>What to call it in the transcript, e.g. "Whisper (medium)" or "Azure Speech".</summary>
    string Description { get; }

    /// <summary>
    /// Whether the recording leaves this machine.
    ///
    /// Part of the interface rather than a detail of the implementation, because the UI has to
    /// say it. A local recogniser and a cloud service are not interchangeable from the user's
    /// point of view even when their text is identical, and "recognition runs on this machine"
    /// must not be printed by a code path that is sending audio to a service.
    /// </summary>
    bool IsRemote { get; }

    /// <summary>Transcribe 16 kHz mono 16-bit PCM.</summary>
    /// <param name="language">A culture name such as de-DE.</param>
    Task<WhisperResult> TranscribeAsync(
        ReadOnlyMemory<byte> pcm, string? language, CancellationToken cancel = default);
}
