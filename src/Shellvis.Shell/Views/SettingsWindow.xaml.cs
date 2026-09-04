using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Shellvis.Shell.Interop;

using Windows.Graphics;

namespace Shellvis.Shell.Views;

/// <summary>One box on a settings form.</summary>
/// <param name="Key">How the caller asks for the value afterwards.</param>
/// <param name="Label">The header above the box.</param>
/// <param name="Placeholder">
/// Shown when the box is empty. This is where an INHERITED value belongs: putting it in the
/// text would make the next save freeze it into an explicit setting.
/// </param>
/// <param name="Value">What the configuration actually holds, which may be nothing.</param>
/// <param name="Secret">Whether it is typed into a password box and never read back.</param>
/// <param name="Enabled">False for a value that is fixed and only being shown.</param>
/// <param name="Min">
/// With <paramref name="Max"/>, makes this field a slider rather than a box.
///
/// A range is a different kind of question from a string: there is no wrong value to type,
/// only a position to choose, and a text box for it invites "30 Tage" and then has to explain
/// why that is not a number. The value comes back as the integer it is.
/// </param>
/// <param name="Max">The other end of the range. Ignored unless Min is set too.</param>
/// <param name="Describe">
/// What a position on the slider means, in words, shown beside the header as it moves.
/// Optional, and worth supplying: "60" tells the reader nothing that "die letzten zwei
/// Monate" does not tell them better.
/// </param>
internal sealed record SettingsField(
    string Key,
    string Label,
    string Placeholder = "",
    string Value = "",
    bool Secret = false,
    bool Enabled = true,
    int? Min = null,
    int? Max = null,
    Func<int, string>? Describe = null);

/// <summary>What the user did with a settings form.</summary>
/// <param name="Button">The button they pressed, or null when they closed it.</param>
/// <param name="Values">What every enabled box contained.</param>
internal sealed record SettingsResult(string? Button, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// A settings form, in a window of its own.
///
/// <b>Why not a ContentDialog in the pill, which is what this replaces.</b> A ContentDialog is
/// drawn inside the window that hosts its XamlRoot, and the pill is 386 physical pixels wide
/// with its console open -- and clipped by a Win32 region to the silhouette it paints. A form
/// with six fields is therefore cut twice: once by a window far too small for it, once by the
/// region. The report was "the window is incomplete and is not displayed properly", with a
/// screenshot showing the buttons sliced in half. The approval prompt gets away with it
/// because it is two lines.
///
/// So the form gets a real window: sized to its content, centred on the monitor the pill is
/// on, rounded the same way the answer window is, and closed by its own buttons. Not modal --
/// WinUI has no modal window -- but always on top of its owner, which is enough for something
/// the user opened deliberately.
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private readonly WindowShaper _shaper;
    private readonly Dictionary<string, Control> _boxes = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<SettingsResult> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PointInt32 _dragFrom;
    private PointInt32 _windowFrom;
    private bool _dragging;
    private bool _answered;

    private SettingsWindow()
    {
        InitializeComponent();

        _shaper = new WindowShaper(Win32Interop.GetWindowFromWindowId(AppWindow.Id));

        ExtendsContentIntoTitleBar = true;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        Closed += (_, _) => Answer(null);
    }

    /// <summary>
    /// Put a form on screen and wait for it.
    ///
    /// The first button in <paramref name="buttons"/> is the accent one and the last is
    /// treated as the way out, which is the order Windows dialogs use.
    /// </summary>
    public static Task<SettingsResult> ShowAsync(
        nint besideWindow,
        string title,
        string? note,
        IReadOnlyList<SettingsField> fields,
        IReadOnlyList<string> buttons)
    {
        var window = new SettingsWindow();
        window.Build(title, note, fields, buttons);
        window.PlaceBeside(besideWindow, fields.Count);

        window.Activate();

        return window._done.Task;
    }

    /// <summary>
    /// A range field, as a slider with its meaning in the header.
    ///
    /// The header is rewritten as the thumb moves, which is what makes a number of days
    /// legible: nobody knows what 62 means until it says "die letzten zwei Monate". The
    /// wording comes from the caller rather than from here, so it lives in one place with the
    /// setting it describes.
    /// </summary>
    private Slider Slide(SettingsField field, int low, int high)
    {
        _ = int.TryParse(field.Value, System.Globalization.CultureInfo.InvariantCulture, out int start);

        var slider = new Slider
        {
            Minimum = low,
            Maximum = high,
            StepFrequency = 1,
            Value = Math.Clamp(start == 0 ? low : start, low, high),
            IsEnabled = field.Enabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        void Retitle() => slider.Header = field.Describe is { } describe
            ? $"{field.Label} — {describe((int)Math.Round(slider.Value))}"
            : field.Label;

        Retitle();
        slider.ValueChanged += (_, _) => Retitle();

        if (field.Enabled)
            _boxes[field.Key] = slider;

        return slider;
    }

    private void Build(
        string title,
        string? note,
        IReadOnlyList<SettingsField> fields,
        IReadOnlyList<string> buttons)
    {
        TitleText.Text = title;

        if (note is { Length: > 0 })
        {
            NoteText.Text = note;
            NoteText.Visibility = Visibility.Visible;
        }

        foreach (SettingsField field in fields)
        {
            if (field is { Min: { } low, Max: { } high })
            {
                Fields.Children.Add(Slide(field, low, high));
                continue;
            }

            Control box = field.Secret
                ? new PasswordBox
                {
                    Header = field.Label,
                    PlaceholderText = field.Placeholder,
                    IsEnabled = field.Enabled,
                }
                : new TextBox
                {
                    Header = field.Label,
                    PlaceholderText = field.Placeholder,
                    Text = field.Value,
                    IsEnabled = field.Enabled,
                };

            Fields.Children.Add(box);

            // Only enabled boxes are collected: a disabled one is being shown, not asked for,
            // and returning its text would let a fixed value be saved as if it were entered.
            if (field.Enabled)
                _boxes[field.Key] = box;
        }

        for (int i = 0; i < buttons.Count; i++)
        {
            string caption = buttons[i];

            var button = new Button
            {
                Content = caption,
                MinWidth = 96,
                Style = i == 0 && Application.Current.Resources.TryGetValue(
                    "AccentButtonStyle", out object? accent) && accent is Style style
                        ? style
                        : null,
            };

            button.Click += (_, _) => Answer(caption);
            Buttons.Children.Add(button);
        }

        MakeDraggable(Header);
        MakeDraggable(Surface);

        RootHost.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                Answer(null);
            }
        };
    }

    private void Answer(string? button)
    {
        if (_answered)
            return;

        _answered = true;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string key, Control box) in _boxes)
        {
            values[key] = box switch
            {
                PasswordBox password => password.Password,
                TextBox text => text.Text,

                // As an integer, invariantly. A slider's Value is a double, and a German
                // culture would render 30 as "30" but 30.5 as "30,5" -- which the caller
                // then cannot parse. Rounding here means the caller gets what the control
                // means rather than what it stores.
                Slider slider => ((int)Math.Round(slider.Value))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),

                _ => string.Empty,
            };
        }

        _done.TrySetResult(new SettingsResult(button, values));

        // Closed raises this again with null, which the guard above absorbs.
        try
        {
            Close();
        }
        catch (Exception)
        {
            // Already closing.
        }
    }

    /// <summary>Centre it on the monitor the pill is on, sized to what it has to show.</summary>
    private void PlaceBeside(nint besideWindow, int fieldCount)
    {
        double scale = _shaper.Scale;

        // Measured in DIPs and converted once. A form is as tall as its fields: 74 per box is
        // a header, a box and the spacing, and the rest is the title, the note and the buttons.
        int width = (int)Math.Round(520 * scale);
        int height = (int)Math.Round(Math.Min(150 + (fieldCount * 74), 640) * scale);

        DisplayArea area = besideWindow == 0
            ? DisplayArea.Primary
            : DisplayArea.GetFromWindowId(
                Win32Interop.GetWindowIdFromWindow(besideWindow), DisplayAreaFallback.Nearest);

        AppWindow.MoveAndResize(new RectInt32(
            area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
            area.WorkArea.Y + ((area.WorkArea.Height - height) / 3),
            width,
            height));

        // The frame goes, then the glass, then the clip -- the same order and the same reasons
        // as the answer window. See WindowShaper.TrimFrame.
        _shaper.TrimFrame(keepResizeBorder: false);
        _shaper.TrySoftenEdges();
        _shaper.ClipWindowRounded(8);
    }

    /// <summary>Drag by the surface, the same way the pill and the answer window do.</summary>
    private void MakeDraggable(UIElement surface)
    {
        surface.PointerPressed += (sender, e) =>
        {
            // Only the blank parts drag. A press that started on a box or a button belongs
            // to that control, and dragging the window out from under it would eat the click.
            if (e.OriginalSource is TextBox or PasswordBox or Button)
                return;

            _dragging = true;
            _dragFrom = CursorPosition();
            _windowFrom = AppWindow.Position;
            ((UIElement)sender).CapturePointer(e.Pointer);
        };

        surface.PointerMoved += (_, _) =>
        {
            if (!_dragging)
                return;

            PointInt32 now = CursorPosition();

            AppWindow.Move(new PointInt32(
                _windowFrom.X + (now.X - _dragFrom.X),
                _windowFrom.Y + (now.Y - _dragFrom.Y)));
        };

        surface.PointerReleased += (sender, e) =>
        {
            _dragging = false;
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);

            // The clip is in window coordinates, so it does not move with the window -- but it
            // is recut anyway, because a monitor change alters the scale factor.
            _shaper.ClipWindowRounded(8);
        };
    }

    private static PointInt32 CursorPosition() =>
        Windows.Win32.PInvoke.GetCursorPos(out System.Drawing.Point cursor)
            ? new PointInt32(cursor.X, cursor.Y)
            : default;
}
