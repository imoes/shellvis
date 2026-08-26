namespace Shellvis.Core.Voice;

/// <summary>
/// A pipe between an audio capture callback and a reader that expects a blocking
/// <see cref="Stream"/>.
///
/// This exists because of a mismatch between two APIs that otherwise fit.
/// <c>SpeechRecognitionEngine.SetInputToAudioStream</c> wants a stream it can read from
/// and expects <c>Read</c> to BLOCK until samples arrive -- it treats a zero-length read
/// as end of audio and stops recognising. NAudio, on the other hand, pushes buffers at
/// you from its own thread. So something has to turn push into blocking pull, and
/// <see cref="System.IO.Pipelines"/> or a plain MemoryStream both get this wrong: the
/// first has no Stream with the right blocking semantics for this consumer, the second
/// returns 0 the moment it runs dry and the engine gives up after the first pause between
/// words.
/// </summary>
internal sealed class BlockingAudioStream : Stream
{
    private readonly Queue<byte[]> _chunks = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _available = new(0);

    private byte[]? _current;
    private int _offset;
    private bool _closed;

    /// <summary>
    /// How much audio may queue up before the oldest is dropped.
    ///
    /// Bounded on purpose. If recognition stalls, an unbounded queue would grow for as
    /// long as the microphone is open -- and stale audio is worthless anyway: recognising
    /// what was said thirty seconds ago is not useful, so dropping the oldest is the right
    /// loss.
    /// </summary>
    private const int MaxQueuedChunks = 200;

    /// <summary>
    /// Push captured audio in. Called from NAudio's thread.
    ///
    /// Named Push, not Write: Stream.Write(ReadOnlySpan) already exists and hiding it
    /// would mean a caller with a Stream reference silently reaching the wrong method.
    /// </summary>
    public void Push(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        byte[] copy = data.ToArray();

        lock (_gate)
        {
            if (_closed)
                return;

            if (_chunks.Count >= MaxQueuedChunks)
                _chunks.Dequeue();

            _chunks.Enqueue(copy);
        }

        _available.Release();
    }

    /// <summary>Signal end of audio, so a blocked reader returns 0 rather than hanging.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_closed)
                return;

            _closed = true;
        }

        // Release generously: the reader may be blocked, and a single release would only
        // wake one waiter while leaving a later read blocked forever.
        _available.Release(4);
    }

    /// <summary>
    /// How the consumer has actually behaved: reads attempted, bytes handed over, seeks.
    ///
    /// Counted because the difference between "the engine never read" and "the engine read
    /// and could not decode" is the whole diagnosis, and from outside the two look identical
    /// -- silence either way.
    /// </summary>
    internal int ReadCalls { get; private set; }

    internal long BytesRead => _delivered;

    internal int SeekCalls { get; private set; }

    /// <summary>
    /// Hand over exactly <paramref name="count"/> bytes, or fewer only at end of audio.
    ///
    /// FILLING THE BUFFER IS THE WHOLE POINT, and returning a partial read was the defect
    /// that made dictation silent. A Stream is allowed to return fewer bytes than asked for
    /// and well-behaved consumers loop -- SpeechRecognitionEngine does not. Measured: it
    /// issued one read, was handed 3200 bytes (one capture buffer) out of 87522 available,
    /// read once more and stopped. It treats a short read as end of audio.
    ///
    /// That is also why SetInputToWaveFile always worked and this did not: a FileStream
    /// fills the buffer, so the engine never saw a short read and the difference between
    /// the two paths was invisible from outside -- audio arriving at the level meter, no
    /// recognition, and no rejection events either, because an engine that has decided the
    /// audio ended is not rejecting anything.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadCalls++;

        int filled = 0;

        while (filled < count)
        {
            if (_current is not null && _offset < _current.Length)
            {
                int take = Math.Min(count - filled, _current.Length - _offset);
                Array.Copy(_current, _offset, buffer, offset + filled, take);
                _offset += take;
                filled += take;
                _delivered += take;

                if (_offset >= _current.Length)
                    _current = null;

                continue;
            }

            bool ended;

            lock (_gate)
            {
                if (_chunks.Count > 0)
                {
                    _current = _chunks.Dequeue();
                    _offset = 0;
                    continue;
                }

                ended = _closed;
            }

            if (ended)
                break;

            // Timed rather than indefinite: if capture dies without calling Finish, an
            // indefinite wait would wedge the engine's reader thread for the life of the
            // process. Returning to the loop re-checks the closed flag.
            _available.Wait(TimeSpan.FromMilliseconds(500));
        }

        return filled;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    /// <summary>
    /// A length, and a position, even though this is a live stream.
    ///
    /// Both used to throw NotSupportedException, which is the textbook answer for a
    /// forward-only stream -- and it made SetInputToAudioStream fail outright with
    /// "Specified method is not supported." before a single sample was read. The engine
    /// queries these regardless of CanSeek. So it gets answers: an effectively endless
    /// length, because the microphone is open until someone stops it, and a position that
    /// counts what has been handed out.
    /// </summary>
    public override long Length => long.MaxValue;

    public override long Position
    {
        get => _delivered;

        // Ignored rather than throwing. Nothing can seek a microphone, but refusing the
        // assignment aborts the caller, and the caller here only sets it to zero at the
        // start.
        set { }
    }

    private long _delivered;

    public override void Flush()
    {
    }

    /// <summary>Reports where it is; cannot actually move.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        SeekCalls++;
        return _delivered;
    }

    public override void SetLength(long value)
    {
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Finish();
            _available.Dispose();
        }

        base.Dispose(disposing);
    }
}
