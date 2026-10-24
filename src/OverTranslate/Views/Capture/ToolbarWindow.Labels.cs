using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OverTranslate.Services;

namespace OverTranslate.Views.Capture;

/// <summary>
/// The name of the button under the pointer, and a line on what it does, in a card that points at
/// it.
/// </summary>
/// <remarks>
/// <para>Most of the bar's buttons are icons alone: the words under them were what pushed the bar off
/// the side of the screen in the longer languages (#349). What the words were doing — saying what a
/// button is before it is pressed — is done here instead, and it has to be quick and easy to find
/// for that to be a fair trade. A ToolTip is neither: it waits 400ms, comes up wherever the cursor
/// stopped, and closes and reopens from nothing at every button.</para>
///
/// <para>So one card for the whole bar. The first one waits a moment, long enough not to flash up as
/// the pointer crosses the bar on its way to the selection; after that, moving to the next button
/// slides the card across with nothing to wait for, so a pass along the row reads every name.
/// Pressing puts it away until the pointer leaves that button: what the press did is the thing to
/// look at then.</para>
///
/// <para>It grows away from the selection — under the bar when the bar is under the selection, over
/// it when the bar sits above — so it never lands on the text being read, and goes to the other
/// side only when the screen ends first.</para>
/// </remarks>
public partial class ToolbarWindow
{
    private const int LabelShowDelayMs = 100;
    private const int LabelWarmMs = 500;
    private const int LabelLeaveGraceMs = 90;
    private const int LabelFlashMs = 1200;
    private const int LabelInMs = 140;
    private const int LabelOutMs = 90;
    private const int LabelSlideMs = 160;

    // Between the button and the card's edge; the arrow lives in it.
    private const double LabelGap = 9;

    // Room around the card inside the popup for its shadow, and above or below it for the arrow.
    private const double LabelEdge = 10;

    private readonly List<LabelTarget> _labelTargets = new();
    private LabelTarget? _labelCurrent;
    private LabelTarget? _labelSuppressed;
    private bool _labelVisible;
    private bool _labelWarm;
    private bool _labelFlashing;

    // Set where the bar is placed: whether it ended up above the selection.
    private bool _labelsPreferAbove;

    private DispatcherTimer? _labelShowTimer;
    private DispatcherTimer? _labelHideTimer;
    private DispatcherTimer? _labelCoolTimer;
    private DispatcherTimer? _labelFlashTimer;

    private sealed record LabelTarget(FrameworkElement Element, Func<string> Name, Func<string?> Description);

    private void InitializeLabels()
    {
        AddLabel(TtsBtn, () => LocalizationService.Get(_isSpeaking ? "S.Toolbar.SpeakStopLabel" : "S.Toolbar.Speak"));
        AddLabel(SwapBtn, () => LocalizationService.Get("S.Toolbar.Swap"));
        AddLabel(ToggleBtn,
            () => LocalizationService.Get(_bubblesVisible ? "S.Toolbar.ShowSource" : "S.Toolbar.ShowTranslation"),
            () => LocalizationService.Get(_bubblesVisible ? "S.Toolbar.ShowSourceHint" : "S.Toolbar.ShowTranslationHint"));
        AddLabel(CopyTextBtn,
            () => LocalizationService.Get(CopiesTranslation() ? "S.Toolbar.CopyTranslation" : "S.Toolbar.CopyText"),
            () => LocalizationService.Get(CopiesTranslation() ? "S.Toolbar.CopyTranslationHint" : "S.Toolbar.CopyTextHint"));
        AddLabel(AnnotateBtn, () => LocalizationService.Get("S.Toolbar.Annotate"));
        AddLabel(CopyShotBtn,
            () => LocalizationService.Get("S.Toolbar.Screenshot"),
            () => LocalizationService.Get("S.Toolbar.ScreenshotHint"));
        AddLabel(OpenWindowBtn, () => LocalizationService.Get("S.Toolbar.OpenWindow"));
        AddLabel(MoreBtn,
            () => LocalizationService.Get("S.Shell.Settings"),
            () => LocalizationService.Get("S.Toolbar.SettingsHint"));
        AddLabel(CloseBtn, () => LocalizationService.Get("S.Toolbar.Close"));

        // On the bar rather than on each button: a disabled button raises no mouse events of its own,
        // and 顯示原文 is disabled until there is a translation — which is exactly when someone is
        // most likely to wonder what it is.
        BarSurface.MouseMove += (_, e) => TrackLabel(e);
        BarSurface.MouseLeave += (_, _) => LeaveLabels();
        BarSurface.PreviewMouseLeftButtonDown += (_, _) => PressLabel();
        LocationChanged += (_, _) => HideLabel(animate: false);
        Closed += (_, _) =>
        {
            foreach (var timer in new[] { _labelShowTimer, _labelHideTimer, _labelCoolTimer, _labelFlashTimer })
                timer?.Stop();
            LabelPopup.IsOpen = false;
        };
    }

    /// <summary>
    /// Has <paramref name="element"/> named by the card, with the line under the name taken from
    /// <paramref name="description"/>, or from its ToolTip when there is none.
    /// </summary>
    /// <remarks>
    /// The ToolTip stays set but switched off: where it already says what a button does, in every
    /// language — and for the speak button it changes with the state — reading it keeps one place to
    /// say it. A ToolTip that only repeats the name adds nothing under it. The buttons that never had
    /// one, because their word used to be enough, pass their line in.
    /// </remarks>
    private void AddLabel(FrameworkElement element, Func<string> name, Func<string?>? description = null)
    {
        ToolTipService.SetIsEnabled(element, false);
        _labelTargets.Add(new LabelTarget(element, name, description ?? (() =>
        {
            var tip = element.ToolTip as string;
            return string.IsNullOrWhiteSpace(tip) || tip == name() ? null : tip;
        })));
    }

    private bool CopiesTranslation() =>
        ResolveCopyTextKind(_hasTranslated, _bubblesVisible) == CopyTextKind.Translation;

    /// <summary>Has the button just pressed say that it worked, in the card, for a moment.</summary>
    private void FlashLabel(FrameworkElement element, string message)
    {
        var target = _labelTargets.FirstOrDefault(t => t.Element == element);
        if (target is null) return;

        _labelFlashing = true;
        _labelSuppressed = target;
        _labelHideTimer?.Stop();
        _labelShowTimer?.Stop();
        PlaceLabel(target, slide: _labelVisible, flash: message);

        _labelFlashTimer ??= NewOneShotTimer(LabelFlashMs, () =>
        {
            _labelFlashing = false;
            HideLabel(animate: true);
        });
        _labelFlashTimer.Stop();
        _labelFlashTimer.Start();
    }

    private void TrackLabel(System.Windows.Input.MouseEventArgs e)
    {
        var target = _labelTargets.FirstOrDefault(t => t.Element.IsVisible && IsUnder(t.Element, e));
        if (target == _labelCurrent) return;

        if (target is null)
        {
            // Crossing the few pixels between two buttons is not leaving, or the card could never
            // slide to the next one; it goes only if nothing else is reached in a moment.
            _labelCurrent = null;
            _labelSuppressed = null;
            _labelShowTimer?.Stop();
            if (_labelVisible && !_labelFlashing)
            {
                _labelHideTimer ??= NewOneShotTimer(LabelLeaveGraceMs, () => HideLabel(animate: true));
                _labelHideTimer.Stop();
                _labelHideTimer.Start();
            }
            return;
        }

        EnterLabel(target);
    }

    private static bool IsUnder(FrameworkElement element, System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(element);
        return p.X >= 0 && p.Y >= 0 && p.X < element.ActualWidth && p.Y < element.ActualHeight;
    }

    private void EnterLabel(LabelTarget target)
    {
        _labelCurrent = target;
        _labelShowTimer?.Stop();
        _labelHideTimer?.Stop();
        _labelCoolTimer?.Stop();
        if (target == _labelSuppressed) return;
        if (_labelFlashing)
        {
            _labelFlashing = false;
            _labelFlashTimer?.Stop();
        }

        if (_labelVisible)
        {
            PlaceLabel(target, slide: true);
        }
        else if (_labelWarm)
        {
            PlaceLabel(target, slide: false);
        }
        else
        {
            _labelShowTimer ??= NewOneShotTimer(LabelShowDelayMs, () =>
            {
                if (_labelCurrent is { } current && current != _labelSuppressed)
                    PlaceLabel(current, slide: false);
            });
            _labelShowTimer.Start();
        }
    }

    private void LeaveLabels()
    {
        _labelCurrent = null;
        _labelSuppressed = null;
        _labelShowTimer?.Stop();
        if (!_labelFlashing) HideLabel(animate: true);
    }

    private void PressLabel()
    {
        _labelSuppressed = _labelCurrent;
        _labelShowTimer?.Stop();
        if (!_labelFlashing) HideLabel(animate: false);
    }

    private void PlaceLabel(LabelTarget target, bool slide, string? flash = null)
    {
        if (PresentationSource.FromVisual(BarSurface) is null) return;

        var description = flash is null ? target.Description() : null;
        LabelName.Text = flash ?? target.Name();
        LabelName.SetResourceReference(TextBlock.ForegroundProperty, flash is null ? "TooltipFg" : "AppSuccess");
        LabelTick.Visibility = flash is null ? Visibility.Collapsed : Visibility.Visible;
        LabelDescription.Text = description ?? "";
        LabelDescription.Visibility = description is null ? Visibility.Collapsed : Visibility.Visible;

        LabelCard.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = LabelCard.DesiredSize.Width;
        double height = LabelCard.DesiredSize.Height;
        double barWidth = BarSurface.ActualWidth;

        var element = target.Element;
        var origin = element.TranslatePoint(new System.Windows.Point(0, 0), BarSurface);
        double centre = origin.X + element.ActualWidth / 2;

        // Kept inside the bar's span, with the arrow still on the button.
        double left = Math.Clamp(centre - width / 2, 0, Math.Max(0, barWidth - width));
        bool above = LabelGoesAbove(origin.Y, element.ActualHeight, height);
        double top = above
            ? origin.Y - LabelGap - height
            : origin.Y + element.ActualHeight + LabelGap;

        LabelCanvas.Width = barWidth + 2 * LabelEdge;
        LabelCanvas.Height = height + 2 * LabelEdge;
        Canvas.SetTop(LabelCard, LabelEdge);
        LabelPopup.HorizontalOffset = -LabelEdge;
        LabelPopup.VerticalOffset = top - LabelEdge;

        double arrowX = centre - left;
        LabelArrow.VerticalAlignment = above ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        LabelArrow.Margin = above
            ? new Thickness(arrowX - LabelArrow.Width / 2, 0, 0, -6)
            : new Thickness(arrowX - LabelArrow.Width / 2, -6, 0, 0);
        LabelArrowFlip.ScaleY = above ? -1 : 1;

        double x = LabelEdge + left;
        bool animate = SystemParameters.ClientAreaAnimation;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (slide && _labelVisible && animate)
        {
            // From where it is now, so a pass that changes its mind halfway does not jump.
            var slideTo = new DoubleAnimation(x, TimeSpan.FromMilliseconds(LabelSlideMs)) { EasingFunction = ease };
            LabelShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slideTo);
            LabelCard.BeginAnimation(OpacityProperty, null);
            LabelCard.Opacity = 1;
        }
        else if (_labelVisible)
        {
            LabelShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            LabelShift.X = x;
        }
        else
        {
            LabelShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            LabelShift.X = x;
            LabelPopup.IsOpen = true;

            if (animate)
            {
                // Grows out of the button: scaled about the arrow's tip, and drawn a few pixels
                // towards the button.
                LabelScale.CenterX = arrowX;
                LabelScale.CenterY = above ? height : 0;
                var duration = TimeSpan.FromMilliseconds(LabelInMs);
                LabelCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
                var grow = new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease };
                LabelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
                LabelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
                LabelShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    new DoubleAnimation(above ? 4 : -4, 0, duration) { EasingFunction = ease });
            }
            else
            {
                LabelCard.BeginAnimation(OpacityProperty, null);
                LabelCard.Opacity = 1;
            }
        }

        _labelVisible = true;
        _labelWarm = true;
    }

    /// <summary>Whether the card goes over the button rather than under it.</summary>
    private bool LabelGoesAbove(double buttonTop, double buttonHeight, double cardHeight)
    {
        bool fitsBelow = true, fitsAbove = true;
        var source = PresentationSource.FromVisual(BarSurface);
        if (source?.CompositionTarget is { } target)
        {
            double scale = target.TransformToDevice.M22;
            var barTop = BarSurface.PointToScreen(new System.Windows.Point(0, 0));
            var wa = System.Windows.Forms.Screen
                .FromPoint(new System.Drawing.Point((int)barTop.X, (int)barTop.Y))
                .WorkingArea;
            fitsBelow = barTop.Y + (buttonTop + buttonHeight + LabelGap + cardHeight) * scale <= wa.Bottom;
            fitsAbove = barTop.Y + (buttonTop - LabelGap - cardHeight) * scale >= wa.Top;
        }

        return _labelsPreferAbove ? fitsAbove || !fitsBelow : !fitsBelow && fitsAbove;
    }

    private void HideLabel(bool animate)
    {
        _labelHideTimer?.Stop();
        if (!_labelVisible) return;
        _labelVisible = false;
        _labelFlashing = false;
        _labelFlashTimer?.Stop();

        _labelCoolTimer ??= NewOneShotTimer(LabelWarmMs, () => _labelWarm = false);
        _labelCoolTimer.Stop();
        _labelCoolTimer.Start();

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            LabelCard.BeginAnimation(OpacityProperty, null);
            LabelPopup.IsOpen = false;
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(LabelOutMs));
        fade.Completed += (_, _) => { if (!_labelVisible) LabelPopup.IsOpen = false; };
        LabelCard.BeginAnimation(OpacityProperty, fade);
    }
}
