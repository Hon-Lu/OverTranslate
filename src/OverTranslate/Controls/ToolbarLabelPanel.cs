using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// UseWindowsForms puts System.Windows.Forms and System.Drawing in the implicit usings, so these names collide
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace OverTranslate.Controls;

/// <summary>
/// Lays out a toolbar's label (<see cref="ToolbarLabel"/>): the card, and the arrow from it to the
/// button.
/// </summary>
/// <remarks>
/// <para>Two children, the card first and the arrow second. All the geometry is here, in one
/// measure and arrange pass, from the sizes the layout system has just measured: where the card goes
/// along the bar, how it keeps clear of the bar's ends and the screen's, where the arrow meets it,
/// and how much of the screen the popup has to cover for all of that to be drawn. Everything outside
/// says only which button (<see cref="Anchor"/>), which side (<see cref="Above"/>) and where the
/// screen ends (<see cref="ScreenLeft"/>, <see cref="ScreenRight"/>), and reads back where the popup
/// starts (<see cref="FrameLeft"/>). The first version worked these out in the window's code from a
/// size it measured itself, set the card and the arrow separately, and the three numbers drifted
/// apart — a card sized for the previous label, an arrow left behind where the last one was.</para>
///
/// <para>Wide enough for the card wherever along the bar it slides, and no wider than the screen.
/// The second half matters as much as the first: a popup that runs off the screen is pushed back
/// onto it whole, arrow and all, which leaves the arrow pointing beside its button. And the bar can
/// be narrower than the card — the realtime capsule collapsed to three buttons is — so the room has
/// to come from the card's width, not from a margin chosen for a wide bar. As tall as the card plus
/// <see cref="Edge"/> above and below, which is room for the arrow on one side and the shadow on
/// both.</para>
/// </remarks>
public sealed class ToolbarLabelPanel : Panel
{
    /// <summary>Room above and below the card, for the arrow and the shadow.</summary>
    public const double Edge = 10;

    // How far the arrow keeps from either end of the card: past the 9 of the rounded corner, by half
    // its own width and a little more, so it always leaves from a straight edge.
    private const double ArrowInset = 18;

    public static readonly DependencyProperty AnchorProperty = DependencyProperty.Register(
        nameof(Anchor), typeof(double), typeof(ToolbarLabelPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty AboveProperty = DependencyProperty.Register(
        nameof(Above), typeof(bool), typeof(ToolbarLabelPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty SpanProperty = DependencyProperty.Register(
        nameof(Span), typeof(double), typeof(ToolbarLabelPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ScreenLeftProperty = DependencyProperty.Register(
        nameof(ScreenLeft), typeof(double), typeof(ToolbarLabelPanel),
        new FrameworkPropertyMetadata(double.NegativeInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ScreenRightProperty = DependencyProperty.Register(
        nameof(ScreenRight), typeof(double), typeof(ToolbarLabelPanel),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The centre of the button being named, measured from the bar's left edge.</summary>
    /// <remarks>Animated to slide the label from one button to the next.</remarks>
    public double Anchor
    {
        get => (double)GetValue(AnchorProperty);
        set => SetValue(AnchorProperty, value);
    }

    /// <summary>Whether the card sits over the button rather than under it.</summary>
    public bool Above
    {
        get => (bool)GetValue(AboveProperty);
        set => SetValue(AboveProperty, value);
    }

    /// <summary>The width of the bar, which the card stays within where its arrow allows.</summary>
    public double Span
    {
        get => (double)GetValue(SpanProperty);
        set => SetValue(SpanProperty, value);
    }

    /// <summary>Where the screen's usable area starts, measured from the bar's left edge.</summary>
    /// <remarks>The popup goes no further; the card keeps <see cref="Edge"/> inside it.</remarks>
    public double ScreenLeft
    {
        get => (double)GetValue(ScreenLeftProperty);
        set => SetValue(ScreenLeftProperty, value);
    }

    /// <summary>Where the screen's usable area ends, measured from the bar's left edge.</summary>
    public double ScreenRight
    {
        get => (double)GetValue(ScreenRightProperty);
        set => SetValue(ScreenRightProperty, value);
    }

    /// <summary>
    /// Where the panel's own left edge is, measured from the bar's: the popup's horizontal offset.
    /// </summary>
    /// <remarks>Set by the measure pass, from the card's width and the screen.</remarks>
    public double FrameLeft { get; private set; }

    /// <summary>The card's height as last measured.</summary>
    public double CardHeight => InternalChildren.Count > 0 ? InternalChildren[0].DesiredSize.Height : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (UIElement child in InternalChildren)
            child.Measure(unbounded);

        // A card let out past either end of the bar to keep its arrow off a corner goes at most its
        // own width past it; the edge leaves room for the shadow beyond that.
        double reach = (InternalChildren.Count > 0 ? InternalChildren[0].DesiredSize.Width : 0) + Edge;
        double left = Math.Max(-reach, ScreenLeft);
        double right = Math.Min(Span + reach, ScreenRight);

        FrameLeft = left;
        return new Size(Math.Max(0, right - left), CardHeight + 2 * Edge);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count < 2) return finalSize;
        UIElement card = InternalChildren[0], arrow = InternalChildren[1];

        var size = card.DesiredSize;
        double anchor = Anchor;

        // Over its button, inside the bar's span where the bar is wide enough to hold it; centred on
        // a bar that is not. Then inside the screen with room for its shadow, and never at the cost
        // of the arrow leaving a corner.
        double left = size.Width <= Span
            ? Math.Clamp(anchor - size.Width / 2, 0, Span - size.Width)
            : (Span - size.Width) / 2;
        double screenMin = ScreenLeft + Edge, screenMax = ScreenRight - Edge - size.Width;
        left = Math.Clamp(left, screenMin, Math.Max(screenMin, screenMax));
        left = Math.Clamp(left, anchor - (size.Width - ArrowInset), anchor - ArrowInset);
        card.Arrange(new Rect(left - FrameLeft, Edge, size.Width, size.Height));

        // Over the card's edge by a pixel, so the arrow's fill opens the border where they meet.
        // Drawn pointing up; turned over for a card above the button.
        var tip = arrow.DesiredSize;
        double y = Above ? Edge + size.Height - 1 : Edge + 1 - tip.Height;
        arrow.RenderTransform = Above
            ? new ScaleTransform(1, -1, tip.Width / 2, tip.Height / 2)
            : Transform.Identity;
        arrow.Arrange(new Rect(anchor - tip.Width / 2 - FrameLeft, y, tip.Width, tip.Height));

        return finalSize;
    }
}
