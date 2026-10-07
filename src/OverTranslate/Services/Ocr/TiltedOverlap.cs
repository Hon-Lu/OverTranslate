using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Draws level, as every group was before tilted text was drawn along its slope, any tilted group
/// whose slope runs it into another group's text — see <see cref="TiltedText"/>.
/// </summary>
/// <remarks>
/// <para>A tilted group is drawn in its own box turned to its own angle, and a level one in its
/// upright box. Where tilted and level groups sit close together — a dense label photographed a
/// few degrees off, where one paragraph was found tilted and the lines above and below it were
/// not — the two disagree about where the text is. Measured on photo-ko-tilted's left tin, a
/// three-line paragraph found at 3.1°: its turned box covered 0.28 of the line above it, where its
/// upright box covered 0.16, and the translations were drawn over each other (#273).</para>
///
/// <para>So a group goes back to level only where that is what made it collide: where its turned
/// box overlaps some other group by clearly more than its upright box would. Never the other way
/// round — a level group is never turned — and never on overlap alone, since text really can sit
/// on text. On a card whose groups are all tilted alike, the turned boxes do not meet at all while
/// the upright ones overlap by up to all of a neighbour (region-comic-en-3's 30° card), and the one
/// tilted line on the 9.4° card that does touch a neighbour touches it less turned than upright;
/// both stay as they are.</para>
///
/// <para>Measured against every other group, not within one: the groups that collide are by
/// definition different ones. Run again after each change, since a group put back level takes its
/// larger upright box with it.</para>
/// </remarks>
internal static class TiltedOverlap
{
    /// <summary>
    /// How much more a turned box may overlap a neighbour than the upright box would, as a share of
    /// the smaller of the two. Above the slivers where a turned box's corner clips a neighbour's —
    /// 0.013 for the same tilted paragraph against the line below it — and below the paragraph
    /// that ran into the line above (0.12) or a tilted cell running into the row beside it (0.05).
    /// </summary>
    internal const double Tolerance = 0.03;

    /// <summary>
    /// <paramref name="groups"/> with the tilt taken off every group it makes run into another;
    /// <paramref name="groups"/> itself when there is none.
    /// </summary>
    internal static List<OcrTextBlock> Level(List<OcrTextBlock> groups)
    {
        if (!groups.Any(group => group.Tilt is not null))
            return groups;

        var regions = groups.Select(Region).ToArray();
        List<OcrTextBlock>? result = null;
        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < groups.Count; i++)
            {
                var current = result?[i] ?? groups[i];
                if (current.Tilt is null || !Collides(regions, i, Upright(current.Bounds)))
                    continue;

                result ??= groups.ToList();
                result[i] = current with { Tilt = null };
                regions[i] = Upright(current.Bounds);
                changed = true;
            }
        }
        while (changed);

        return result ?? groups;
    }

    /// <summary>
    /// Whether the turned box at <paramref name="index"/> overlaps some other region by more than
    /// <paramref name="upright"/> would, past <see cref="Tolerance"/>.
    /// </summary>
    private static bool Collides(Point[][] regions, int index, Point[] upright)
    {
        var turned = regions[index];
        double turnedArea = Area(turned);
        for (var j = 0; j < regions.Length; j++)
        {
            if (j == index) continue;
            var other = regions[j];
            double smaller = Math.Min(turnedArea, Area(other));
            if (smaller <= 0) continue;

            double excess = Area(Intersection(turned, other)) - Area(Intersection(upright, other));
            if (excess > smaller * Tolerance)
                return true;
        }
        return false;
    }

    /// <summary>Where a group is drawn: its turned box when it is tilted, its upright box when not.</summary>
    private static Point[] Region(OcrTextBlock group) =>
        group.Tilt is { } tilt ? tilt.Outline : Upright(group.Bounds);

    private static Point[] Upright(Rect box) =>
        [box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft];

    /// <summary>The area of a polygon, whichever way round its corners go.</summary>
    internal static double Area(IReadOnlyList<Point> polygon)
    {
        double twice = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            twice += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(twice) / 2;
    }

    /// <summary>
    /// The part of <paramref name="subject"/> inside the convex polygon <paramref name="clip"/>, by
    /// Sutherland–Hodgman; empty when they do not meet.
    /// </summary>
    internal static List<Point> Intersection(IReadOnlyList<Point> subject, IReadOnlyList<Point> clip)
    {
        double turn = 0;
        for (var i = 0; i < clip.Count; i++)
        {
            var a = clip[i];
            var b = clip[(i + 1) % clip.Count];
            turn += a.X * b.Y - b.X * a.Y;
        }
        if (turn == 0) return [];
        turn = Math.Sign(turn);

        var output = subject.ToList();
        for (var edge = 0; edge < clip.Count && output.Count > 0; edge++)
        {
            var a = clip[edge];
            var b = clip[(edge + 1) % clip.Count];
            double Side(Point p) => turn * ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X));

            var input = output;
            output = [];
            for (var i = 0; i < input.Count; i++)
            {
                var from = input[i];
                var to = input[(i + 1) % input.Count];
                double sideFrom = Side(from), sideTo = Side(to);
                if (sideFrom >= 0) output.Add(from);
                if (sideFrom >= 0 != sideTo >= 0)
                {
                    double t = sideFrom / (sideFrom - sideTo);
                    output.Add(new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t));
                }
            }
        }
        return output;
    }
}
