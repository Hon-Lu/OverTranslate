using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;

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

    public static readonly DependencyProperty SeamBrushProperty = DependencyProperty.Register(
        nameof(SeamBrush), typeof(Brush), typeof(AttachedRows),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// What the 1px between rows shows; AppRowSeam by default. The section's own fill would do where it
    /// differs from the rows', but in the light theme both are white and the rows would run together.
    /// </summary>
    public Brush? SeamBrush
    {
        get => (Brush?)GetValue(SeamBrushProperty);
        set => SetValue(SeamBrushProperty, value);
    }

    public AttachedRows()
    {
        // A 1px seam drawn at 125% would otherwise land between device pixels and blur.
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        SetResourceReference(SeamBrushProperty, "AppRowSeam");
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (SeamBrush is not { } brush) return;

        // Behind the rows, in the pixel each row past the first leaves above itself.
        foreach (var row in Children.OfType<Border>().Skip(1).Where(r => r.IsVisible))
        {
            var top = row.TranslatePoint(new Point(0, 0), this).Y;
            dc.DrawRectangle(brush, null, new Rect(0, top - row.Margin.Top, ActualWidth, row.Margin.Top));
        }
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
