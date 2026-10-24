using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// UseWindowsForms puts System.Windows.Forms and System.Drawing in the implicit usings, so these names collide
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace OverTranslate.Views.Capture;

/// <summary>
/// Lays out the capture toolbar's label: the card, and the arrow from it to the button.
/// </summary>
/// <remarks>
/// <para>Two children, the card first and the arrow second. All the geometry is here, in one
/// arrange pass, from the sizes the layout system has just measured: where the card goes along the
/// bar, how it keeps clear of the bar's ends, and where the arrow meets it. Everything outside says
/// only which button (<see cref="Anchor"/>) and which side (<see cref="Above"/>). The first version
/// worked these out in the window's code from a size it measured itself, set the card and the arrow
/// separately, and the three numbers drifted apart — a card sized for the previous label, an arrow
/// left behind where the last one was.</para>
///
/// <para>As wide as the bar plus <see cref="SideRoom"/> either side, so a card let out past the end
/// of the bar to keep its arrow off a corner still has room to be drawn. As tall as the card plus
/// <see cref="Edge"/> above and below, which is room for the arrow on one side and the shadow on
/// both.</para>
/// </remarks>
public sealed class ToolbarLabelPanel : Panel
{
    /// <summary>Room either side of the bar for a card let out past its end.</summary>
    public const double SideRoom = 40;

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

    /// <summary>The card's height as last measured.</summary>
    public double CardHeight => InternalChildren.Count > 0 ? InternalChildren[0].DesiredSize.Height : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (UIElement child in InternalChildren)
            child.Measure(unbounded);

        return new Size(Span + 2 * SideRoom, CardHeight + 2 * Edge);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count < 2) return finalSize;
        UIElement card = InternalChildren[0], arrow = InternalChildren[1];

        var size = card.DesiredSize;
        double anchor = Anchor;

        // Inside the bar's span where it can be; never at the cost of the arrow leaving a corner.
        double left = Math.Clamp(anchor - size.Width / 2, 0, Math.Max(0, Span - size.Width));
        left = Math.Clamp(left, anchor - (size.Width - ArrowInset), anchor - ArrowInset);
        card.Arrange(new Rect(SideRoom + left, Edge, size.Width, size.Height));

        // Over the card's edge by a pixel, so the arrow's fill opens the border where they meet.
        // Drawn pointing up; turned over for a card above the button.
        var tip = arrow.DesiredSize;
        double y = Above ? Edge + size.Height - 1 : Edge + 1 - tip.Height;
        arrow.RenderTransform = Above
            ? new ScaleTransform(1, -1, tip.Width / 2, tip.Height / 2)
            : Transform.Identity;
        arrow.Arrange(new Rect(SideRoom + anchor - tip.Width / 2, y, tip.Width, tip.Height));

        return finalSize;
    }
}
