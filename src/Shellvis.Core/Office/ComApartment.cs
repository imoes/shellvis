using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Shellvis.Core.Office;

/// <summary>
/// A dedicated single-threaded apartment for every COM call in the process.
///
/// Office automation is not thread-agnostic. The object model is STA, and calling it
/// from arbitrary thread-pool threads produces failures that look random: RPC_E_*
/// errors, calls that succeed once and hang the next time, and -- the signature
/// symptom -- WINWORD.EXE or EXCEL.EXE surviving after the app closes, because the
/// reference that would have released them was held on a thread that has since died.
///
/// That failure mode is not theoretical here: a verification script in an earlier step
/// left EXCEL and POWERPNT running precisely because it threw before its Quit call.
///
/// So all COM work funnels through one long-lived STA thread. Every call is queued,
/// executed there, and its result marshalled back. Callers get a Task and never see
/// the thread.
/// </summary>
public sealed class ComApartment : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public ComApartment(string name = "Shellvis COM")
    {
        _thread = new Thread(Pump)
        {
            Name = name,
            IsBackground = true,
        };

        // The whole point. Without STA, Office calls fail in ways that are extremely
        // hard to attribute to threading.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Run work on the apartment thread and return its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Cancellation can only prevent a call from starting. A COM call already in
        // flight cannot be interrupted -- the apartment is single-threaded, so
        // abandoning it would wedge every later call behind it.
        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    /// <summary>Run work on the apartment thread with no result.</summary>
    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default) =>
        InvokeAsync<bool>(() => { work(); return true; }, cancellationToken);

    private void Pump()
    {
        // GetConsumingEnumerable blocks until work arrives and ends when the queue is
        // marked complete, so the thread costs nothing while idle.
        foreach (Action work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception)
            {
                // Already surfaced through the TaskCompletionSource. Swallowing here
                // is what keeps one bad call from taking the apartment down and
                // stranding every queued caller.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _queue.CompleteAdding();

        // Wait for the queue to drain so pending releases actually run. This is the
        // difference between a clean exit and a leftover EXCEL.EXE, so a bounded wait
        // is worth it -- but bounded, because a wedged COM call must not stop the
        // application from closing.
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
            return;

        _queue.Dispose();
    }
}

/// <summary>
/// Helpers for handling COM references without leaking them.
/// </summary>
public static partial class Com
{
    /// <summary>
    /// Release a COM reference now rather than whenever the finalizer happens to run.
    ///
    /// The garbage collector does eventually release RCWs, but "eventually" means the
    /// Office process stays alive in the meantime -- and if the app exits first, it
    /// stays alive forever. Explicit release is the only reliable option.
    /// </summary>
    public static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
            return;

        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch (ArgumentException)
        {
            // Already released, or never actually a COM object.
        }
        catch (InvalidComObjectException)
        {
        }
    }

    /// <summary>Release several references, innermost first.</summary>
    public static void ReleaseAll(params object?[] comObjects)
    {
        // Reverse order: a child reference should go before its parent, mirroring how
        // the references were acquired.
        for (int i = comObjects.Length - 1; i >= 0; i--)
            Release(comObjects[i]);
    }

    /// <summary>
    /// Attach to a running application, or start one.
    ///
    /// Outlook, Word, Excel and PowerPoint are all registered single-instance, so
    /// creating the ProgID attaches to the running copy when there is one. That
    /// matters for Outlook in particular: a second instance would not have the user's
    /// profile open and would prompt for one.
    /// </summary>
    public static dynamic GetOrCreate(string progId)
    {
        Type? type = Type.GetTypeFromProgID(progId, throwOnError: false);

        if (type is null)
        {
            throw new InvalidOperationException(
                $"{progId} is not registered on this machine. The application is probably not installed.");
        }

        object? instance = Activator.CreateInstance(type);

        return instance
            ?? throw new InvalidOperationException($"{progId} could not be started.");
    }

    /// <summary>
    /// Attach to a running application, or start one, saying which happened.
    ///
    /// <see cref="GetOrCreate"/> does both and does not distinguish, which turned out to matter
    /// for Outlook. Asking "what appointments do I have today" with Outlook closed launched the
    /// user's mail client -- silently, and taking tens of seconds while it opened a profile and
    /// began synchronising. That is a visible thing to do to somebody's machine in answer to a
    /// read-only question, and doing it without saying so is the part that was wrong.
    ///
    /// The flag lets the caller say it. It does not decide anything here: whether starting the
    /// application is acceptable is the caller's judgement, and it differs by application --
    /// starting Word to render a PDF is routine, starting Outlook is not.
    /// </summary>
    public static dynamic GetOrStart(string progId, out bool started)
    {
        dynamic? running = TryGetActive(progId);

        if (running is not null)
        {
            started = false;
            return running;
        }

        started = true;
        return GetOrCreate(progId);
    }

    /// <summary>Whether a ProgID is registered at all, for capability checks.</summary>
    public static bool IsAvailable(string progId) =>
        Type.GetTypeFromProgID(progId, throwOnError: false) is not null;

    /// <summary>
    /// Attach to an ALREADY RUNNING application, or return null.
    ///
    /// This is not the same as <see cref="GetOrCreate"/> and the difference matters. The
    /// comment there is right about Outlook, which is registered single-instance, and
    /// wrong about Word, Excel and PowerPoint: for those, CreateInstance starts a NEW
    /// invisible copy rather than attaching to the one the user is looking at. Driving
    /// that copy would read an empty document and, if teardown ever failed, leave a
    /// WINWORD.EXE nobody can see.
    ///
    /// Attaching means the Running Object Table, and there the framework has a gap:
    /// <c>Marshal.GetActiveObject</c> does not exist on modern .NET -- it was left behind
    /// in .NET Framework. So the two oleaut32 entry points are declared here by hand.
    /// </summary>
    public static dynamic? TryGetActive(string progId)
    {
        try
        {
            int clsidResult = CLSIDFromProgID(progId, out Guid clsid);

            if (clsidResult != 0)
                return null;

            int activeResult = GetActiveObject(ref clsid, IntPtr.Zero, out object instance);

            // MK_E_UNAVAILABLE (0x800401E3) is the ordinary "nothing is running" answer,
            // not a fault, so every non-zero result is simply "no".
            return activeResult == 0 ? instance : null;
        }
        catch (Exception)
        {
            // A COM subsystem that cannot answer is indistinguishable from nothing
            // running, as far as the caller can act on it.
            return null;
        }
    }

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

    // DllImport, not LibraryImport: the source generator refuses MarshalAs(IUnknown),
    // and the out parameter here has to be marshalled as an IUnknown because that is
    // what GetActiveObject hands back. SYSLIB1052 says so explicitly and names DllImport
    // as the way out.
    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid rclsid,
        IntPtr pvReserved,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}
