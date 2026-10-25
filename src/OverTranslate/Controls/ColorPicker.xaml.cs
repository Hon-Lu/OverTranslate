using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace OverTranslate.Controls;

/// <summary>
/// A colour picker of our own: a saturation/value square, a hue strip, the colour's code, 返回 and
/// eight colours to start from (#355).
/// </summary>
/// <remarks>
/// <para>Ours rather than the system dialog, which is modal and takes the foreground: opened from
/// the realtime bar, it would pull focus off the game being translated and sit on top of the very
/// subtitles the colour is for. This one lives inside whatever hosts it and needs nothing from the
/// keyboard except for the code.</para>
///
/// <para>A colour reaches the host only when it is applied — a drag let go, a swatch pressed, a code
/// entered — never on every step of a drag. What the host does with a colour can be expensive (the
/// realtime overlays are rebuilt for it), and a drag is the user still looking for one. While they
/// look, only the picker's own swatch, handles and code follow the pointer.</para>
///
/// <para>Every colour applied since <see cref="Begin"/> can be stepped back through with 返回; see
/// <see cref="ColorPickHistory"/>.</para>
/// </remarks>
public partial class ColorPicker : UserControl
{
    /// <summary>
    /// The eight under the picker, in one row: six hues in the order of the strip, then white and
    /// black together at the end, where the two colours the overlay ships with are always found.
    /// </summary>
    public static readonly IReadOnlyList<Color> Presets =
    [
        Color.FromRgb(0xFF, 0x45, 0x3A),
        Color.FromRgb(0xFF, 0x9F, 0x0A),
        Color.FromRgb(0xFF, 0xD6, 0x0A),
        Color.FromRgb(0x30, 0xD1, 0x58),
        Color.FromRgb(0x0A, 0x84, 0xFF),
        Color.FromRgb(0xBF, 0x5A, 0xF2),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0x00, 0x00, 0x00),
    ];

    private readonly ColorPickHistory _history = new();
    private readonly List<Button> _presetButtons = [];

    // What the handles and the swatch show — ahead of the applied colour while a drag is under way.
    private HsvColor _draft = new(0, 0, 1);
    private Color _applied = Colors.White;

    // Whichever area holds the mouse, between the press and the release.
    private FrameworkElement? _dragging;

    // Set while the code box is being written to from here, so that is not taken for typing.
    private bool _showingHex;

    public ColorPicker()
    {
        InitializeComponent();

        foreach (var color in Presets)
        {
            var hex = Format(color);
            var swatch = new Button
            {
                Style = (Style)FindResource("PresetSwatch"),
                Background = Frozen(color),
                ToolTip = hex,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, hex);
            swatch.Click += (_, _) => Apply(color);
            _presetButtons.Add(swatch);
            PresetGrid.Children.Add(swatch);
        }

        SquareArea.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "S.ColorPicker.Square");
        HueArea.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "S.ColorPicker.Hue");

        Show(_draft);
    }

    /// <summary>Raised with each colour applied: a drag let go, a swatch, a code entered, 返回.</summary>
    public event EventHandler<Color>? ColorCommitted;

    /// <summary>The colour last applied.</summary>
    public Color Color => _applied;

    /// <summary>
    /// Opens the picker on <paramref name="color"/> with nothing to step back to: whatever was
    /// applied the last time it was open is the user's starting point now, not a step of this visit.
    /// </summary>
    public void Begin(Color color)
    {
        EndDrag(commit: false);
        _history.Clear();
        _applied = color;
        Show(HsvColor.FromColor(color, _draft.Hue));
    }

    // ── Square and strip ─────────────────────────────────────────────────────────────────────────

    private void Area_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var area = (FrameworkElement)sender;
        _dragging = area;
        area.CaptureMouse();
        Track(area, e.GetPosition(area));
        e.Handled = true;
    }

    private void Area_MouseMove(object sender, MouseEventArgs e)
    {
        if (!ReferenceEquals(_dragging, sender)) return;
        var area = (FrameworkElement)sender;
        Track(area, e.GetPosition(area));
    }

    private void Area_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(_dragging, sender)) return;
        ((FrameworkElement)sender).ReleaseMouseCapture();
    }

    // The one place a drag ends, however it ends — let go, or the capture taken away — so it is
    // applied exactly once.
    private void Area_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(_dragging, sender)) EndDrag(commit: true);
    }

    private void EndDrag(bool commit)
    {
        if (_dragging is not { } area) return;
        _dragging = null;
        if (area.IsMouseCaptured) area.ReleaseMouseCapture();
        if (commit) Apply(_draft.ToColor(), _draft.Hue);
    }

    private void Track(FrameworkElement area, System.Windows.Point point)
    {
        double x = area.ActualWidth > 0 ? Math.Clamp(point.X / area.ActualWidth, 0, 1) : 0;

        if (ReferenceEquals(area, SquareArea))
        {
            double y = area.ActualHeight > 0 ? Math.Clamp(point.Y / area.ActualHeight, 0, 1) : 0;
            Show(_draft with { Saturation = x, Value = 1 - y });
        }
        else
        {
            // Short of 360, which is the strip's left end again.
            Show(_draft with { Hue = Math.Min(x * 360, 359.999) });
        }
    }

    private void Area_SizeChanged(object sender, SizeChangedEventArgs e) => PlaceThumbs();

    // ── Applying and stepping back ───────────────────────────────────────────────────────────────

    private void Apply(Color color) => Apply(color, _draft.Hue);

    private void Apply(Color color, double hue)
    {
        Show(HsvColor.FromColor(color, hue));
        if (color == _applied) return;

        _history.Record(_applied, color);
        _applied = color;
        UndoBtn.IsEnabled = _history.CanUndo;
        ColorCommitted?.Invoke(this, color);
    }

    private void UndoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_history.TryUndo(out var previous)) return;

        _applied = previous;
        Show(HsvColor.FromColor(previous, _draft.Hue));
        ColorCommitted?.Invoke(this, previous);
    }

    // ── The code ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a code as typed: six hex digits, with or without the #. Anything else is not a colour
    /// yet — the box goes back to the one in use rather than guessing.
    /// </summary>
    internal static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var digits = (text ?? "").Trim().TrimStart('#');
        if (digits.Length != 6 ||
            !int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            return false;

        color = Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    internal static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    // A click anywhere in the field, the HEX label included, is a click in the box.
    private void HexField_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (HexBox.IsMouseOver) return;
        HexBox.Focus();
        HexBox.SelectAll();
        e.Handled = true;
    }

    // The first click into the box takes the whole code, so typing replaces it rather than running
    // into the length limit behind it; later clicks place the caret as usual.
    private void HexBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (HexBox.IsKeyboardFocusWithin) return;
        HexBox.Focus();
        HexBox.SelectAll();
        e.Handled = true;
    }

    private void HexBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(c => c == '#' || Uri.IsHexDigit(c));

    // A paste with anything but a code in it is refused whole: half a pasted code is another colour.
    private void HexBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !TryParseHex(text, out _))
            e.CancelCommand();
    }

    private void HexBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitHex();
                e.Handled = true;
                break;
            case Key.Escape:
                ShowHex(_applied);
                e.Handled = false; // the host gives the keyboard back on the same key
                break;
            case Key.Space:
                e.Handled = true;
                break;
        }
    }

    // The handles follow a code as soon as it is a whole one, the way they follow a drag; it is
    // applied on Enter or on leaving the box.
    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_showingHex || !TryParseHex(HexBox.Text, out var typed)) return;
        ShowHandles(HsvColor.FromColor(typed, _draft.Hue));
    }

    private void HexBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HexField.SetResourceReference(Border.BorderBrushProperty, "AppInputFocusBorder");
        HexBox.SelectAll();
    }

    private void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        HexField.SetResourceReference(Border.BorderBrushProperty, "AppInputBorder");
        CommitHex();
    }

    /// <summary>
    /// Applies a code typed and not yet entered, for a host that knows the typing is over when the
    /// box itself has not been told — the keyboard taken away by another application.
    /// </summary>
    public void CommitPendingCode() => CommitHex();

    private void CommitHex()
    {
        if (TryParseHex(HexBox.Text, out var typed)) Apply(typed);
        else Show(HsvColor.FromColor(_applied, _draft.Hue));
    }

    // ── Drawing ──────────────────────────────────────────────────────────────────────────────────

    private void Show(HsvColor hsv)
    {
        ShowHandles(hsv);
        ShowHex(hsv.ToColor());
        UndoBtn.IsEnabled = _history.CanUndo;
    }

    /// <summary>Everything but the code box: the square's hue, both handles, the swatch, the ring.</summary>
    private void ShowHandles(HsvColor hsv)
    {
        _draft = hsv;
        var color = hsv.ToColor();

        HueFill.Background = Frozen(hsv.PureHue());
        SquareThumb.Fill = Frozen(color);
        HueThumb.Fill = Frozen(hsv.PureHue());
        DraftSwatch.Background = Frozen(color);

        foreach (var swatch in _presetButtons)
            swatch.Tag = swatch.Background is SolidColorBrush brush && brush.Color == color ? "Selected" : null;

        PlaceThumbs();
    }

    private void ShowHex(Color color)
    {
        _showingHex = true;
        HexBox.Text = Format(color);
        if (HexBox.IsKeyboardFocused) HexBox.SelectAll();
        _showingHex = false;
    }

    private void PlaceThumbs()
    {
        Canvas.SetLeft(SquareThumb, _draft.Saturation * SquareArea.ActualWidth - SquareThumb.Width / 2);
        Canvas.SetTop(SquareThumb, (1 - _draft.Value) * SquareArea.ActualHeight - SquareThumb.Height / 2);
        Canvas.SetLeft(HueThumb, _draft.Hue / 360 * HueArea.ActualWidth - HueThumb.Width / 2);
        Canvas.SetTop(HueThumb, (HueArea.ActualHeight - HueThumb.Height) / 2);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
