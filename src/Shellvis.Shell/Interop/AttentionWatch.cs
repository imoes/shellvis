using Shellvis.Core.Desktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Shellvis.Shell.Interop;

/// <summary>
/// Feeds <see cref="AttentionLog"/>: every time another window comes to the front, and every
/// time the window in front changes its title.
///
/// <b>Why an event hook and not a poll.</b> Windows announces both changes, and an
/// out-of-context hook costs nothing between them; a poll would either miss a short switch or
/// wake the machine every second for a question whose answer rarely changes.
///
/// <b>The title changes matter as much as the switches.</b> A browser keeps one window and
/// retitles it for every tab; Outlook's reading pane leaves the main window's title alone, but
/// an opened mail gets a window of its own titled with the subject. Name changes are the
/// chattiest event there is, so only the foreground window's own title is taken, never a
/// child's.
///
/// Its own windows are left out: "the user was looking at Shellvis" answers nothing.
/// </summary>
internal sealed class AttentionWatch : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectNameChange = 0x800C;
    private const uint OutOfContext = 0x0000;
    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;

    private readonly AttentionLog _log;

    // The one live watch. The callback is an unmanaged function pointer -- marshaling is off
    // in this project -- so it cannot capture anything and finds its instance here.
    private static AttentionWatch? _current;

    private HWINEVENTHOOK _foregroundHook;
    private HWINEVENTHOOK _nameHook;

    public AttentionWatch(AttentionLog log)
    {
        _log = log;
        _current = this;
    }

    /// <summary>Install the hooks on the calling thread, which must pump messages: the UI thread.</summary>
    public unsafe bool Start()
    {
        _foregroundHook = PInvoke.SetWinEventHook(
            EventSystemForeground, EventSystemForeground, HMODULE.Null, &OnEvent, 0, 0, OutOfContext);

        _nameHook = PInvoke.SetWinEventHook(
            EventObjectNameChange, EventObjectNameChange, HMODULE.Null, &OnEvent, 0, 0, OutOfContext);

        Note(PInvoke.GetForegroundWindow());

        return !_foregroundHook.IsNull;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void OnEvent(
        HWINEVENTHOOK hook, uint eventType, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd.IsNull)
            return;

        if (eventType == EventObjectNameChange)
        {
            // Only the window itself, and only the one in front: a child control renaming
            // itself is not a change of what the user is looking at.
            if (idObject != ObjectIdWindow || idChild != ChildIdSelf || hwnd != PInvoke.GetForegroundWindow())
                return;
        }

        _current?.Note(hwnd);
    }

    private unsafe void Note(HWND hwnd)
    {
        try
        {
            if (WindowInspector.Describe((nint)hwnd.Value) is not { } window)
                return;

            if (window.ProcessId == Environment.ProcessId || window.ProcessName.Equals("LockApp", StringComparison.OrdinalIgnoreCase))
                return;

            _log.Note(window.ProcessName, window.Title, DateTime.Now);
        }
        catch (Exception)
        {
            // A window that vanished between the event and the look. Nothing to record.
        }
    }

    public void Dispose()
    {
        if (!_foregroundHook.IsNull)
            PInvoke.UnhookWinEvent(_foregroundHook);

        if (!_nameHook.IsNull)
            PInvoke.UnhookWinEvent(_nameHook);

        _foregroundHook = default;
        _nameHook = default;

        if (_current == this)
            _current = null;
    }
}
