using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OverTranslate.Controls;

/// <summary>
/// The name of the button under the pointer, and a line on what it does, in a card that points at
/// it.
/// </summary>
/// <remarks>
/// <para>The toolbars' buttons are mostly icons alone: the words under the capture toolbar's were
/// what pushed it off the side of the screen in the longer languages (#349). What the words were
/// doing — saying what a button is before it is pressed — is done here instead, and it has to be
/// quick and easy to find for that to be a fair trade. A ToolTip is neither: it waits, comes up
/// wherever the cursor stopped, and closes and reopens from nothing at every button.</para>
///
/// <para>So one card for the whole bar. The first one waits a moment, long enough not to flash up as
/// the pointer crosses the bar on its way somewhere else; after that, moving to the next button
/// slides the card across with nothing to wait for, so a pass along the row reads every name.
/// Pressing puts it away until the pointer leaves that button: what the press did is the thing to
/// look at then.</para>
///
/// <para>Which side it grows to is the bar's to say (<see cref="PrefersAbove"/>); it goes to the
/// other side only when the screen ends first. Shared by the capture toolbar and the realtime
/// control bar, so the two read as one family.</para>
/// </remarks>
public partial class ToolbarLabel : Popup
{
    private const int ShowDelayMs = 100;
    private const int WarmMs = 500;
    private const int LeaveGraceMs = 90;
    private const int FlashMs = 1200;
    private const int InMs = 140;
    private const int OutMs = 90;
    private const int SlideMs = 160;

    // Between the button and the card's edge; the arrow lives in it.
    private const double Gap = 9;

    private readonly List<Target> _targets = new();
    private FrameworkElement _bar = null!;
    private Target? _current;
    private Target? _suppressed;

    // The button the card is on, or sliding to.
    private FrameworkElement? _button;
    private bool _visible;
    private bool _warm;
    private bool _flashing;

    private DispatcherTimer? _showTimer;
    private DispatcherTimer? _hideTimer;
    private DispatcherTimer? _coolTimer;
    private DispatcherTimer? _flashTimer;

    private sealed record Target(FrameworkElement Element, Func<string> Name, Func<string?> Description);

    public ToolbarLabel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Whether the card would rather sit over the bar than under it, asked each time it is placed.
    /// </summary>
    public Func<bool>? PrefersAbove { get; set; }

    /// <summary>
    /// Has the card follow the pointer over <paramref name="bar"/>, the surface the buttons sit on,
    /// for as long as <paramref name="window"/> is open.
    /// </summary>
    public void Attach(Window window, FrameworkElement bar)
    {
        _bar = bar;
        PlacementTarget = bar;

        // On the bar rather than on each button: a disabled button raises no mouse events of its own,
        // and the capture toolbar's 顯示原文 is disabled until there is a translation — which is
        // exactly when someone is most likely to wonder what it is.
        bar.MouseMove += (_, e) => Track(e);
        bar.MouseLeave += (_, _) => Leave();
        bar.PreviewMouseLeftButtonDown += (_, _) => Press();
        window.LocationChanged += (_, _) => Hide(animate: false);
        window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) Hide(animate: false); };

        // Which side, how far up and where the popup starts follow the card's size — known for
        // certain only once it has been laid out with its text, which for a popup just opened can be
        // a pass later.
        LabelPanel.SizeChanged += (_, _) => PlaceFrame();
        window.Closed += (_, _) =>
        {
            foreach (var timer in new[] { _showTimer, _hideTimer, _coolTimer, _flashTimer })
                timer?.Stop();
            IsOpen = false;
        };
    }

    /// <summary>
    /// Has <paramref name="element"/> named by the card, with the line under the name taken from
    /// <paramref name="description"/>, or from its ToolTip when there is none.
    /// </summary>
    /// <remarks>
    /// <para>The ToolTip stays set but never opens: where it already says what a button does, in
    /// every language — and where it changes with the button's state — reading it keeps one place to
    /// say it. A ToolTip that only repeats the name adds nothing under it. The buttons that never had
    /// one pass their line in.</para>
    ///
    /// <para>Kept from opening rather than switched off. A button whose ToolTip is switched off is
    /// passed over when WPF looks for one to show, and the search goes on to the button's parents:
    /// the collapsed realtime capsule's own tooltip came up under the card, saying something else.
    /// A button that has a ToolTip and cancels it ends the search there.</para>
    /// </remarks>
    public void Add(FrameworkElement element, Func<string> name, Func<string?>? description = null)
    {
        element.ToolTip ??= string.Empty;
        element.ToolTipOpening += (_, e) => e.Handled = true;
        _targets.Add(new Target(element, name, description ?? (() =>
        {
            var tip = element.ToolTip as string;
            return string.IsNullOrWhiteSpace(tip) || tip == name() ? null : tip;
        })));
    }

    /// <summary>Has the button just pressed say that it worked, in the card, for a moment.</summary>
    public void Flash(FrameworkElement element, string message)
    {
        var target = _targets.FirstOrDefault(t => t.Element == element);
        if (target is null) return;

        _flashing = true;
        _suppressed = target;
        _hideTimer?.Stop();
        _showTimer?.Stop();
        Place(target, slide: _visible, flash: message);

        _flashTimer ??= NewOneShotTimer(FlashMs, () =>
        {
            _flashing = false;
            Hide(animate: true);
        });
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private void Track(System.Windows.Input.MouseEventArgs e)
    {
        var target = _targets.FirstOrDefault(t => t.Element.IsVisible && IsUnder(t.Element, e));
        if (target == _current) return;

        if (target is null)
        {
            // Crossing the few pixels between two buttons is not leaving, or the card could never
            // slide to the next one; it goes only if nothing else is reached in a moment.
            _current = null;
            _suppressed = null;
            _showTimer?.Stop();
            if (_visible && !_flashing)
            {
                _hideTimer ??= NewOneShotTimer(LeaveGraceMs, () => Hide(animate: true));
                _hideTimer.Stop();
                _hideTimer.Start();
            }
            return;
        }

        Enter(target);
    }

    private static bool IsUnder(FrameworkElement element, System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(element);
        return p.X >= 0 && p.Y >= 0 && p.X < element.ActualWidth && p.Y < element.ActualHeight;
    }

    private void Enter(Target target)
    {
        _current = target;
        _showTimer?.Stop();
        _hideTimer?.Stop();
        _coolTimer?.Stop();
        if (target == _suppressed) return;
        if (_flashing)
        {
            _flashing = false;
            _flashTimer?.Stop();
        }

        if (_visible)
        {
            Place(target, slide: true);
        }
        else if (_warm)
        {
            Place(target, slide: false);
        }
        else
        {
            _showTimer ??= NewOneShotTimer(ShowDelayMs, () =>
            {
                if (_current is { } current && current != _suppressed)
                    Place(current, slide: false);
            });
            _showTimer.Start();
        }
    }

    private void Leave()
    {
        _current = null;
        _suppressed = null;
        _showTimer?.Stop();
        if (!_flashing) Hide(animate: true);
    }

    private void Press()
    {
        _suppressed = _current;
        _showTimer?.Stop();
        if (!_flashing) Hide(animate: false);
    }

    private void Place(Target target, bool slide, string? flash = null)
    {
        if (PresentationSource.FromVisual(_bar) is null) return;

        var description = flash is null ? target.Description() : null;
        LabelName.Text = flash ?? target.Name();
        LabelName.SetResourceReference(TextBlock.ForegroundProperty, flash is null ? "TooltipFg" : "AppSuccess");
        LabelTick.Visibility = flash is null ? Visibility.Collapsed : Visibility.Visible;
        LabelDescription.Text = description ?? "";
        LabelDescription.Visibility = description is null ? Visibility.Collapsed : Visibility.Visible;

        var element = target.Element;
        _button = element;
        double anchor = element.TranslatePoint(new System.Windows.Point(0, 0), _bar).X
            + element.ActualWidth / 2;

        bool wasVisible = _visible;
        bool animate = SystemParameters.ClientAreaAnimation;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        LabelPanel.Span = _bar.ActualWidth;
        SetScreenLimits();

        if (wasVisible && slide && animate)
        {
            // From where it is now, so a pass that changes its mind halfway does not jump. The panel
            // lays the card and the arrow out again at every step, so the two travel as one.
            LabelPanel.BeginAnimation(ToolbarLabelPanel.AnchorProperty,
                new DoubleAnimation(anchor, TimeSpan.FromMilliseconds(SlideMs)) { EasingFunction = ease });
        }
        else
        {
            LabelPanel.BeginAnimation(ToolbarLabelPanel.AnchorProperty, null);
            LabelPanel.Anchor = anchor;
        }

        if (!wasVisible)
        {
            // Opened unseen and laid out before anything is shown, so the first frame already has
            // the card's size, side and place.
            LabelPanel.BeginAnimation(OpacityProperty, null);
            LabelPanel.Opacity = 0;
            IsOpen = true;
        }

        _visible = true;
        _warm = true;
        LabelPanel.UpdateLayout();
        PlaceFrame();

        if (wasVisible)
        {
            LabelPanel.BeginAnimation(OpacityProperty, null);
            LabelPanel.Opacity = 1;
            return;
        }

        if (!animate)
        {
            LabelPanel.Opacity = 1;
            return;
        }

        // Faded in, and nothing else. It used to rise a few pixels towards the button as well, and
        // text under a moving transform is drawn the way WPF draws animated text, then eased back to
        // its sharp form about a second after the movement stops: measured, the name stayed blurred
        // until 950ms with the lift and was sharp by 350ms without it. A fade leaves the text where
        // it is.
        LabelPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(InMs)) { EasingFunction = ease });
    }

    /// <summary>
    /// Puts the popup where the panel has laid the card out: under or over its button, from the
    /// card's measured height, and starting where the panel's frame does.
    /// </summary>
    /// <remarks>
    /// Run again whenever the card changes size, not only when it is placed: the card's size is what
    /// the popup's position depends on that only layout can supply.
    /// </remarks>
    private void PlaceFrame()
    {
        if (!_visible || _button is not { } button) return;
        if (PresentationSource.FromVisual(_bar) is null) return;

        double height = LabelPanel.CardHeight;
        if (height <= 0) return;

        double top = button.TranslatePoint(new System.Windows.Point(0, 0), _bar).Y;
        bool above = GoesAbove(top, button.ActualHeight, height);

        LabelPanel.Above = above;
        HorizontalOffset = LabelPanel.FrameLeft;
        VerticalOffset = above
            ? top - Gap - height - ToolbarLabelPanel.Edge
            : top + button.ActualHeight + Gap - ToolbarLabelPanel.Edge;
    }

    /// <summary>
    /// Tells the panel where the screen's usable area ends either side, in the bar's own units, so
    /// the popup never runs off it — Windows would push it back whole, and the arrow with it.
    /// </summary>
    /// <remarks>
    /// The scale is read off the bar rather than the window: the realtime bar is scaled inside its
    /// window for a monitor at another DPI, and two points a known distance apart on the bar say
    /// what one of its units is on the screen whatever stands between.
    /// </remarks>
    private void SetScreenLimits()
    {
        var origin = _bar.PointToScreen(new System.Windows.Point(0, 0));
        double scale = (_bar.PointToScreen(new System.Windows.Point(100, 0)).X - origin.X) / 100;
        if (scale <= 0) return;

        var wa = System.Windows.Forms.Screen
            .FromPoint(new System.Drawing.Point((int)(origin.X + _bar.ActualWidth * scale / 2), (int)origin.Y))
            .WorkingArea;
        LabelPanel.ScreenLeft = (wa.Left - origin.X) / scale;
        LabelPanel.ScreenRight = (wa.Right - origin.X) / scale;
    }

    /// <summary>Whether the card goes over the button rather than under it.</summary>
    private bool GoesAbove(double buttonTop, double buttonHeight, double cardHeight)
    {
        bool fitsBelow = true, fitsAbove = true;
        var source = PresentationSource.FromVisual(_bar);
        if (source?.CompositionTarget is { } target)
        {
            double scale = target.TransformToDevice.M22;
            var barTop = _bar.PointToScreen(new System.Windows.Point(0, 0));
            var wa = System.Windows.Forms.Screen
                .FromPoint(new System.Drawing.Point((int)barTop.X, (int)barTop.Y))
                .WorkingArea;
            fitsBelow = barTop.Y + (buttonTop + buttonHeight + Gap + cardHeight) * scale <= wa.Bottom;
            fitsAbove = barTop.Y + (buttonTop - Gap - cardHeight) * scale >= wa.Top;
        }

        return PrefersAbove?.Invoke() == true ? fitsAbove || !fitsBelow : !fitsBelow && fitsAbove;
    }

    /// <summary>Takes the card away at once, for a bar that is about to change under it.</summary>
    public void Hide() => Hide(animate: false);

    private void Hide(bool animate)
    {
        _hideTimer?.Stop();
        if (!_visible) return;
        _visible = false;
        _flashing = false;
        _flashTimer?.Stop();

        _coolTimer ??= NewOneShotTimer(WarmMs, () => _warm = false);
        _coolTimer.Stop();
        _coolTimer.Start();

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            LabelPanel.BeginAnimation(OpacityProperty, null);
            IsOpen = false;
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(OutMs));
        fade.Completed += (_, _) => { if (!_visible) IsOpen = false; };
        LabelPanel.BeginAnimation(OpacityProperty, fade);
    }

    private DispatcherTimer NewOneShotTimer(int ms, Action onElapsed)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(ms),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            onElapsed();
        };
        return timer;
    }
}
