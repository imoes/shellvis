using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

using Shellvis.Core.Config;
using Shellvis.Core.Ui;

namespace Shellvis.Shell.Views;

/// <summary>
/// What language the window speaks, and where its words are put on.
///
/// <b>Set in code rather than bound in XAML.</b> A markup extension resolving a static
/// property is the tidier-looking answer and it is worse here: the tooltips are set on
/// buttons that also change their glyph and size when the bar docks, so the code-behind is
/// already the place that owns their appearance. Two mechanisms dressing one control is how
/// they come to disagree.
///
/// <b>Once, at startup.</b> The language is read when the window is built and not again.
/// Changing it in the settings takes effect on the next start, which is said plainly in the
/// settings rather than hidden: a half-relabelled window is worse than one that waits, and
/// the reference page cannot be relabelled at all without reloading it.
///
/// <b>What is NOT here.</b> The console. It is a log of tool names, durations and results --
/// "browser_navigate ok in 312ms" -- and translating the three words around them would make
/// a line that is half English by construction. That was a deliberate choice about scope:
/// the interface is bilingual, the log is English.
/// </summary>
public sealed partial class PillWindow
{
    private string L(string english, string german) =>
        Words.LanguageTag == "de" ? german : english;
    /// <summary>Every user-visible string, in the language this session speaks.</summary>
    /// <remarks>
    /// Static so the partial classes that make up this window can all reach it without
    /// threading a parameter through methods that have nothing else to do with language.
    /// Settled on first use, from the configuration and then the machine.
    /// </remarks>
    internal static UiText Words { get; } = Resolve();

    private static UiText Resolve()
    {
        try
        {
            return UiLanguage.For(ConfigStore.Load().Config.Ui.Language);
        }
        catch (Exception)
        {
            // A config that cannot be read is not a reason to start with no words at all.
            // The machine's own language is the better guess than an exception here.
            return UiLanguage.TextFor(UiLanguage.Detect());
        }
    }

    /// <summary>
    /// Put this language's words on everything the window shows.
    /// </summary>
    /// <remarks>
    /// Called once, after InitializeComponent, so the XAML carries whatever reads best in
    /// the file and this decides what is actually seen. The English in the markup is
    /// therefore a default rather than a duplicate -- and if a control is added and not
    /// listed here, it keeps that English, which is a visible omission rather than an empty
    /// label.
    /// </remarks>
    private void ApplyLanguage()
    {
        PromptBox.PlaceholderText = Words.AskPlaceholder;
        HistorySearch.PlaceholderText = L("Search past conversations", "Frühere Unterhaltungen suchen");
        NewSessionButton.Content = L("Start a new conversation", "Neue Unterhaltung beginnen");

        Tip(VorzimmerButton, Words.VorzimmerButtonTip);
        AutomationProperties.SetName(VorzimmerButton, L("Open workbench", "Arbeitsfenster öffnen"));
        Tip(AnswerButton, Words.AnswerButtonTip);
        AutomationProperties.SetName(AnswerButton, Words.AnswerButtonTip);
        Tip(ConsoleToggleButton, Words.ConsoleShowTip);
        AutomationProperties.SetName(ConsoleToggleButton, Words.ConsoleShowTip);
        Tip(HistoryButton, Words.HistoryTip);
        AutomationProperties.SetName(HistoryButton, Words.HistoryTip);
        Tip(SettingsButton, Words.SettingsTip);
        AutomationProperties.SetName(SettingsButton, Words.SettingsTip);
        Tip(MicButton, Words.DictateTip);
        AutomationProperties.SetName(MicButton, Words.DictateTip);
        Tip(ExpandButton, Words.ExpandTip);
        AutomationProperties.SetName(ExpandButton, Words.ExpandTip);
        Tip(AttachButton, Words.AttachTip);
        AutomationProperties.SetName(AttachButton, L("Choose Office document", "Office-Dokument auswählen"));
        Tip(ModeButton, Words.ModeTip);
        AutomationProperties.SetName(ModeButton, Words.ModeTip);
        Tip(CloseButton, Words.CloseTip);
        AutomationProperties.SetName(CloseButton, Words.CloseTip);
    }

    /// <summary>
    /// A tooltip, when the control exists.
    /// </summary>
    /// <remarks>
    /// Null-tolerant on purpose: this file names controls that live in a XAML file it does
    /// not own, and a control renamed or removed there should cost a missing tooltip rather
    /// than a window that will not open.
    /// </remarks>
    private static void Tip(DependencyObject? control, string text)
    {
        if (control is not null)
            ToolTipService.SetToolTip(control, text);
    }
}
