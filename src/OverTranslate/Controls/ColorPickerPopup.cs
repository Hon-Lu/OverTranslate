using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace OverTranslate.Controls;

/// <summary>
/// The colour picker in a panel of its own, opened from a colour button and closed by clicking
/// anywhere else or Esc (#355).
/// </summary>
/// <remarks>
/// <para>Built as 可用按鍵 on the settings page is: right edge under the button's right edge, grown
/// out of that corner, a shadow round it. One per page is enough — opening it from a second button
/// moves it there.</para>
///
/// <para>Nothing to confirm: a colour is handed to the caller as it is applied, the same as on the
/// realtime bar's panel, so the page behind follows while the panel is still open. Esc and a click
/// outside both just close it.</para>
/// </remarks>
public sealed class ColorPickerPopup : Popup
{
    private readonly ColorPicker _picker = new() { Width = 300 };
    private readonly Border _frame;
    private readonly Border _panel;

    private Action<Color>? _commit;

    // Set when the press that closed the panel was on the button it was opened from, so the click
    // that press becomes does not open it again straight away.
    private bool _closedByOwnButton;

    public ColorPickerPopup()
    {
        AllowsTransparency = true;
        StaysOpen = false;
        Placement = PlacementMode.Bottom;

        _panel = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Focusable = true,
            FocusVisualStyle = null,
            RenderTransformOrigin = new System.Windows.Point(1, 0),
            RenderTransform = new ScaleTransform(),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.28 },
            Child = _picker,
        };
        _panel.SetResourceReference(Border.BackgroundProperty, "AppSurfaceBg");
        _panel.SetResourceReference(Border.BorderBrushProperty, "AppBorder");
        // On the way up rather than down, so Esc in the code box puts the code back first (see
        // ColorPicker) and closing then has nothing typed left to apply.
        _panel.KeyDown += Panel_KeyDown;

        // The padding is room for the shadow inside the popup's own window.
        _frame = new Border { Padding = new Thickness(14, 4, 14, 18), Child = _panel };
        Child = _frame;

        _picker.ColorCommitted += (_, color) => _commit?.Invoke(color);
    }

    /// <summary>The button the panel is open under, or null while it is closed.</summary>
    public FrameworkElement? OpenOn => IsOpen ? PlacementTarget as FrameworkElement : null;

    /// <summary>
    /// Opens the panel under <paramref name="button"/> on <paramref name="current"/>, or closes it
    /// when it is already open there. Each colour applied goes to <paramref name="commit"/>.
    /// </summary>
    public void Toggle(FrameworkElement button, Color current, Action<Color> commit)
    {
        var sameButton = ReferenceEquals(PlacementTarget, button);
        if (_closedByOwnButton && sameButton)
        {
            _closedByOwnButton = false;
            return;
        }
        _closedByOwnButton = false;

        if (IsOpen)
        {
            IsOpen = false;
            if (sameButton) return;
        }

        _commit = commit;
        _picker.Begin(Color.FromRgb(current.R, current.G, current.B));
        PlacementTarget = button;

        // Measured rather than fixed: the panel is as wide as the picker plus its padding, and the
        // frame's own padding is shadow, so the panel's right edge is the frame's less that.
        _frame.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        HorizontalOffset = button.ActualWidth - _frame.DesiredSize.Width + _frame.Padding.Right;
        IsOpen = true;
    }

    /// <summary>
    /// Grows the panel out of its button's corner. Short and without overshoot; with animations
    /// turned off in Windows it simply appears.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (PlacementTarget is Control button)
            button.SetResourceReference(Control.BorderBrushProperty, "AppInputFocusBorder");

        if (SystemParameters.ClientAreaAnimation)
        {
            var scale = (ScaleTransform)_panel.RenderTransform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(160);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
            _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        }

        // So Esc reaches the panel: the popup is its own window and has no focus of its own.
        _panel.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // A code typed and not yet entered is applied, not lost, as it is on leaving the box.
        _picker.CommitPendingCode();

        if (PlacementTarget is Control button)
        {
            button.ClearValue(Control.BorderBrushProperty);
            _closedByOwnButton = button.IsMouseOver && Mouse.LeftButton == MouseButtonState.Pressed;
        }
    }

    private void Panel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        IsOpen = false;
        (PlacementTarget as UIElement)?.Focus();
        e.Handled = true;
    }
}
