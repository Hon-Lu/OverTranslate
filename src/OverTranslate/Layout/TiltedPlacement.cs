using System.Windows;
using System.Windows.Media;
using OverTranslate.Services.Ocr;
using Point = System.Windows.Point;

namespace OverTranslate.Layout;

/// <summary>
/// The WPF half of <see cref="TiltedText"/>, shared by both overlays: the transform that puts an
/// element laid out level back on the card, and the shapes it covers.
/// </summary>
/// <remarks>
/// Both overlays place elements on a canvas in device-independent pixels, each with its own way
/// from the capture's pixels to there; <c>toCanvas</c> is that way. The scale is taken to be the
/// same on both axes, which is what keeps an angle an angle.
/// </remarks>
internal static class TiltedPlacement
{
    /// <summary>
    /// The transform for an element whose top left is at <paramref name="elementLeft"/>,
    /// <paramref name="elementTop"/> on the canvas and which was laid out in the level frame.
    /// </summary>
    /// <remarks>
    /// A shear for a card seen in perspective, so the translation's letters stay upright as the
    /// source's are; a rotation for a card that was turned.
    /// </remarks>
    public static Transform For(
        TiltedText tilt, Func<Point, Point> toCanvas, double elementLeft, double elementTop)
    {
        var centre = toCanvas(tilt.Centre);
        double x = centre.X - elementLeft, y = centre.Y - elementTop;
        Transform transform = tilt.Sheared
            ? new SkewTransform(0, tilt.Degrees, x, y)
            : new RotateTransform(tilt.Degrees, x, y);
        transform.Freeze();
        return transform;
    }

    /// <summary>
    /// Polygons given in capture pixels, as one shape in the coordinates of an element whose top
    /// left is at <paramref name="elementLeft"/>, <paramref name="elementTop"/> on the canvas.
    /// </summary>
    public static Geometry Shape(
        IEnumerable<Point[]> polygons, Func<Point, Point> toCanvas, double elementLeft, double elementTop)
    {
        var shape = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var polygon in polygons)
        {
            if (polygon.Length < 3) continue;
            var points = polygon
                .Select(toCanvas)
                .Select(point => new Point(point.X - elementLeft, point.Y - elementTop))
                .ToList();
            var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
            foreach (var point in points.Skip(1))
                figure.Segments.Add(new LineSegment(point, false));
            shape.Children.Add(new PathGeometry([figure]));
        }
        shape.Freeze();
        return shape;
    }

    /// <summary>
    /// The level box <paramref name="level"/> (capture pixels), grown by
    /// <paramref name="dx"/> and <paramref name="dy"/> on each side, as it lies on the card.
    /// </summary>
    public static Point[] Outline(TiltedText tilt, Rect level, double dx, double dy)
    {
        var grown = level;
        grown.Inflate(dx, dy);
        return tilt.Corners(grown);
    }
}
