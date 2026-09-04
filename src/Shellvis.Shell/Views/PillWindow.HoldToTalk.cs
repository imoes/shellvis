using Microsoft.UI.Xaml;

// Aliased, not imported. DispatcherQueueTimer exists in both Microsoft.UI.Dispatching and
// Windows.System, so importing either namespace wholesale makes the type ambiguous. The same
// collision the console animation hit in step one.
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Shellvis.Shell.Views;

/// <summary>
/// Hold the space bar to talk, release to transcribe.
///
/// <b>Why this gesture and not the existing hotkey.</b> Ctrl+Alt+D is a toggle, and a toggle
/// has a failure mode a hold does not: the microphone stays open when you believe it closed.
/// Holding a key means the recording lasts exactly as long as your hand says it does. This is
/// what the plan meant by push-to-talk from the beginning, and it went unbuilt because
/// <c>RegisterHotKey</c> reports a press and never a release.
///
/// <b>Why the space bar can be it.</b> Space is a text character, so it cannot simply be
/// claimed. What separates the two meanings is duration: a tap types a space, a hold starts
/// listening. Both readings stay available and the threshold tells them apart.
///
/// <b>Why the space is swallowed and re-inserted rather than typed and taken back.</b> Both
/// were built; the first one failed, and the reason is worth keeping. Letting the space type
/// itself and deleting it at the threshold requires knowing what the box held beforehand --
/// but the hook fires before the character is inserted, so that notification and the
/// insertion race, and the answer was sometimes "with the space already in it". On top of
/// that, auto-repeat kept refilling the box after the cleanup. Swallowing every space removes
/// both problems: nothing is inserted, so nothing has to be undone.
///
/// The re-insertion is done by INDEX, not at the caret. That is what makes overlapping
/// keystrokes come out right: press space, type "w" before releasing, and the space still
/// lands where it was pressed -- "hello w", not "hellow ".
/// </summary>
public sealed partial class PillWindow
{
    /// <summary>
    /// How long the space bar must be down before it means "listen".
    ///
    /// 400 ms sits between the two things being told apart. A typed space is a tap of
    /// 60-120 ms even from a slow typist, so this is well clear of it; someone who means to
    /// hold a key holds it for the length of a sentence, so four tenths of a second costs
    /// them nothing.
    /// </summary>
    private const int HoldMilliseconds = 400;

    private DispatcherQueueTimer? _holdTimer;

    /// <summary>The keyboard hook, or null when it could not be installed.</summary>
    private Interop.SpaceHook? _spaceHook;

    /// <summary>Whether the space bar is currently down.</summary>
    private bool _spaceDown;

    /// <summary>
    /// Where the swallowed space belongs if this turns out to be a tap.
    ///
    /// Captured at the press and used verbatim on release, which is what keeps a space typed
    /// during another keystroke in its right place.
    /// </summary>
    private int _spaceIndex;

    /// <summary>Whether the press already became a hold, so the release means "stop".</summary>
    private bool _holdDictating;

    /// <summary>
    /// Whether the space-bar gesture is actually available.
    ///
    /// Read when the console says how to dictate: the hook can fail to install, and a line
    /// that offers a gesture which does nothing is worse than one that offers neither.
    /// </summary>
    private bool HoldToTalkInstalled => _spaceHook is not null;

    private void RegisterHoldToTalk()
    {
        // A low-level keyboard hook, not a XAML key handler. Three XAML routes were tried
        // against a real keyboard and all three failed identically: WinUI raises KeyDown after
        // the TextBox has turned the key into a character, so marking the event handled -- on
        // the root, or on the box itself -- suppresses nothing at all.
        _spaceHook = new Interop.SpaceHook(
            WinRT.Interop.WindowNative.GetWindowHandle(this));

        _spaceHook.Pressed += () => DispatcherQueue.TryEnqueue(OnSpacePressed);
        _spaceHook.Released += () => DispatcherQueue.TryEnqueue(OnSpaceReleased);

        if (_spaceHook.Install() is { } problem)
        {
            // A warning, not a failure: Ctrl+Alt+D still dictates, and refusing to start over
            // a convenience would be out of proportion.
            AddRow(GlyphWarning, problem, "voice");
            _spaceHook.Dispose();
            _spaceHook = null;

            return;
        }

        // Live only while the input box has the focus. Space activates a focused button in
        // WinUI, and there is nothing to re-insert a tapped space into when the focus is
        // elsewhere -- so outside the box the key is left entirely alone.
        _spaceHook.Armed = PromptBox.FocusState != FocusState.Unfocused;

        PromptBox.GotFocus += (_, _) =>
        {
            if (_spaceHook is not null)
                _spaceHook.Armed = true;
        };

        PromptBox.LostFocus += (_, _) =>
        {
            if (_spaceHook is not null)
                _spaceHook.Armed = false;


            // A press whose release will never be seen here has to be closed out, or the
            // microphone stays open with nothing on screen saying so.
            if (_spaceDown)
                OnSpaceReleased();
        };

        // The same hazard one level up: the window loses activation while the bar is down --
        // Alt+Tab, the screen locking -- and no release ever arrives.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated && _spaceDown)
                OnSpaceReleased();
        };
    }

    private void OnSpacePressed()
    {
        if (_spaceDown)
            return;

        _spaceDown = true;


        // A press over a selection would have replaced it. Remembered as the selection start,
        // and the selection is left intact: re-inserting into it on a tap is the caller's job
        // below, and deleting what the user had selected is not something to do on a guess.
        _spaceIndex = PromptBox.SelectionStart;

        _holdTimer ??= DispatcherQueue.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(HoldMilliseconds);
        _holdTimer.IsRepeating = false;

        // Reassigned rather than added: CreateTimer returns one object reused for every hold,
        // and subscribing per press would stack handlers and start dictation several times.
        _holdTimer.Tick -= OnHoldElapsed;
        _holdTimer.Tick += OnHoldElapsed;
        _holdTimer.Start();
    }

    private void OnHoldElapsed(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();

        if (!_spaceDown)
            return;

        // Dictation may already be running from the toggle hotkey, and taking it over on a key
        // press would end a recording somebody else started.
        if (_dictation?.State is Core.Voice.DictationState.Listening
            or Core.Voice.DictationState.Transcribing)
        {
            return;
        }


        _holdDictating = true;
        StartDictation();

    }

    private void OnSpaceReleased()
    {
        if (!_spaceDown)
            return;


        _spaceDown = false;
        _holdTimer?.Stop();

        if (!_holdDictating)
        {
            // A tap. The space the hook swallowed is put back where it was pressed.
            InsertSpace();
            return;
        }

        _holdDictating = false;

        if (_dictation?.State != Core.Voice.DictationState.Listening)
            return;

        _dictation.Stop();

        if (_dictation.State == Core.Voice.DictationState.Transcribing)
        {
            ShowListening(false);
            StatusText.Text = "Transcribing...";
        }
    }

    /// <summary>
    /// Put back the space that was swallowed, at the position it was pressed.
    /// </summary>
    private void InsertSpace()
    {
        string text = PromptBox.Text;
        int at = Math.Clamp(_spaceIndex, 0, text.Length);

        // Selection replaced, as a typed character would: if the user had text selected when
        // they hit space, the space takes its place.
        if (PromptBox.SelectionLength > 0)
        {
            int start = PromptBox.SelectionStart;
            int length = Math.Min(PromptBox.SelectionLength, text.Length - start);

            PromptBox.Text = text.Remove(start, length).Insert(start, " ");
            PromptBox.SelectionStart = start + 1;

            return;
        }

        // Read BEFORE the assignment. Setting TextBox.Text resets the caret to zero, so
        // reading it afterwards always yields 0 -- and this cost a real, visible bug: typing
        // "hallo", tapping space and typing "da" produced "dahallo ", because the caret had
        // silently gone to the start and the next two letters landed there.
        int caret = PromptBox.SelectionStart;

        PromptBox.Text = text.Insert(at, " ");

        // The caret moves past the inserted space only if it was at or after it. A space that
        // belongs behind the caret -- which happens when another key was pressed during the
        // press -- must not drag the caret backwards.
        PromptBox.SelectionStart = Math.Clamp(caret >= at ? caret + 1 : caret, 0, PromptBox.Text.Length);
    }
}
