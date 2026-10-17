using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shellvis.Shell.Interop;

/// <summary>
/// A notification-area icon, so the pill has somewhere to live when it is not on screen.
///
/// Built on <c>Shell_NotifyIcon</c> directly rather than with H.NotifyIcon.WinUI, which
/// the plan named. Two reasons, and the second is the deciding one. First, everything
/// comparable in this window is already hand-done -- the clipping region, the DWM corner
/// hints, the hotkey -- so the machinery is familiar rather than novel. Second, the tray
/// callback is a window message, and this window already has a comctl32 subclass for
/// WM_HOTKEY: comctl32 explicitly supports several subclasses on one window distinguished
/// by id, which is what the id parameter is for. So the whole feature costs no dependency
/// and no new mechanism.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>Our own message for the tray callback. WM_APP is reserved for exactly this.</summary>
    private const uint CallbackMessage = PInvoke.WM_APP + 1;

    /// <summary>Identifies the icon within this window.</summary>
    private const uint IconId = 1;

    /// <summary>A second subclass, distinct from the hotkey one. That is what the id is for.</summary>
    private static readonly nuint SubclassId = 0xC0F0;

    private const uint CommandShow = 0xC101;
    private const uint CommandConsole = 0xC102;
    private const uint CommandDock = 0xC104;
    private const uint CommandExit = 0xC103;

    /// <summary>
    /// Live icons by window handle.
    ///
    /// The subclass procedure is an unmanaged function pointer and can capture nothing, so
    /// the instance is looked up by handle -- the same constraint, and the same solution,
    /// as in the hotkey listener.
    /// </summary>
    private static readonly ConcurrentDictionary<nint, TrayIcon> Instances = new();

    private readonly HWND _hwnd;
    private HICON _icon;
    private bool _added;
    private bool _subclassed;

    /// <summary>Raised when the user asks to see the window.</summary>
    public event Action? ShowRequested;

    /// <summary>Raised when the user asks to toggle the console.</summary>
    public event Action? ConsoleRequested;

    /// <summary>Raised when the user asks to dock to or undock from the taskbar.</summary>
    public event Action? DockRequested;

    /// <summary>Raised when the user asks to quit.</summary>
    public event Action? ExitRequested;

    public TrayIcon(nint windowHandle)
    {
        _hwnd = new HWND(windowHandle);
        Instances[windowHandle] = this;
    }

    /// <summary>
    /// Put the icon in the notification area.
    ///
    /// Returns false rather than throwing: a missing tray icon is a diminished experience,
    /// not a broken application, and Explorer does occasionally refuse an add while it is
    /// restarting.
    /// </summary>
    public unsafe bool TryAdd(string iconPath, string tooltip)
    {
        if (!_subclassed)
        {
            _subclassed = PInvoke.SetWindowSubclass(_hwnd, &Subclass, SubclassId, 0);

            if (!_subclassed)
                return false;
        }

        _icon = LoadIcon(iconPath);

        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_ICON
                | NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE
                | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };

        // szTip is a fixed inline buffer of 128 chars including the terminator. The shell
        // truncates anything longer silently, so it is truncated here instead, where the
        // limit is written down.
        ReadOnlySpan<char> tip = tooltip.Length > 126 ? tooltip.AsSpan(0, 126) : tooltip;

        Span<char> buffer = data.szTip.AsSpan();
        tip.CopyTo(buffer);
        buffer[tip.Length] = '\0';

        _added = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data);

        return _added;
    }

    /// <summary>
    /// Change the tooltip on an icon that is already there.
    ///
    /// The quietest channel this application has. It says how much is waiting without
    /// showing anything, making a sound or taking focus -- the user finds it when they
    /// point at the icon, which is when they were asking anyway.
    ///
    /// NIM_MODIFY with only NIF_TIP set: the icon and the callback message stay as they
    /// are. Sending the full flag set again would work and would also re-send the icon
    /// handle on every reminder, for nothing.
    /// </summary>
    public unsafe bool UpdateTooltip(string tooltip)
    {
        if (!_added)
            return false;

        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
        };

        // Same fixed 128-char inline buffer as TryAdd, truncated here where the limit is
        // written down rather than silently by the shell.
        ReadOnlySpan<char> tip = tooltip.Length > 126 ? tooltip.AsSpan(0, 126) : tooltip;

        Span<char> buffer = data.szTip.AsSpan();
        tip.CopyTo(buffer);
        buffer[tip.Length] = '\0';

        return PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

    /// <summary>
    /// Load the icon from a file.
    ///
    /// LR_LOADFROMFILE with an explicit small-icon size: passing 0,0 asks for the
    /// system's default icon metric, which on a high-DPI display gives a 32px icon
    /// squeezed into a 16px slot and looks visibly wrong. SM_CXSMICON is what the
    /// notification area actually wants.
    /// </summary>
    private static unsafe HICON LoadIcon(string path)
    {
        if (File.Exists(path))
        {
            // CsWin32 wraps the returned HANDLE in a SafeHandle. The raw value is what
            // NOTIFYICONDATA needs, and the icon's lifetime is managed explicitly in
            // Dispose rather than by the wrapper -- the shell holds it until NIM_DELETE.
            using Microsoft.Win32.SafeHandles.SafeFileHandle handle = PInvoke.LoadImage(
                null,
                path,
                GDI_IMAGE_TYPE.IMAGE_ICON,
                16,
                16,
                IMAGE_FLAGS.LR_LOADFROMFILE);

            if (!handle.IsInvalid)
            {
                nint raw = handle.DangerousGetHandle();
                handle.SetHandleAsInvalid();

                return new HICON(raw);
            }
        }

        // No icon file is not a reason to have no tray icon; the shell shows a blank
        // slot, which is still clickable and still carries the menu.
        return default;
    }

    /// <summary>
    /// The subclass procedure. Static and unmanaged, so it holds no state.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT Subclass(
        HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint idSubclass, nuint refData)
    {
        if (!Instances.TryGetValue((nint)hwnd.Value, out TrayIcon? tray))
            return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);

        if (message == CallbackMessage)
        {
            // The mouse message is in the LOW word of lParam, not in wParam -- wParam
            // carries the icon id. Reading the wrong one is the classic way a tray icon
            // appears to ignore clicks.
            uint mouse = (uint)((nint)lParam.Value & 0xFFFF);

            switch (mouse)
            {
                case PInvoke.WM_LBUTTONUP:
                    tray.ShowRequested?.Invoke();
                    return new LRESULT(0);

                case PInvoke.WM_RBUTTONUP:
                    tray.ShowMenu();
                    return new LRESULT(0);
            }

            return new LRESULT(0);
        }

        if (message == PInvoke.WM_COMMAND)
        {
            uint command = (uint)((nint)wParam.Value & 0xFFFF);

            switch (command)
            {
                case CommandShow:
                    tray.ShowRequested?.Invoke();
                    return new LRESULT(0);

                case CommandConsole:
                    tray.ConsoleRequested?.Invoke();
                    return new LRESULT(0);

                case CommandDock:
                    tray.DockRequested?.Invoke();
                    return new LRESULT(0);

                case CommandExit:
                    tray.ExitRequested?.Invoke();
                    return new LRESULT(0);
            }
        }

        return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// Show the context menu at the cursor.
    /// </summary>
    private unsafe void ShowMenu()
    {
        HMENU menu = PInvoke.CreatePopupMenu();

        if (menu == default)
            return;

        try
        {
            AppendItem(menu, CommandShow, "Show Shellvis");
            AppendItem(menu, CommandDock, "Dock to or undock from the taskbar");
            AppendItem(menu, CommandConsole, "Toggle console");
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
            AppendItem(menu, CommandExit, "Exit");

            if (!PInvoke.GetCursorPos(out System.Drawing.Point cursor))
                return;

            // The window has to be foreground before TrackPopupMenu, or the menu appears
            // and then will not dismiss when the user clicks elsewhere -- a documented
            // quirk of tray menus, and the reason this call is here rather than in the
            // click handler.
            PInvoke.SetForegroundWindow(_hwnd);

            PInvoke.TrackPopupMenuEx(
                menu,
                (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN),
                cursor.X,
                cursor.Y,
                _hwnd,
                null);
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    private static unsafe void AppendItem(HMENU menu, uint command, string text)
    {
        fixed (char* label = text)
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, command, new PCWSTR(label));
    }

    public unsafe void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)sizeof(NOTIFYICONDATAW),
                hWnd = _hwnd,
                uID = IconId,
            };

            // Without this the icon stays in the notification area as a ghost until the
            // user hovers over it -- the shell only notices the owner is gone when it
            // tries to talk to it.
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
            _added = false;
        }

        if (_icon != default)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }

        if (_subclassed)
        {
            PInvoke.RemoveWindowSubclass(_hwnd, &Subclass, SubclassId);
            _subclassed = false;
        }

        Instances.TryRemove((nint)_hwnd.Value, out _);
    }
}
