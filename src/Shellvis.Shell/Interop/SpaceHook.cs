using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shellvis.Shell.Interop;

/// <summary>
/// Sees the space bar before any window does, so a held key can be swallowed.
///
/// <b>Why a low-level hook and not a key handler.</b> Three cheaper mechanisms were tried
/// against a real keyboard and all three failed for the same reason: by the time WinUI raises
/// KeyDown, the TextBox has already turned the key into a character. Marking the event handled
/// on the root does nothing (the handler runs after the control), and marking it handled on the
/// TextBox itself does nothing either (its class handler runs before instance handlers). A
/// held space therefore filled the prompt box with spaces, and worse, deleting them at the
/// hold threshold did not help because the auto-repeat kept arriving afterwards and refilled
/// it.
///
/// <c>WH_KEYBOARD_LL</c> is the level where the decision can actually be made: it sees the
/// event before it is dispatched to any window, and returning non-zero drops it. The project
/// plan predicted exactly this when it noted that real push-to-talk "would need a low-level
/// keyboard hook", because <c>RegisterHotKey</c> reports a press and never a release.
///
/// <b>What it deliberately does not do.</b> It never swallows the FIRST press. A tap of the
/// space bar has to type a space, and at the moment of the press nobody knows yet whether this
/// is a tap or a hold -- suppressing it and re-injecting it on release would put the space
/// after the next letter for anyone typing quickly. So the early spaces are allowed through and
/// removed by the caller, and only once the press is known to be a hold is the key swallowed.
///
/// <b>Scope.</b> The hook is global by nature -- there is no per-window variant -- so it
/// checks the foreground window and does nothing whatsoever unless the pill has it. A hook
/// that acted on the space bar in someone's document would be a keylogger with a good excuse.
/// </summary>
internal sealed unsafe class SpaceHook : IDisposable
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint VK_SPACE = 0x20;

    /// <summary>
    /// The one installed hook.
    ///
    /// Static because the callback cannot capture: under CsWin32 with marshalling disabled it
    /// is an unmanaged function pointer, so closures are impossible and the instance has to be
    /// reachable from a static field. The same constraint the hotkey subclass works under --
    /// but a single field rather than a dictionary, because there is exactly one pill and a
    /// second low-level keyboard hook in one process would be a bug rather than a feature.
    /// </summary>
    private static SpaceHook? _installed;

    private UnhookWindowsHookExSafeHandle? _hook;
    private readonly HWND _owner;

    private Thread? _pump;
    private uint _pumpThreadId;
    private readonly ManualResetEventSlim _ready = new(false);
    private string? _installProblem;

    /// <summary>Whether the space bar is currently down, so repeats are recognisable.</summary>
    private bool _down;

    public SpaceHook(nint ownerHandle) => _owner = (HWND)ownerHandle;

    /// <summary>
    /// Declared by hand, because CsWin32 does not project this one.
    ///
    /// It lives in kernel32 rather than user32, and asking for it in NativeMethods.txt yields
    /// nothing -- the Win32 metadata exposes it under a name the generator does not match. One
    /// signature is cheaper than working around its absence.
    /// </summary>
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>Raised on the first press, once per physical press.</summary>
    public event Action? Pressed;

    /// <summary>Raised on release.</summary>
    public event Action? Released;

    /// <summary>
    /// Whether the gesture is live at all: only when the prompt box has the focus.
    ///
    /// The scope has to be this narrow. Space activates a focused button in WinUI, and
    /// swallowing it everywhere would break that; and when the focus is not in a text box
    /// there is nothing for a tapped space to be re-inserted into. So outside the input box
    /// the space bar is left completely alone and this feature does not exist.
    /// </summary>
    public bool Armed { get; set; }

    /// <summary>
    /// Install the hook on a thread of its own, and wait until it is up.
    ///
    /// <b>Why its own thread.</b> A low-level hook is called on the thread that installed it,
    /// and Windows skips a hook whose thread does not answer within
    /// <c>LowLevelHooksTimeout</c> -- 300 ms by default -- delivering the keystroke as if no
    /// hook existed. Installed on the UI thread, every piece of UI work became a hole in the
    /// suppression: opening the microphone and starting the console animation was enough for a
    /// space to slip into the prompt box. Measured, not theorised: preloading the speech model
    /// removed a 1.8-second stall and one space still got through afterwards.
    ///
    /// A thread that does nothing but pump messages cannot be late, so the suppression stops
    /// depending on what the UI happens to be doing.
    /// </summary>
    public string? Install()
    {
        if (_installed is not null && _installed != this)
            return "a space hook is already installed in this process.";

        _installed = this;

        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "Shellvis space hook",

            // Above normal: this thread is on the critical path of every key press, and being
            // scheduled late here is indistinguishable from not being installed.
            Priority = ThreadPriority.AboveNormal,
        };

        _pump.Start();

        // Waited for, so the caller knows whether the gesture exists before it reports it. A
        // bound rather than an indefinite wait: a hook that cannot be installed must not stop
        // the application from starting.
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            return "the keyboard hook did not start; hold-to-talk is unavailable.";

        if (_installProblem is not null)
            _installed = null;

        return _installProblem;
    }

    private void Pump()
    {
        // The OS thread id, which is what PostThreadMessage addresses -- not the managed one,
        // which is a runtime bookkeeping number and unrelated.
        _pumpThreadId = GetCurrentThreadId();

        // hMod null and thread 0: a global low-level hook. There is no per-window form, which
        // is why the foreground check in the callback is not optional.
        _hook = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &Callback, null, 0);

        if (_hook is null || _hook.IsInvalid)
        {
            _installProblem = "the keyboard hook could not be installed "
                + $"(error {Marshal.GetLastWin32Error()}); hold-to-talk is unavailable.";

            _ready.Set();
            return;
        }

        _ready.Set();

        // A message loop is required, not optional: low-level hook callbacks are delivered
        // through the thread's message queue, so a thread that never pumps is never called.
        MSG message;

        while (PInvoke.GetMessage(out message, default, 0, 0))
        {
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static LRESULT Callback(int code, WPARAM wParam, LPARAM lParam)
    {
        SpaceHook? hook = _installed;

        // code below zero means "pass it on without looking", and it is not advice.
        if (hook is null || code < 0)
            return PInvoke.CallNextHookEx(null, code, wParam, lParam);

        try
        {
            return hook.Handle((int)wParam.Value, lParam);
        }
        catch (Exception)
        {
            // An exception escaping a hook procedure takes the process with it, and a
            // dictation convenience must not be able to do that.
            return PInvoke.CallNextHookEx(null, code, wParam, lParam);
        }
    }

    private LRESULT Handle(int message, LPARAM lParam)
    {
        var info = *(KBDLLHOOKSTRUCT*)lParam.Value;

        if (info.vkCode != VK_SPACE)
            return PInvoke.CallNextHookEx(null, 0, (WPARAM)(nuint)message, lParam);

        // Injected input is NOT filtered out. It would be easy to reject anything carrying
        // LLKHF_INJECTED, and it would be wrong twice over: on-screen keyboards, remote
        // sessions and accessibility tools all inject, so a real user would lose the gesture,
        // and nothing is gained -- the foreground check below is what limits the scope.
        bool isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = message is WM_KEYUP or WM_SYSKEYUP;

        if (!isDown && !isUp)
            return PInvoke.CallNextHookEx(null, 0, (WPARAM)(nuint)message, lParam);

        // Nothing at all unless the pill is the foreground window. This is a global hook and
        // the space bar belongs to whatever the user is actually typing in.
        if (!Armed || PInvoke.GetForegroundWindow() != _owner)
        {
            _down = false;

            return PInvoke.CallNextHookEx(null, 0, (WPARAM)(nuint)message, lParam);
        }

        if (isDown)
        {
            bool repeat = _down;
            _down = true;

            // Every space is dropped, including the first, and the caller puts one back if the
            // press turns out to be a tap. The earlier design let the first one through and
            // removed it later, which failed for a reason worth recording: the hook runs
            // before the character is inserted, so the notification and the insertion race,
            // and "what did the box look like before the space" had no reliable answer.
            // Dropping it removes the race -- there is nothing to undo.
            if (!repeat)
                Pressed?.Invoke();

            return (LRESULT)1;
        }

        _down = false;
        Released?.Invoke();

        // The release is dropped too: a KEYUP for a KEYDOWN no window ever saw reads as a
        // stray release, and some controls act on it.
        return (LRESULT)1;
    }

    public void Dispose()
    {
        if (_installed == this)
            _installed = null;

        // The loop is asked to end, and the hook is released on the thread that installed it.
        // Unhooking from another thread is allowed but tearing the thread down first is not:
        // its message queue is how the callback arrives.
        if (_pumpThreadId != 0)
            PInvoke.PostThreadMessage(_pumpThreadId, PInvoke.WM_QUIT, default, default);

        _pump?.Join(TimeSpan.FromSeconds(2));

        _hook?.Dispose();
        _hook = null;
        _ready.Dispose();
    }
}
