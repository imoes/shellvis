using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Shellvis.Core.Permissions;

namespace Shellvis.Shell.Views;

/// <summary>
/// The permission-mode chip.
///
/// One control, three states, and the state is always on screen. The alternative -- a
/// slash command -- was in the plan and is not enough on its own: how much an agent may
/// do to a machine without asking is the single most consequential setting it has, and a
/// setting you have to remember a command to inspect is a setting nobody inspects.
/// </summary>
public sealed partial class PillWindow
{
    /// <summary>The order the menu offers them in: least to most permissive.</summary>
    private static readonly PermissionMode[] Modes =
        [PermissionMode.Ask, PermissionMode.AutoRead, PermissionMode.Yolo];

    private void ShowModeMenu()
    {
        if (_session is null)
            return;

        var flyout = new MenuFlyout
        {
            // Above the chip: the pill sits at the bottom of the screen, so a menu
            // dropping downward would open off the display.
            Placement = FlyoutPlacementMode.Top,
        };

        PermissionMode current = _session.Permissions.Mode;

        foreach (PermissionMode mode in Modes)
        {
            // Radio rather than a plain item so the current mode is visible in the menu
            // itself. A user opening it to check what is set should not have to infer it
            // from the chip they just clicked on.
            var item = new RadioMenuFlyoutItem
            {
                GroupName = "PermissionMode",
                Text = $"{PermissionPolicy.Label(mode)}  -  {PermissionPolicy.Describe(mode)}",
                IsChecked = mode == current,
                Tag = mode,
            };

            item.Click += (sender, _) =>
            {
                if (sender is RadioMenuFlyoutItem { Tag: PermissionMode chosen })
                    ChooseMode(chosen);
            };

            flyout.Items.Add(item);
        }

        flyout.ShowAt(ModeButton);
    }

    private void ChooseMode(PermissionMode mode)
    {
        if (_session?.SetPermissionMode(mode) is { } note)
        {
            // Announced in the transcript, not only shown on the chip. A permission change
            // is an event in the session's history, and the record of what the agent was
            // allowed to do at the time matters when reading back what it did.
            AddRow(GlyphSpeaker, note, "mode", isAnnouncement: true);
        }

        RefreshModeChip();
    }

    /// <summary>
    /// Put the current mode on the chip. Called after the session is up, because the mode
    /// comes from the config file and the chip's XAML default would otherwise be a claim
    /// rather than a reading.
    /// </summary>
    private void RefreshModeChip()
    {
        PermissionMode mode = _session?.Permissions.Mode ?? PermissionMode.AutoRead;

        ModeButton.Content = PermissionPolicy.Label(mode);
        ModeButton.SetValue(ToolTipService.ToolTipProperty, PermissionPolicy.Describe(mode));

        Brush accent = mode == PermissionMode.Yolo
            // A Window has no theme of its own; the theme lives on the content root.
            ? Brush(RootHost.ActualTheme == ElementTheme.Dark ? "ModeWarnBrushDark" : "ModeWarnBrush")
            : Brush("PillHintBrush");

        ModeButton.Foreground = accent;
        ModeButton.BorderBrush = accent;
    }
}
