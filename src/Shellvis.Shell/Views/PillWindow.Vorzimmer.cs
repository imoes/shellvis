namespace Shellvis.Shell.Views;

/// <summary>
/// The reference page, and the button that opens it.
///
/// <b>Why the application carries its own rules.</b> How this assistant sorts mail, when it
/// decides something is worth interrupting for, and what it will never do -- those are not
/// implementation details, they are the terms of the arrangement. They were written down in
/// three skill files and a prompt, which is the right place for the model to read them and
/// the wrong place for a person to. So they are also one page, in a window, behind a button
/// next to the answer.
///
/// Held lazily and never destroyed, for the same reason as the answer window: most sessions
/// never open it, and the one that does should not pay for the runtime twice.
/// </summary>
public sealed partial class PillWindow
{
    private VorzimmerWindow? _vorzimmer;

    /// <summary>Open the reference page, or bring it back if it is already up.</summary>
    private void ShowVorzimmer()
    {
        _vorzimmer ??= new VorzimmerWindow();

        _vorzimmer.Reveal(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }
}
