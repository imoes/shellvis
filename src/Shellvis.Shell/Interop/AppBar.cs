using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Shellvis.Core.Desktop;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shellvis.Shell.Interop;

/// <summary>
/// Registering the pill as an application desktop toolbar, so the shell tells it where to be.
///
/// <b>Why register at all, when the window is placed by hand anyway.</b> Registration is not
/// about placement here -- Shellvis positions itself on the taskbar strip and reserves no
/// screen area at all. Registration is about the notification stream: an appbar is on the
/// shell's internal list, and being on that list is the only documented way to be told that a
/// full-screen application has taken the screen, that the taskbar has moved, or that its state
/// has changed. Without it the only option is to poll and guess, which is what this replaced.
///
/// It does claim an EDGE, with an empty rectangle, and <see cref="ClaimEdgeWithoutSpace"/>
/// explains why: an appbar without one is on the list and gets nothing.
///
/// The decision itself is in <see cref="TaskbarBand"/>, with no Win32 in it. This class is the
/// wiring: a subclass to receive the callback message, the two notifications the documentation
/// says an appbar <i>must</i> send back, and the <c>SetWindowPos</c> that carries out the
/// decision.
/// </summary>
internal sealed class AppBar : IDisposable
{
    /// <summary>
    /// Our own message for the appbar callback.
    ///
    /// WM_APP + 2: the tray icon already took WM_APP + 1 on this same window, and comctl32
    /// supports several subclasses on one window distinguished by id, which is what makes
    /// stacking them safe.
    /// </summary>
    private const uint CallbackMessage = PInvoke.WM_APP + 2;

    /// <summary>A third subclass on the pill, after the hotkey one and the tray one.</summary>
    private static readonly nuint SubclassId = 0xC0F2;

    private static readonly nint HwndTopmost = -1;
    private static readonly nint HwndBottom = 1;

    // The appbar messages, from shellapi.h. Written out because the Win32 metadata package
    // projects SHAppBarMessage and APPBARDATA but not these values, and only the five that are
    // actually sent -- ABM_QUERYPOS is not among them, because its whole purpose is to
    // negotiate reserved screen area and nothing here reserves any.
    private const uint AbmNew = 0x0;
    private const uint AbmRemove = 0x1;
    private const uint AbmSetPos = 0x3;
    private const uint AbmActivate = 0x6;
    private const uint AbmWindowPosChanged = 0x9;

    /// <summary>ABE_BOTTOM: the edge the bar is associated with.</summary>
    private const uint AbeBottom = 0x3;

    /// <summary>
    /// Live bars by window handle.
    ///
    /// The subclass procedure is an unmanaged function pointer and can capture nothing, so the
    /// instance is found by handle -- the same constraint and the same answer as the tray icon
    /// and the hotkey listener.
    /// </summary>
    private static readonly ConcurrentDictionary<nint, AppBar> Instances = new();

    private readonly HWND _hwnd;
    private readonly TaskbarBand _band = new();

    private bool _registered;
    private bool _subclassed;

    /// <summary>Raised when the taskbar has moved, resized or come and gone.</summary>
    /// <remarks>
    /// The docked bar sits on the taskbar strip, so this is when it has to be placed again.
    /// Previously that only happened because the position was re-checked on a timer.
    /// </remarks>
    public event Action? TaskbarMoved;

    /// <summary>Raised when the bar's place in the z-order changed, with the reason to show.</summary>
    public event Action<string?>? BandChanged;

    public AppBar(nint windowHandle)
    {
        _hwnd = new HWND(windowHandle);
        Instances[windowHandle] = this;
    }

    /// <summary>Where the bar currently belongs.</summary>
    public BandPosition Position => _band.Position;

    /// <summary>
    /// Join the shell's list of appbars.
    ///
    /// Returns false rather than throwing, on the same reasoning as the tray icon: an
    /// unregistered bar still works, it merely goes back to being uninformed, and Explorer
    /// does refuse things while it is restarting.
    /// </summary>
    public unsafe bool TryRegister()
    {
        if (!_subclassed)
        {
            _subclassed = PInvoke.SetWindowSubclass(_hwnd, &Subclass, SubclassId, 0);

            if (!_subclassed)
                return false;
        }

        var data = new APPBARDATA
        {
            cbSize = (uint)sizeof(APPBARDATA),
            hWnd = _hwnd,
            uCallbackMessage = CallbackMessage,
        };

        _registered = PInvoke.SHAppBarMessage(AbmNew, ref data) != 0;

        if (_registered)
        {
            ClaimEdgeWithoutSpace();
            ApplyPosition();
        }

        return _registered;
    }

    /// <summary>
    /// Put the window where the current state says it goes.
    ///
    /// Public because the window calls it when it is activated: raising Shellvis over a
    /// full-screen application is a thing the user asked for by pressing the hotkey, and the
    /// bar should not sink back until the state actually changes.
    ///
    /// <c>SWP_NOACTIVATE</c> is not decoration. The whole point of moving in the z-order is to
    /// stay out of the way of whatever has the focus, and taking the focus in order to do it
    /// would be worse than not moving at all.
    /// </summary>
    public void ApplyPosition() => Place(_band.Position == BandPosition.Bottom ? HwndBottom : HwndTopmost);

    /// <summary>
    /// Re-assert the topmost position without changing anything else.
    ///
    /// Needed only for the docked bar, and only because two topmost windows are competing for
    /// the same pixels: being in the topmost band is not a position within it, so anything that
    /// raises Shell_TrayWnd -- and the shell raises it readily -- leaves a bar that occupies the
    /// taskbar's own strip completely hidden. Not called while a full-screen application has
    /// the screen, because then being underneath is the correct answer.
    /// </summary>
    public void HoldAbove()
    {
        if (_band.Position == BandPosition.Topmost)
            Place(HwndTopmost);
    }

    /// <summary>Tell the shell the bar was activated, which it needs to order autohide bars.</summary>
    public unsafe void NotifyActivated(bool active)
    {
        if (!_registered)
            return;

        var data = new APPBARDATA
        {
            cbSize = (uint)sizeof(APPBARDATA),
            hWnd = _hwnd,
            lParam = active ? 1 : 0,
        };

        PInvoke.SHAppBarMessage(AbmActivate, ref data);
    }

    public unsafe void Dispose()
    {
        Instances.TryRemove((nint)_hwnd.Value, out _);

        if (_registered)
        {
            var data = new APPBARDATA
            {
                cbSize = (uint)sizeof(APPBARDATA),
                hWnd = _hwnd,
            };

            PInvoke.SHAppBarMessage(AbmRemove, ref data);
            _registered = false;
        }

        if (_subclassed)
        {
            PInvoke.RemoveWindowSubclass(_hwnd, &Subclass, SubclassId);
            _subclassed = false;
        }
    }

    /// <summary>
    /// Take an edge, and none of the screen that goes with it.
    ///
    /// <b>Measured, not assumed.</b> Registering with <c>ABM_NEW</c> alone is enough for the
    /// shell to accept the bar onto its list -- the call succeeds -- and not enough for it to
    /// send a single notification. Verified directly: with a borderless window covering the
    /// whole monitor, Shell_TrayWnd hid itself, so the shell plainly knew a full-screen
    /// application had the screen, and neither <c>ABN_FULLSCREENAPP</c> nor
    /// <c>ABN_POSCHANGED</c> arrived. An appbar with no edge is on the list and off the
    /// distribution.
    ///
    /// So an edge is claimed with an EMPTY rectangle. The shell reserves the area a bar asks
    /// for, and a rectangle of zero height asks for nothing: no other application loses a
    /// pixel, nobody's maximised window shrinks, and the bar is nonetheless a first-class
    /// appbar as far as the notifications are concerned. That is the whole reason this is here
    /// rather than the usual query-adjust-set dance from the documentation, which exists to
    /// reserve space -- exactly what must not happen.
    /// </summary>
    private unsafe void ClaimEdgeWithoutSpace()
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)sizeof(APPBARDATA),
            hWnd = _hwnd,

            // The bottom edge, because that is where the bar docks and where the taskbar is on
            // an ordinary desktop. The edge decides which other appbars this one is ordered
            // against; it does not move the window, which Shellvis places itself.
            uEdge = AbeBottom,
            rc = default,
        };

        PInvoke.SHAppBarMessage(AbmSetPos, ref data);
    }

    private void Place(nint insertAfter) =>
        PInvoke.SetWindowPos(
            _hwnd,
            (HWND)insertAfter,
            0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    /// <summary>Tell the shell the bar moved. The documentation says an appbar MUST send this.</summary>
    private unsafe void NotifyMoved()
    {
        if (!_registered)
            return;

        var data = new APPBARDATA
        {
            cbSize = (uint)sizeof(APPBARDATA),
            hWnd = _hwnd,
        };

        PInvoke.SHAppBarMessage(AbmWindowPosChanged, ref data);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT Subclass(
        HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint idSubclass, nuint refData)
    {
        if (!Instances.TryGetValue((nint)hwnd.Value, out AppBar? bar))
            return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);

        if (message == CallbackMessage)
        {
            // The notification code is in wParam; lParam is the notification's own argument.
            uint notification = (uint)(nuint)wParam.Value;
            bool flag = lParam.Value != 0;

            if (bar._band.Apply(notification, flag))
            {
                bar.ApplyPosition();
                bar.BandChanged?.Invoke(bar._band.Moved);
            }

            if (notification == TaskbarBand.PositionChanged)
                bar.TaskbarMoved?.Invoke();

            return new LRESULT(0);
        }

        // The two the appbar owes the shell in return. Sent after the default handling, so the
        // window has already been given its new position when the shell is told about it.
        if (message == PInvoke.WM_WINDOWPOSCHANGED)
        {
            LRESULT result = PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
            bar.NotifyMoved();
            return result;
        }

        if (message == PInvoke.WM_ACTIVATE)
        {
            LRESULT result = PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
            bar.NotifyActivated((uint)(nuint)wParam.Value != 0);
            return result;
        }

        return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
    }
}
