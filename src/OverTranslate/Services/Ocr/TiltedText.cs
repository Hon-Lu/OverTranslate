using System.Windows;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using Vector = System.Windows.Vector;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// One line of a tilted card as <see cref="TiltedLayout.Straighten"/> found it, carried through
/// grouping so the group can be drawn the way the card runs.
/// </summary>
/// <param name="Cluster">Which card the line belongs to; a group drawn tilted is one card.</param>
/// <param name="Sheared">Whether the card was levelled by shearing rather than by rotation.</param>
/// <param name="Degrees">The line's own angle, averaged over its neighbours.</param>
/// <param name="Weight">How much the line's angle counts toward its group's: its length when the
/// angle is to be trusted, nothing for a short word or a username.</param>
/// <param name="Centre">The centre of the detector's box.</param>
/// <param name="Level">The size of the line's level box, its thickness capped as grouping saw it.</param>
/// <param name="Quad">The detector's quadrilateral, corner after corner round its edge.</param>
internal sealed record TiltedLine(
    int Cluster, bool Sheared, double Degrees, double Weight, Point Centre, Size Level, Point[] Quad);

/// <summary>
/// Where a group read off a tilted card is drawn: a level box, and the turn or shear that puts it
/// back on the card. All in the same pixels as the group's <see cref="OcrTextBlock.Bounds"/>.
/// </summary>
/// <remarks>
/// <para>A tilted card's lines are drawn over by an upright rectangle everywhere else, and on a
/// card that rectangle is either too big or too small: on the 30° card of region-comic-en-3 the
/// upright boxes of neighbouring comments overlap one another and reach off the card onto the
/// page, while on the 9.4° one the band, sized from the letters, leaves both ends of every line
/// showing. So a tilted group is set level inside <see cref="Box"/>, by the ordinary rules, and
/// turned with the card: rotated where the whole card was turned, sheared where it is seen in
/// perspective with its letters upright — which keeps the translation's letters upright too. What
/// covers the source is the same box, turned the same way; what is erased is the inside of the
/// lines' own quadrilaterals.</para>
///
/// <para>The level frame is centred on <see cref="Centre"/>, so a point and its level counterpart
/// coincide there.</para>
/// </remarks>
internal sealed record TiltedText(
    double Degrees, bool Sheared, Point Centre, Rect Box, IReadOnlyList<Point[]> LineQuads)
{
    /// <summary>
    /// How far past a line's quadrilateral the erase may reach, as a share of the line's thickness:
    /// outward across it on each side, and along it at each end. The quadrilateral is the detector's
    /// fit, and an outline, a descender or a last letter's tail can sit just outside it.
    /// </summary>
    private const double ErasePadding = 0.18;

    /// <summary>
    /// How far inside the box's left edge the erase stops, as a share of the line's thickness —
    /// about half the room the detector leaves before the first letter.
    /// </summary>
    private const double MarginInset = 0.12;

    /// <summary>
    /// Whether these are vertical columns rather than lines across: <see cref="Box"/> is then a
    /// column group's level box, and <see cref="Degrees"/> its turn from vertical — see
    /// <see cref="FromColumns"/>.
    /// </summary>
    public bool Column { get; init; }

    /// <summary>The box's four corners on the card, round its edge from its top left.</summary>
    public Point[] Outline => Corners(Box);

    /// <summary>The image point a level point lands on.</summary>
    public Point ToImage(Point level)
    {
        double dx = level.X - Centre.X, dy = level.Y - Centre.Y;
        if (Sheared)
            return new Point(level.X, level.Y + dx * Math.Tan(Radians));

        double cos = Math.Cos(Radians), sin = Math.Sin(Radians);
        return new Point(Centre.X + dx * cos - dy * sin, Centre.Y + dx * sin + dy * cos);
    }

    /// <summary>The level point an image point came from.</summary>
    public Point ToLevel(Point image)
    {
        double dx = image.X - Centre.X, dy = image.Y - Centre.Y;
        if (Sheared)
            return new Point(image.X, image.Y - dx * Math.Tan(Radians));

        double cos = Math.Cos(Radians), sin = Math.Sin(Radians);
        return new Point(Centre.X + dx * cos + dy * sin, Centre.Y - dx * sin + dy * cos);
    }

    /// <summary>A level rectangle's corners on the card, round its edge from its top left.</summary>
    public Point[] Corners(Rect level) =>
    [
        ToImage(level.TopLeft), ToImage(level.TopRight),
        ToImage(level.BottomRight), ToImage(level.BottomLeft),
    ];

    /// <summary>
    /// The lines' quadrilaterals, each grown by <see cref="ErasePadding"/> and cut off a padding's
    /// width before the card's margin — the left edge of <see cref="Box"/>.
    /// </summary>
    /// <remarks>
    /// <para>Cut for the reason the box's edge is a median: a quadrilateral that overshoots the text
    /// reaches off the card, and on the 30° card the repair then erased the card's own black border
    /// against the white page and painted the page into the card.</para>
    ///
    /// <para>Columns are not cut. A column group's box is everything its columns cover, not a
    /// median margin — an indented column starts lower than the rest — so there is no edge inside
    /// the text to cut at, and cutting at the box's own top would remove nothing.</para>
    /// </remarks>
    public IReadOnlyList<Point[]> EraseQuads =>
        [.. LineQuads.Select(quad => Column ? Padded(quad) : FromMargin(Padded(quad), quad))];

    /// <summary>The upright box round some points.</summary>
    public static Rect Enclosing(IEnumerable<Point> points)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var point in points)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }
        return left > right ? Rect.Empty : new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// An upright rectangle inside the box, for the colour samplers, which read a rectangle and the
    /// ring around it.
    /// </summary>
    /// <remarks>
    /// The upright box round a tilted group takes in the page beside the card and the comments
    /// above and below, and its ring lies wholly off the card. This one is half the box's height
    /// and as wide as still keeps all four of its corners on the box.
    /// </remarks>
    public Rect SampleArea
    {
        get
        {
            var middle = ToImage(new Point(Box.X + Box.Width / 2, Box.Y + Box.Height / 2));
            double height = Box.Height / 2;
            var level = Box;
            level.Inflate(0.5, 0.5);
            for (double width = Box.Width; width > 1; width *= 0.9)
            {
                var area = new Rect(middle.X - width / 2, middle.Y - height / 2, width, height);
                if (new[] { area.TopLeft, area.TopRight, area.BottomLeft, area.BottomRight }
                    .All(corner => level.Contains(ToLevel(corner))))
                    return area;
            }
            return new Rect(middle.X - 0.5, middle.Y - height / 2, 1, height);
        }
    }

    /// <summary>The same text moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public TiltedText Offset(double dx, double dy)
    {
        var box = Box;
        box.Offset(dx, dy);
        var shift = new Vector(dx, dy);
        return this with
        {
            Centre = Centre + shift,
            Box = box,
            LineQuads = [.. LineQuads.Select(quad => quad.Select(point => point + shift).ToArray())],
        };
    }

    /// <summary>
    /// The group the lines make, or null when they are not one card: lines from two clusters, or a
    /// group with nothing in it.
    /// </summary>
    /// <remarks>
    /// <para>One angle for the group — its lines' own, weighted by length — about the middle of
    /// them. A card in perspective drifts by several degrees from top to bottom, and the group is
    /// drawn the way its own part of the card runs.</para>
    ///
    /// <para>The box's left edge is the median of the lines' left edges, not the leftmost of them.
    /// The detector's quadrilateral can overshoot the text — on the 30° card the first comment's
    /// first line reached past the card's left edge onto the page — and a box taken from the
    /// leftmost edge carried the translation off the card with it. A card's lines start at one
    /// margin, and the median is that margin. The right edge is the furthest line's: a comment's
    /// lines end raggedly, and the longest is the room the comment has.</para>
    ///
    /// <para>Each line is as thick as grouping saw it, capped by the card's own line pitch. Upright
    /// letters on a sloped line fatten its quadrilateral by each letter's width times the sine of
    /// the slope; uncapped, the box of a three-line comment reached well into the comments either
    /// side of it.</para>
    /// </remarks>
    public static TiltedText? From(IReadOnlyList<TiltedLine> lines)
    {
        if (lines.Count == 0) return null;
        var first = lines[0];
        if (lines.Any(line => line.Cluster != first.Cluster || line.Sheared != first.Sheared))
            return null;

        double weight = lines.Sum(line => line.Weight);
        double degrees = weight > 0
            ? lines.Sum(line => line.Degrees * line.Weight) / weight
            : lines.Average(line => line.Degrees);
        var centre = new Point(lines.Average(line => line.Centre.X), lines.Average(line => line.Centre.Y));
        var frame = new TiltedText(degrees, first.Sheared, centre, Rect.Empty, [.. lines.Select(line => line.Quad)]);

        var boxes = lines
            .Select(line =>
            {
                var middle = frame.ToLevel(line.Centre);
                return new Rect(middle.X - line.Level.Width / 2, middle.Y - line.Level.Height / 2,
                    line.Level.Width, line.Level.Height);
            })
            .ToList();

        // The upper of the two middle values: a two-line comment whose first line overshoots has
        // only the other to say where the margin is.
        var lefts = boxes.Select(box => box.Left).OrderBy(left => left).ToList();
        double margin = lefts[lefts.Count / 2];
        double right = boxes.Max(box => box.Right);
        double top = boxes.Min(box => box.Top);
        double bottom = boxes.Max(box => box.Bottom);
        if (right <= margin) return null;

        return frame with { Box = new Rect(margin, top, right - margin, bottom - top) };
    }

    /// <summary>
    /// A group of tilted columns, turned <paramref name="degrees"/> from vertical (see
    /// <see cref="TiltedColumns.Deviation"/>): the level box their quadrilaterals make, about the
    /// middle of them.
    /// </summary>
    /// <remarks>
    /// <para>Always a rotation. A tilted narration box or strip of paper is turned whole, its
    /// letters with it; a hand-lettered column that leans by a few degrees reads the same either
    /// way.</para>
    ///
    /// <para>The box is everything the columns cover, taken from each quadrilateral's corners in the
    /// level frame rather than from its measurements, so a short column whose fit came out the other
    /// way round still adds the room it really takes.</para>
    /// </remarks>
    /// <param name="quads">Each column's quadrilateral, first side along the column — see
    /// <see cref="TiltedColumns.Quad"/>.</param>
    public static TiltedText? FromColumns(double degrees, IReadOnlyList<Point[]> quads)
    {
        if (quads.Count == 0 || quads.Any(quad => quad.Length != 4)) return null;
        var centre = new Point(
            quads.Average(quad => quad.Average(point => point.X)),
            quads.Average(quad => quad.Average(point => point.Y)));
        var frame = new TiltedText(degrees, false, centre, Rect.Empty, quads) { Column = true };
        var box = Enclosing(quads.SelectMany(quad => quad).Select(frame.ToLevel));
        return box.IsEmpty || box.Width <= 0 || box.Height <= 0 ? null : frame with { Box = box };
    }

    /// <summary>Whether a point lies in a convex polygon, whichever way round its corners go.</summary>
    public static bool Inside(Point[] polygon, double x, double y) => Depth(polygon, x, y) >= 0;

    /// <summary>
    /// How far inside a convex polygon a point lies: positive inside, negative outside. Outside, it
    /// is the distance to the nearest edge's line, which past a corner is a little short of the
    /// distance to the corner itself.
    /// </summary>
    public static double Depth(Point[] polygon, double x, double y)
    {
        double area = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            area += a.X * b.Y - b.X * a.Y;
        }
        if (area == 0) return double.NegativeInfinity;
        double turn = Math.Sign(area);

        double depth = double.MaxValue;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Length];
            double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (length == 0) continue;
            double cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            depth = Math.Min(depth, turn * cross / length);
        }
        return depth;
    }

    private double Radians => Degrees * Math.PI / 180;

    /// <summary>
    /// The part of <paramref name="polygon"/> that lies right of the margin, less the padding the
    /// erase is allowed past <paramref name="quad"/>; the polygon itself when the box is not set.
    /// </summary>
    private Point[] FromMargin(Point[] polygon, Point[] quad)
    {
        // Inside the margin, not a padding short of it. The box's edge is the detector's, which
        // pads past the letters — on the 30° card the box starts at 66 and the letters at 72 — and
        // anything up to it reached the card's black border, which the repair then filled from the
        // white page behind it.
        if (Box.IsEmpty || quad.Length != 4) return polygon;
        double edge = Box.Left + (quad[3] - quad[0]).Length * MarginInset;

        // One side of Sutherland–Hodgman, in the level frame, where the margin is a vertical line.
        var level = polygon.Select(ToLevel).ToList();
        var kept = new List<Point>();
        for (int i = 0; i < level.Count; i++)
        {
            var from = level[i];
            var to = level[(i + 1) % level.Count];
            bool fromIn = from.X >= edge, toIn = to.X >= edge;
            if (fromIn) kept.Add(from);
            if (fromIn != toIn)
            {
                double t = (edge - from.X) / (to.X - from.X);
                kept.Add(new Point(edge, from.Y + (to.Y - from.Y) * t));
            }
        }
        return kept.Count >= 3 ? [.. kept.Select(ToImage)] : polygon;
    }

    private static Point[] Padded(Point[] quad)
    {
        if (quad.Length != 4) return quad;
        var middle = new Point(quad.Average(point => point.X), quad.Average(point => point.Y));
        var along = (quad[1] - quad[0]) / 2;
        var across = (quad[3] - quad[0]) / 2;
        double thickness = across.Length * 2;
        if (along.Length <= 0 || thickness <= 0) return quad;

        var reach = along * ((along.Length + thickness * ErasePadding) / along.Length);
        var depth = across * (1 + ErasePadding);
        return [middle - reach - depth, middle + reach - depth, middle + reach + depth, middle - reach + depth];
    }
}
