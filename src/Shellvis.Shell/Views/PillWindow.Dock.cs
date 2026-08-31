using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Shellvis.Core.Desktop;
using Windows.Graphics;

namespace Shellvis.Shell.Views;

/// <summary>
/// The docked mode: a compact input field lying on the taskbar strip, with an arrow that
/// brings the full pill back.
///
/// <b>What this is not.</b> It is not a control inside the Windows taskbar. The taskbar
/// is Explorer's own window, and putting a child window into it means injecting into a
/// system process -- unsupported, broken by the next Windows update, and the sort of thing
/// security software treats as hostile. So this is a small always-on-top window positioned
/// ON the taskbar strip. It looks and behaves like the search field beside it; it is not
/// literally part of it, and that distinction is worth knowing before wondering why it
/// does not follow taskbar theming.
///
/// Docking shrinks the pill BAND and the window with it, then moves the window so the
/// band lands on the taskbar. The console still grows upward from there, which is exactly
/// the requested popup, with no second layout to keep in step. Shrinking the window too
/// is not optional: the window is always exactly as tall as its content, and a 412px
/// window around a 382px layout left thirty pixels of nothing below the bar, which lifted
/// the visible bar that far off the taskbar.
/// </summary>
public sealed partial class PillWindow
{
    private bool _docked;

    /// <summary>Where the window sat before docking, so undocking is exact.</summary>
    private RectInt32? _undockedPlacement;

    private void ToggleDock()
    {
        if (_docked)
            Undock();
        else
            Dock();
    }

    private void Dock()
    {
        _undockedPlacement = new RectInt32(
            AppWindow.Position.X, AppWindow.Position.Y,
            AppWindow.Size.Width, AppWindow.Size.Height);

        // Shut the console first: docking with it open would leave a panel floating over
        // the desktop with its anchor moving underneath it.
        if (_consoleOpen)
            ToggleConsole();

        _docked = true;

        // What stays, and why each one earns its place on a 34px bar.
        //
        // The prompt box and the microphone: dictating into a docked field is the main reason
        // to have one. The console toggle: docked is the state in which output is least
        // visible, so reaching the log matters more here than when the pill floats with its
        // full row. And the history, which was missing and was asked for -- a docked bar is
        // also the state someone leaves Shellvis in for hours, which makes "what did I ask
        // earlier" a question that arises HERE, and the alternative was to undock first, find
        // the history, and dock again.
        //
        // What goes: attach, the mode pill and minimise. Attach and mode belong to composing
        // a request at length, which is not what a taskbar field is for, and minimise is
        // replaced by the expand arrow that undoes it.
        AttachButton.Visibility = Visibility.Collapsed;
        ModeButton.Visibility = Visibility.Collapsed;
        SparkleButton.Visibility = Visibility.Collapsed;
        HistoryButton.Visibility = Visibility.Visible;
        ConsoleToggleButton.Visibility = Visibility.Visible;
        ExpandButton.Visibility = Visibility.Visible;

        PillHost.Height = PillMetrics.DockedHeight;
        RootHost.Width = PillMetrics.DockedWidth;

        ApplyDockedLook(docked: true);
        PlaceOnTaskbar();
    }

    /// <summary>
    /// Swap between the floating look and the taskbar look.
    ///
    /// The pill's own styling is right when it floats over a desktop and wrong on the
    /// taskbar: a light acrylic panel with a coloured gradient ring reads as something
    /// pasted on top rather than a control sitting in it. Windows' own field down there is
    /// flat, near-neutral, with a hairline border and no accent -- so that is what this
    /// matches.
    /// </summary>
    private void ApplyDockedLook(bool docked)
    {
        if (!docked)
        {
            PillTint.Background = Brush("PillTintBrush");
            PillRing.BorderBrush = Brush("PillRingBrush");
            PillRing.BorderThickness = new Thickness(1.5);
            PillRing.CornerRadius = new CornerRadius(PillMetrics.PillRadius);
            PillTint.CornerRadius = new CornerRadius(PillMetrics.PillRadius);
            PillBackdrop.CornerRadius = new CornerRadius(PillMetrics.PillRadius);

            PromptBox.Foreground = Brush("PillTextBrush");
            PromptBox.PlaceholderForeground = Brush("PillHintBrush");
            PromptBox.FontSize = 15;

            MicBackdrop.Background = Brush("MicButtonBrush");
            MicBackdrop.Width = 38;
            MicBackdrop.Height = 38;
            MicButton.Foreground = Brush("PillGlyphBrush");
            ExpandButton.Foreground = Brush("PillGlyphBrush");

            // Both back to the pill's size, and both of them: the history button is shrunk
            // while docked too, so undocking has to undo the same thing twice.
            foreach (Button button in new[] { ConsoleToggleButton, HistoryButton })
            {
                button.Width = 36;
                button.Height = 36;
                button.Foreground = Brush("PillGlyphBrush");
            }

            return;
        }

        // The TASKBAR's theme, not the app's. Windows keeps the two independent, and a
        // light app on a dark taskbar is an ordinary configuration -- following the app
        // theme here is how a light bar ends up on a dark strip.
        bool light = TaskbarIsLight();
        string suffix = light ? "Light" : "Dark";

        PillTint.Background = Brush("DockedTint" + suffix);
        PillRing.BorderBrush = Brush("DockedBorder" + suffix);

        // One pixel, not one and a half: a hairline. The gradient ring is gone entirely --
        // an accent-coloured outline on a taskbar control reads as "selected" or "error".
        PillRing.BorderThickness = new Thickness(1);

        var radius = new CornerRadius(PillMetrics.DockedRadius);
        PillRing.CornerRadius = radius;
        PillTint.CornerRadius = radius;
        PillBackdrop.CornerRadius = radius;

        PromptBox.Foreground = Brush("DockedText" + suffix);
        PromptBox.PlaceholderForeground = Brush("DockedHint" + suffix);

        // 14, matching the taskbar's own text scale rather than the pill's 15.
        PromptBox.FontSize = 14;

        MicBackdrop.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        MicBackdrop.Width = 26;
        MicBackdrop.Height = 26;
        MicButton.Foreground = Brush("DockedGlyph" + suffix);
        ExpandButton.Foreground = Brush("DockedGlyph" + suffix);

        // Shrunk to fit the strip, like the microphone beside it. Left at 36 they would be
        // taller than the 34px bar that contains them.
        foreach (Button button in new[] { ConsoleToggleButton, HistoryButton })
        {
            button.Width = PillMetrics.DockedButton;
            button.Height = PillMetrics.DockedButton;
            button.Foreground = Brush("DockedGlyph" + suffix);
        }
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    /// <summary>
    /// Whether the taskbar is light.
    ///
    /// Read from SystemUsesLightTheme, which is the setting the taskbar and the tray
    /// follow. AppsUseLightTheme -- what a XAML app's own theme tracks -- is a separate
    /// value, and the two differ on any machine set to "Dark" for Windows and "Light" for
    /// apps, which is a common preference.
    /// </summary>
    private static bool TaskbarIsLight()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // Absent means the Windows default, which is dark.
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Undock()
    {
        _docked = false;

        AttachButton.Visibility = Visibility.Visible;
        HistoryButton.Visibility = Visibility.Visible;
        ConsoleToggleButton.Visibility = Visibility.Visible;
        ModeButton.Visibility = Visibility.Visible;
        SparkleButton.Visibility = Visibility.Visible;
        ExpandButton.Visibility = Visibility.Collapsed;

        PillHost.Height = PillMetrics.PillHeight;
        RootHost.Width = PillMetrics.Width;

        ApplyDockedLook(docked: false);

        if (_undockedPlacement is { } placement)
            AppWindow.MoveAndResize(placement);
        else
            PositionAtBottomCentre();

        // The layout has to settle before the region can be measured from it, which is
        // why this is queued rather than called inline.
        DispatcherQueue.TryEnqueue(() => ApplyRegion(_consoleOpen ? PillMetrics.ConsoleHeight : 0));

        _hotkey?.BringToFront();
        PromptBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Put the window where the docked bar lands on the taskbar strip.
    ///
    /// The strip is the difference between the display bounds and the work area -- there
    /// is no supported way to ask the taskbar for its rectangle, and reading Shell_TrayWnd
    /// directly would break the moment someone moves the taskbar to the side. The
    /// difference is defined regardless of edge, so this degrades sensibly.
    /// </summary>
    private void PlaceOnTaskbar()
    {
        double scale = _shaper.Scale;

        DisplayArea display = DisplayArea.GetFromWindowId(
            AppWindow.Id, DisplayAreaFallback.Nearest);

        RectInt32 outer = display.OuterBounds;
        RectInt32 work = display.WorkArea;

        int width = (int)Math.Round(PillMetrics.DockedWidth * scale);
        int height = (int)Math.Round(PillMetrics.DockedTotalHeight * scale);
        int bandHeight = (int)Math.Round(PillMetrics.DockedHeight * scale);

        int taskbarTop = work.Y + work.Height;
        int taskbarHeight = outer.Y + outer.Height - taskbarTop;

        if (taskbarHeight <= 0)
        {
            // No taskbar at the bottom -- hidden, or on another edge. Sitting at the
            // bottom of the work area is the honest fallback: still reachable, still
            // out of the way, and it does not pretend to be docked to something.
            taskbarTop = work.Y + work.Height - bandHeight;
            taskbarHeight = bandHeight;
        }

        // Centred within the strip's height, so the bar looks seated rather than
        // overlapping the taskbar's own buttons.
        int bandTop = taskbarTop + Math.Max(0, (taskbarHeight - bandHeight) / 2);

        // Where the taskbar is actually empty, asked rather than assumed. The arithmetic
        // this replaces -- a fixed offset from the right, never left of centre -- is right
        // on an empty taskbar and wrong on a working one: Windows 11 centres the app
        // buttons, so the cluster grows outward as windows open and eventually reaches under
        // a bar parked at a fixed offset, while the stretch beside Start sits empty.
        int trayReserve = (int)Math.Round(230 * scale);
        int x;

        TaskbarLayout.Span? free = TaskbarLayout.FindFreeSpan(
            stripTop: taskbarTop,
            stripBottom: outer.Y + outer.Height,
            stripLeft: work.X,
            stripRight: work.X + work.Width,
            needed: width);

        if (free is { } span)
        {
            // Against the side the icons are on, so the bar reads as part of the row rather
            // than as something adrift in the middle of an empty stretch. The left-hand gap
            // is bounded on its right by the cluster, so the bar tucks in beside Start.
            x = span.Left <= work.X
                ? span.Right - width - (int)Math.Round(12 * scale)
                : span.Left + (int)Math.Round(12 * scale);

            x = Math.Clamp(x, span.Left, Math.Max(span.Left, span.Right - width));
        }
        else
        {
            // The taskbar could not be read -- Explorer restarting, a shell that no longer
            // publishes its buttons. The old arithmetic, which has been on screen for
            // months, is a better answer than an invented one.
            x = work.X + work.Width - trayReserve - width;
            x = Math.Max(x, work.X + (work.Width / 2));
        }

        // The band is the bottom of the window, so the top is the band's top minus
        // everything above it.
        int y = bandTop - (height - bandHeight);

        AppWindow.MoveAndResize(ContentRect(x, y, width, height));

        DispatcherQueue.TryEnqueue(() => ApplyRegion(0));
    }

    /// <summary>
    /// While docked, console output opens the panel by itself.
    ///
    /// The request was that output "pops up". A docked bar with a hidden console would
    /// otherwise run commands with no visible trace, which is the opacity this whole
    /// console exists to remove -- and worse when the window is deliberately small.
    /// </summary>
    private void RevealConsoleIfDocked()
    {
        if (_docked && !_consoleOpen)
            ToggleConsole();
    }
}
