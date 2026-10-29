using System.Windows;
using System.Windows.Media;

namespace OverTranslate.Views.Capture;

/// <summary>
/// The 橫排／直排 switch: the chosen half shows only its mark, the other half its name.
/// </summary>
/// <remarks>
/// <para>Everything moves on one number, the progress from horizontal (0) to vertical (1). The
/// switch is always two marks plus the longer of the two names wide, and that width is shared out
/// between the halves: the other half gets its mark and its own name, the chosen half the rest. The
/// pill covers the chosen half, so the name is seen to be pushed across by it.</para>
///
/// <para>A fixed total, because the bar sizes to its content and is centred on the selection: a
/// switch that grew or shrank by the difference between two words would move the whole bar under
/// the pointer each time it was pressed — in Russian by thirteen pixels.</para>
///
/// <para>A spring rather than a timed animation, critically damped so nothing overshoots. A timed
/// animation restarted halfway across begins again from rest; the spring keeps the speed it had,
/// so changing one's mind mid-way reads as one movement turning round rather than a stop and a
/// fresh start.</para>
/// </remarks>
public partial class ToolbarWindow
{
    /// <summary>The narrowest a half gets: its mark alone. Public because the markup reads it.</summary>
    public const double DirectionMarkWidth = 32;

    // How long the spring takes to get most of the way, in seconds. At damping 1 it does not
    // overshoot, so this is close to the time it reads as taking.
    private const double DirectionResponse = 0.3;

    private double _horizontalNameWidth;
    private double _verticalNameWidth;
    private double _directionProgress;
    private double _directionVelocity;
    private TimeSpan _directionLastFrame;
    private bool _directionAnimating;

    /// <summary>
    /// Restores the remembered direction and measures the names.
    /// </summary>
    /// <remarks>
    /// The names are measured once: the toolbar lives for one capture session and the interface
    /// language cannot change underneath it — the same reasoning as SizeSelectorsToClosedLabels.
    /// </remarks>
    private void InitializeDirectionSwitch(bool vertical)
    {
        var infinite = new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity);
        HorizontalName.Measure(infinite);
        VerticalName.Measure(infinite);
        _horizontalNameWidth = Math.Ceiling(HorizontalName.DesiredSize.Width);
        _verticalNameWidth = Math.Ceiling(VerticalName.DesiredSize.Width);

        // CompositionTarget.Rendering is static: a spring still running when the bar closes would
        // keep the window alive through it.
        Closed += (_, _) => StopDirectionSpring();

        (vertical ? VerticalSeg : HorizontalSeg).IsChecked = true;
        _directionProgress = vertical ? 1 : 0;
        LayOutDirectionSwitch();
    }

    /// <summary>
    /// Commits the choice on the press rather than on the release, so the pill starts moving under
    /// the pointer instead of after it. See LayoutModeSegment_PreviewMouseLeftButtonDown.
    /// </summary>
    private void DirectionSegment_PreviewMouseLeftButtonDown(
        object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton segment) segment.IsChecked = true;
    }

    /// <summary>
    /// The direction just picked: kept for next time, the manga hint brought in line with it, and
    /// the switch set moving.
    /// </summary>
    private void DirectionSegment_Checked(object sender, RoutedEventArgs e)
    {
        // Fires once while the markup is still being parsed, for the half that opens checked, before
        // the other half and the name slots exist.
        if (VerticalSeg is null || VerticalNameSlot is null || DirectionThumb is null) return;

        if (_initializingDirection) return;

        SaveTextDirectionSelection();
        RenderMangaHint();
        StartDirectionSpring();
    }

    private void StartDirectionSpring()
    {
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            StopDirectionSpring();
            _directionProgress = IsVerticalText ? 1 : 0;
            _directionVelocity = 0;
            LayOutDirectionSwitch();
            return;
        }

        if (_directionAnimating) return;
        _directionAnimating = true;
        _directionLastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += DirectionSpring_Rendering;
    }

    private void StopDirectionSpring()
    {
        if (!_directionAnimating) return;
        _directionAnimating = false;
        CompositionTarget.Rendering -= DirectionSpring_Rendering;
    }

    private void DirectionSpring_Rendering(object? sender, EventArgs e)
    {
        // Rendering can fire more than once per frame; RenderingTime only moves once per frame.
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _directionLastFrame) return;
        double dt = _directionLastFrame == TimeSpan.Zero
            ? 1.0 / 60
            : Math.Min((now - _directionLastFrame).TotalSeconds, 1.0 / 30);
        _directionLastFrame = now;

        double target = IsVerticalText ? 1 : 0;
        double omega = 2 * Math.PI / DirectionResponse;
        double acceleration = -omega * omega * (_directionProgress - target) - 2 * omega * _directionVelocity;
        _directionVelocity += acceleration * dt;
        _directionProgress += _directionVelocity * dt;

        if (Math.Abs(_directionProgress - target) < 0.002 && Math.Abs(_directionVelocity) < 0.02)
        {
            _directionProgress = target;
            _directionVelocity = 0;
            StopDirectionSpring();
        }
        LayOutDirectionSwitch();
    }

    private void LayOutDirectionSwitch()
    {
        var layout = DirectionSwitchLayout.At(_directionProgress, _horizontalNameWidth, _verticalNameWidth);
        HorizontalMark.Width = layout.HorizontalMarkWidth;
        VerticalMark.Width = layout.VerticalMarkWidth;
        HorizontalNameSlot.Width = layout.HorizontalNameWidth;
        VerticalNameSlot.Width = layout.VerticalNameWidth;
        HorizontalName.Opacity = layout.HorizontalNameOpacity;
        VerticalName.Opacity = layout.VerticalNameOpacity;
        DirectionThumb.Width = layout.ThumbWidth;
        DirectionThumb.Margin = new Thickness(layout.ThumbLeft, 0, 0, 0);
    }
}

/// <summary>Where everything in the direction switch sits at one point of its travel.</summary>
/// <param name="HorizontalMarkWidth">The cell 橫排's mark is centred in.</param>
/// <param name="HorizontalNameWidth">How much of 橫排's name is showing.</param>
/// <param name="VerticalMarkWidth">The cell 直排's mark is centred in.</param>
/// <param name="VerticalNameWidth">How much of 直排's name is showing.</param>
/// <param name="ThumbLeft">The pill's left edge, from the inside of the tray.</param>
/// <param name="ThumbWidth">The pill's width: the chosen half's mark cell.</param>
internal readonly record struct DirectionSwitchLayout(
    double HorizontalMarkWidth,
    double HorizontalNameWidth,
    double VerticalMarkWidth,
    double VerticalNameWidth,
    double HorizontalNameOpacity,
    double VerticalNameOpacity,
    double ThumbLeft,
    double ThumbWidth)
{
    /// <param name="progress">0 with horizontal chosen, 1 with vertical chosen.</param>
    /// <param name="horizontalName">橫排's name, with its margin.</param>
    /// <param name="verticalName">直排's name, with its margin.</param>
    public static DirectionSwitchLayout At(double progress, double horizontalName, double verticalName)
    {
        const double mark = ToolbarWindow.DirectionMarkWidth;
        double p = Math.Clamp(progress, 0, 1);
        double room = Math.Max(horizontalName, verticalName);

        // The half that is not chosen is its mark and exactly its own name, so the name sits against
        // its mark however much shorter it is than the other. What the shorter name leaves over goes
        // to the chosen half, whose pill widens to take it — in English the pill on 橫排 is wider
        // than the one on 直排, rather than "Vertical" sitting in a slot cut for "Horizontal" with a
        // hole on its right. The two always add up to one room, so the switch never changes width.
        double horizontalShown = p * horizontalName;
        double verticalShown = (1 - p) * verticalName;
        double horizontalMark = mark + (1 - p) * (room - verticalName);
        double verticalMark = mark + p * (room - horizontalName);

        // The names fade over the last three quarters of their travel, so a name is never seen
        // squeezed to a couple of letters at full strength.
        double horizontalOpacity = Math.Max(0, (p - 0.25) / 0.75);
        double verticalOpacity = Math.Max(0, (1 - p - 0.25) / 0.75);

        // The pill covers one mark cell and travels between the two: on the horizontal one at 0, on
        // the vertical one, past the horizontal mark and its name, at 1.
        double thumbLeft = p * (mark + horizontalName);
        double thumbWidth = (1 - p) * (mark + room - verticalName) + p * (mark + room - horizontalName);

        return new(horizontalMark, horizontalShown, verticalMark, verticalShown,
            horizontalOpacity, verticalOpacity, thumbLeft, thumbWidth);
    }
}
