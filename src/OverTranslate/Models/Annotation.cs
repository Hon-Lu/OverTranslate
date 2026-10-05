using System.Windows;
using System.Windows.Media;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace OverTranslate.Models;

/// <summary>What a drag inside the selection does while 標記 is on.</summary>
/// <remarks>
/// Stored by name in the settings file (the shape 形狀 last gave): the tools are in no order that
/// would keep an ordinal meaning the same one once another is added.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum AnnotationTool
{
    /// <summary>An opaque line at the chosen colour and width.</summary>
    Pen,

    /// <summary>A wide translucent band, so the content under it still reads.</summary>
    Highlighter,

    /// <summary>Rubs out whatever its circle passes over.</summary>
    Eraser,

    /// <summary>A straight line from where the drag started to where it ends.</summary>
    Line,

    /// <summary>The outline of the box the drag spans.</summary>
    Rectangle,

    /// <summary>The outline of the ellipse inscribed in the box the drag spans.</summary>
    Ellipse,
}

/// <summary>
/// One mark the user drew, in the overlay window's own DIP coordinates.
/// </summary>
/// <remarks>
/// <para>Deliberately not stored relative to the selection. The marks annotate what is on the frozen
/// screen, and the selection is a window onto them: moving or resizing the box changes which of them
/// show and which are clipped away, and never destroys one. A stroke kept relative to the box would
/// travel with it and end up sitting over content it was never drawn on.</para>
///
/// <para>The overlay window is pinned to the whole virtual desktop for the life of a session and
/// never moves, so its DIP space is a fixed frame of reference — the same one the bubbles are placed
/// in. Physical pixels would do as well and would have to be converted twice per point.</para>
///
/// <para>Immutable once committed, which is what lets the undo history be a list of snapshots: a
/// snapshot is a copy of the stroke list, and copying the list is enough because nothing can edit a
/// stroke afterwards. The eraser does not break that — it produces a new stroke rather than altering
/// one, which is also what makes a whole erase drag undo in a single press.</para>
/// </remarks>
public sealed class AnnotationStroke
{
    public required AnnotationTool Tool { get; init; }
    public required Color Color { get; init; }
    public required double Thickness { get; init; }
    public required IReadOnlyList<Point> Points { get; init; }

    /// <summary>
    /// How much of what is underneath still shows through. 1 for anything but a highlight.
    /// </summary>
    /// <remarks>
    /// Stored rather than worked out from the tool, because it is the user's to set: a highlighter
    /// laid over pale text and one laid over a screenshot of a dark game want different answers, and
    /// there is no value that is right for both.
    ///
    /// Applied to the rendered shape as a whole rather than to its brush. A translucent brush darkens
    /// wherever a stroke crosses itself — which for a hand-drawn highlight is everywhere it changes
    /// direction — and the result is a band with dark knots in it. Opacity on the element makes WPF
    /// compose the stroke once and then fade the result, so the band is even.
    /// </remarks>
    public required double Opacity { get; init; }

    /// <summary>Whether this is one of the drag-out shapes rather than a freehand line.</summary>
    /// <remarks>
    /// A shape keeps only the two ends of the drag in <see cref="Points"/> and is drawn from them,
    /// rather than being stored as the outline it draws. An ellipse kept as points would be a polygon
    /// at whatever resolution was picked when it was drawn, and the box it is measured from is all
    /// the bounds and the undo history ever need.
    /// </remarks>
    public bool IsShape => IsShapeTool(Tool);

    public static bool IsShapeTool(AnnotationTool tool) =>
        tool is AnnotationTool.Line or AnnotationTool.Rectangle or AnnotationTool.Ellipse;

    /// <summary>The outline a shape tool draws for a drag from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public static Geometry ShapeGeometry(AnnotationTool tool, Point start, Point end)
    {
        Geometry geometry = tool switch
        {
            AnnotationTool.Rectangle => new RectangleGeometry(new Rect(start, end)),
            AnnotationTool.Ellipse   => new EllipseGeometry(new Rect(start, end)),
            _                        => new LineGeometry(start, end),
        };
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Where a shape's drag ends once Shift is held: lines snap to every 45°, boxes and ellipses
    /// become squares and circles.
    /// </summary>
    /// <remarks>
    /// The square takes the longer of the two sides rather than the shorter, so the corner stays on
    /// the side of the pointer it was dragged to and the shape never shrinks away from the hand.
    /// </remarks>
    public static Point ConstrainShape(AnnotationTool tool, Point start, Point end)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;

        if (tool == AnnotationTool.Line)
        {
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) return end;

            double step  = Math.PI / 4;
            double angle = Math.Round(Math.Atan2(dy, dx) / step) * step;
            return new Point(start.X + length * Math.Cos(angle), start.Y + length * Math.Sin(angle));
        }

        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point(
            start.X + (dx < 0 ? -side : side),
            start.Y + (dy < 0 ? -side : side));
    }

    /// <summary>The box the stroke paints inside, with the width of the nib allowed for.</summary>
    /// <remarks>
    /// Worked out from the points rather than by widening them. The widened outline answers this
    /// exactly and costs about five segments for every point recorded, and nothing here needs the
    /// exact answer: it places the mask and rejects strokes the eraser is nowhere near, and both
    /// only need a box that is certainly big enough.
    /// </remarks>
    public Rect Bounds => _bounds ??= MeasureBounds();

    private Rect? _bounds;

    private Rect MeasureBounds()
    {
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            // Half a nib each way for the width, and one more DIP so the round caps and the mask's
            // own soft rim have somewhere to land.
            double pad = Thickness / 2 + 1;
            return new Rect(minX - pad, minY - pad, maxX - minX + pad * 2, maxY - minY + pad * 2);
        }
    }

    /// <summary>
    /// The same path with the specks at either end that only say which way the hand shook left out.
    /// </summary>
    /// <remarks>
    /// <para>For the two ends, and for the flat nib in particular. A flat end is drawn square to the
    /// last bit of travel, and the last bit of travel at the moment a button goes down or comes up
    /// is a pixel or two of the hand settling — so on a quick stroke the end can be squared off
    /// against a direction the stroke never went in, and comes out sliced at a steep angle with a
    /// tab of ink sticking off it. It shows up now and then rather than always because it depends on
    /// which way the hand happened to move.</para>
    ///
    /// <para>The ends themselves are kept, so the stroke still covers exactly what it covered; what
    /// goes is the handful of points between an end and somewhere far enough away to mean a
    /// direction. Half the nib's width is that distance — nearer than that and the point is inside
    /// the ink the end cap draws anyway.</para>
    /// </remarks>
    public static IReadOnlyList<Point> WithoutEndJitter(IReadOnlyList<Point> points, double thickness)
    {
        if (points.Count < 3) return points;

        double reach = thickness / 2;
        double reachSquared = reach * reach;

        int first = 0;
        while (first + 2 < points.Count && DistanceSquared(points[0], points[first + 1]) < reachSquared)
            first++;

        int last = points.Count - 1;
        while (last - 2 > first && DistanceSquared(points[^1], points[last - 1]) < reachSquared)
            last--;

        if (first == 0 && last == points.Count - 1) return points;

        // The two ends, and everything between them that was far enough from either to be travel.
        var kept = new List<Point>(points.Count) { points[0] };
        for (int i = first + 1; i < last; i++) kept.Add(points[i]);
        kept.Add(points[^1]);

        return kept;
    }

    /// <summary>
    /// Whether the stroke passes within <paramref name="radius"/> of <paramref name="point"/>.
    /// </summary>
    /// <remarks>
    /// A cheap first question, asked before any geometry is built: most strokes are nowhere near the
    /// eraser, and widening and subtracting for all of them on every step of a drag would be work
    /// thrown away. It only has to be right in one direction — whatever it says yes to is then given
    /// the exact treatment.
    ///
    /// Measured against the segments, not just the recorded points: those are sampled from pointer
    /// moves, so a fast stroke is a handful of points with long gaps between them, and testing the
    /// points alone would miss most of a quick line.
    /// </remarks>
    public bool IsWithin(Point point, double radius)
    {
        double reach = radius + Thickness / 2;
        double reachSq = reach * reach;

        // A shape's two points are the corners of its box, not a path along it.
        if (IsShape && Points.Count == 2)
            return ShapeGeometry(Tool, Points[0], Points[1])
                .StrokeContains(new Pen(Brushes.Black, reach * 2), point);

        if (Points.Count == 1)
            return DistanceSquared(Points[0], point) <= reachSq;

        for (int i = 1; i < Points.Count; i++)
            if (DistanceToSegmentSquared(point, Points[i - 1], Points[i]) <= reachSq)
                return true;

        return false;
    }



    private static double DistanceSquared(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double DistanceToSegmentSquared(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;

        // A segment whose ends coincide — two pointer events at the same spot — has no direction to
        // project onto, and the projection below would divide by zero.
        if (lenSq <= double.Epsilon) return DistanceSquared(p, a);

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
        return DistanceSquared(p, new Point(a.X + t * dx, a.Y + t * dy));
    }
}
