using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shellvis.Shell.Interop;

/// <summary>
/// A system-wide hotkey that brings the pill to the front.
///
/// This is not a convenience. Windows refuses SetForegroundWindow from any process that
/// does not already own the foreground window, so nothing outside the app can raise it
/// -- a fact this project ran into while trying to drive its own window from a test
/// script. A registered hotkey is the mechanism that grants the app itself the right to
/// come forward, which makes it the only reliable way to reach a pill that is
/// always-on-top but not focused.
///
/// WinUI exposes no window procedure, so the window is subclassed through comctl32 to
/// see WM_HOTKEY. The subclass callback has to be an unmanaged function pointer, which
/// means it cannot capture anything: instances are therefore looked up from a static
/// map keyed by window handle.
/// </summary>
internal sealed class HotkeyListener : IDisposable
{
    /// <summary>The hotkey that raises the window. Any value works; it just has to be ours.</summary>
    public const int RaiseId = 0xC0DE;

    /// <summary>The hotkey that starts and stops dictation.</summary>
    public const int DictateId = 0xC0DF;

    /// <summary>Subclass identity, so the right subclass is removed on teardown.</summary>
    private static readonly nuint SubclassId = 0xC0DE;

    /// <summary>
    /// Live listeners by window handle.
    ///
    /// The unmanaged callback receives only the handle, so this is how it finds the
    /// object to notify. Static state is unavoidable here, not a shortcut.
    /// </summary>
    private static readonly ConcurrentDictionary<nint, HotkeyListener> Listeners = new();

    private readonly HWND _hwnd;
    private bool _registered;
    private bool _subclassed;

    /// <summary>
    /// Raised on the UI thread with the id of the hotkey that fired.
    ///
    /// One event with an id rather than one event per hotkey: the window subclass and the
    /// static instance map are the delicate part here, and duplicating them per key would
    /// mean two subclasses on one window.
    /// </summary>
    public event Action<int>? Pressed;

    /// <summary>Which ids were successfully claimed.</summary>
    private readonly HashSet<int> _claimed = [];

    public HotkeyListener(nint windowHandle)
    {
        _hwnd = new HWND(windowHandle);
        Listeners[windowHandle] = this;
    }

    /// <summary>
    /// Register the hotkey and start listening.
    ///
    /// Returns false rather than throwing when the combination is already taken by
    /// another application, which is common and entirely the user's business to resolve.
    /// A hotkey that could not be claimed is a diminished experience, not a broken app.
    /// </summary>
    public unsafe bool TryRegister(int id, HOT_KEY_MODIFIERS modifiers, uint virtualKey)
    {
        if (!_subclassed)
        {
            _subclassed = PInvoke.SetWindowSubclass(_hwnd, &Subclass, SubclassId, 0);

            if (!_subclassed)
                return false;
        }

        // MOD_NOREPEAT: holding the combination fires once rather than autorepeating,
        // which would otherwise queue dozens of raise requests.
        bool ok = PInvoke.RegisterHotKey(
            _hwnd, id, modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT, virtualKey);

        if (ok)
            _claimed.Add(id);

        _registered |= ok;

        return ok;
    }

    /// <summary>
    /// Bring the window forward.
    ///
    /// Called from the hotkey handler, which is the one context where the call is
    /// permitted: handling a hotkey message grants the process the right to set the
    /// foreground window.
    /// </summary>
    public void BringToFront()
    {
        if (PInvoke.IsIconic(_hwnd))
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOW);
        PInvoke.SetForegroundWindow(_hwnd);
    }

    /// <summary>
    /// The subclass procedure. Static and unmanaged, so it can hold no state.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe LRESULT Subclass(
        HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint idSubclass, nuint refData)
    {
        if (message == PInvoke.WM_HOTKEY)
        {
            int id = (int)wParam.Value;

            if (Listeners.TryGetValue((nint)hwnd.Value, out HotkeyListener? listener)
                && listener._claimed.Contains(id))
            {
                // Raising happens here rather than being posted onwards, because the
                // permission to set the foreground window applies to the thread that is
                // handling this message and is gone by the next one.
                // Raising happens for the raise hotkey only; dictation should not steal
                // the foreground from whatever the user is looking at.
                if (id == RaiseId)
                    listener.BringToFront();

                listener.Pressed?.Invoke(id);
            }
            else
            {
                return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
            }

            return new LRESULT(0);
        }

        return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public unsafe void Dispose()
    {
        foreach (int id in _claimed)
            PInvoke.UnregisterHotKey(_hwnd, id);

        _claimed.Clear();
        _registered = false;

        if (_subclassed)
        {
            // Leaving the subclass in place would call into a collected delegate the
            // next time any message arrived.
            PInvoke.RemoveWindowSubclass(_hwnd, &Subclass, SubclassId);
            _subclassed = false;
        }

        Listeners.TryRemove((nint)_hwnd.Value, out _);
    }
}
