using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Shellvis.Shell.Interop;

/// <summary>
/// When the session is locked and unlocked.
///
/// <b>Why the window and not SystemEvents.</b> <c>SystemEvents.SessionSwitch</c> needs a
/// message window of its own on a thread of its own, and this application already has a
/// window with a message loop. Windows sends <c>WM_WTSSESSION_CHANGE</c> to any window that
/// asks for it, so the bar's window asks, through the same comctl32 subclass mechanism the
/// hotkeys use -- a second subclass id on the same window, which comctl32 supports.
///
/// Holding alerts while locked needs nothing from here: Windows already reports a locked
/// session as "not present", and the quiet gate holds everything for that. What this adds is
/// the moment of coming back.
/// </summary>
internal sealed class SessionWatch : IDisposable
{
    private const uint NotifyForThisSession = 0;
    private const nuint WtsSessionLock = 0x7;
    private const nuint WtsSessionUnlock = 0x8;

    private static readonly nuint SubclassId = 0x5E55;

    private static readonly ConcurrentDictionary<nint, SessionWatch> Watches = new();

    private readonly HWND _hwnd;
    private bool _subclassed;
    private bool _registered;

    /// <summary>Raised on the UI thread when the session is locked.</summary>
    public event Action? Locked;

    /// <summary>Raised on the UI thread when the session is unlocked.</summary>
    public event Action? Unlocked;

    public SessionWatch(nint windowHandle)
    {
        _hwnd = new HWND(windowHandle);
        Watches[windowHandle] = this;
    }

    /// <summary>Start listening. False when Windows refused, which costs the briefing and nothing else.</summary>
    public unsafe bool Start()
    {
        _subclassed = PInvoke.SetWindowSubclass(_hwnd, &Subclass, SubclassId, 0);

        if (!_subclassed)
            return false;

        _registered = PInvoke.WTSRegisterSessionNotification(_hwnd, NotifyForThisSession);
        return _registered;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe LRESULT Subclass(
        HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint idSubclass, nuint refData)
    {
        if (message == PInvoke.WM_WTSSESSION_CHANGE
            && Watches.TryGetValue((nint)hwnd.Value, out SessionWatch? watch))
        {
            if (wParam.Value == WtsSessionLock)
                watch.Locked?.Invoke();
            else if (wParam.Value == WtsSessionUnlock)
                watch.Unlocked?.Invoke();
        }

        return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public unsafe void Dispose()
    {
        if (_registered)
        {
            PInvoke.WTSUnRegisterSessionNotification(_hwnd);
            _registered = false;
        }

        if (_subclassed)
        {
            PInvoke.RemoveWindowSubclass(_hwnd, &Subclass, SubclassId);
            _subclassed = false;
        }

        Watches.TryRemove((nint)_hwnd.Value, out _);
    }
}
