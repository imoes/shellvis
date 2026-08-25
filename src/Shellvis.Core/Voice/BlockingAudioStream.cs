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

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (_current is not null && _offset < _current.Length)
            {
                int take = Math.Min(count, _current.Length - _offset);
                Array.Copy(_current, _offset, buffer, offset, take);
                _offset += take;

                if (_offset >= _current.Length)
                    _current = null;

                _delivered += take;
                return take;
            }

            lock (_gate)
            {
                if (_chunks.Count > 0)
                {
                    _current = _chunks.Dequeue();
                    _offset = 0;
                    continue;
                }

                if (_closed)
                    return 0;
            }

            // Timed rather than indefinite: if capture dies without calling Finish, an
            // indefinite wait would wedge the engine's reader thread for the life of the
            // process. Returning to the loop re-checks the closed flag.
            _available.Wait(TimeSpan.FromMilliseconds(500));

            lock (_gate)
            {
                if (_chunks.Count == 0 && _closed)
                    return 0;
            }
        }
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
    public override long Seek(long offset, SeekOrigin origin) => _delivered;

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
