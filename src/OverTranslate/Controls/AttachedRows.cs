using System.Windows;
using System.Windows.Controls;

namespace OverTranslate.Controls;

/// <summary>
/// A setting and the settings that belong to it, as one group of rows: each row is its own card,
/// the rows are a 1px seam apart, and only the group's top and bottom corners are rounded.
/// </summary>
/// <remarks>
/// The children are the rows, each a <see cref="Border"/> styled as a card of its own. The panel
/// sets their corners, their border's top and bottom, and the seam between them; everything else -
/// fill, outline colour, padding - stays with the row's own style, so a group sits among the cards
/// of whatever section it is in. A row under the first is not indented: what belongs to the setting
/// above says so by being joined to it, not by moving right.
/// </remarks>
public class AttachedRows : StackPanel
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(AttachedRows),
        new FrameworkPropertyMetadata(8.0, (d, _) => ((AttachedRows)d).ShapeRows()));

    /// <summary>The radius of the group's outer corners, which should be the section's cards'.</summary>
    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public AttachedRows()
    {
        // A 1px seam drawn at 125% would otherwise land between device pixels and blur.
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
    }

    protected override void OnVisualChildrenChanged(DependencyObject visualAdded, DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        ShapeRows();
    }

    private void ShapeRows()
    {
        var rows = Children.OfType<Border>().ToList();
        var r = CornerRadius;
        for (var i = 0; i < rows.Count; i++)
        {
            bool first = i == 0, last = i == rows.Count - 1;
            rows[i].CornerRadius = new CornerRadius(first ? r : 0, first ? r : 0, last ? r : 0, last ? r : 0);
            rows[i].BorderThickness = new Thickness(1, first ? 1 : 0, 1, last ? 1 : 0);
            rows[i].Margin = new Thickness(0, first ? 0 : 1, 0, 0);
        }
    }
}
