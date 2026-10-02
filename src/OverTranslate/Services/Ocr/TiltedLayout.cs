using System.Windows;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using Vector = System.Windows.Vector;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Turns a card of tilted lines level for the grouping rules, and back again afterwards.
/// </summary>
/// <remarks>
/// <para>Every grouping rule measures <see cref="OcrTextBlock.LayoutBounds"/>, the upright box
/// around the detector's quadrilateral. On a tilted line that box is as tall as the line's
/// thickness plus its length times the sine of the tilt, so the quantities the rules read — vertical
/// gap, overlap, alignment, glyph height — stop describing the text. Measured on
/// region-comic-en-3: on a card turned 9.4° the five lines of one comment overlapped 0.87–0.90 of
/// their height, and the dialogue grouper took all five for one 259px row, which is what then let a
/// 297px sound effect read as the next line (案例 (4)); on a 30° card the screenshot and panel
/// paths saw every line start inside the one above and left each on its own.</para>
///
/// <para>So nothing in the rules changes. Before grouping, each cluster of consistently tilted
/// lines gets a level box in a frame of its own, and the rules run on that; afterwards the upright
/// box goes back, because <see cref="OcrTextBlock.LayoutBounds"/> means the box the detector drew.
/// <see cref="OcrTextBlock.Bounds"/> and both glyph heights the overlays size from are never
/// touched.</para>
///
/// <para>Only clusters: two or more long lines at the same angle whose boxes meet. A lone tilted
/// line keeps its upright box. That is what keeps level pages exactly as they were, more than the
/// angle floor: across 84 level English captures and 233 Japanese ones, lines between 3° and 7.5°
/// do occur — a name plate, a slanted caption — but never two of them together.</para>
///
/// <para>A lone line long and tilted enough to be sure of — see <see cref="LoneMinDegrees"/> — is
/// still drawn along its slope, so its band does not leave both of its ends showing; its box is
/// never changed, and grouping does not see it.</para>
/// </remarks>
internal static class TiltedLayout
{
    /// <summary>The tilt from which a line can seed a cluster.</summary>
    private const double MinDegrees = 3;

    /// <summary>
    /// Past this a line runs closer to vertical than horizontal; see
    /// <see cref="OcrLineGeometry.TiltedToDegrees"/>.
    /// </summary>
    private const double MaxDegrees = OcrLineGeometry.TiltedToDegrees;

    /// <summary>
    /// How much longer than thick a line has to be before its angle is trusted to steer others.
    /// Stricter than <see cref="OcrLineGeometry.MinLengthToThickness"/>: a short box's angle is the
    /// detector's fit to a blob, and here it would move every line around it.
    /// </summary>
    private const double MinLengthToThickness = 3;

    /// <summary>How far apart in angle two lines can be and still be one card.</summary>
    private const double MaxAngleSpread = 5;

    /// <summary>How many nearby lines a line's own angle is averaged over.</summary>
    private const int LocalAngleLines = 3;

    /// <summary>The tilt from which a line on its own is drawn tilted.</summary>
    /// <remarks>
    /// Above <see cref="MinDegrees"/>, which only has to keep a line from seeding a cluster: a lone
    /// line has no neighbour to agree with it, and a level line drawn tilted is far more visible than
    /// a tilted one drawn level. Across the 84 English and 233 Japanese level captures, a long line's
    /// box came out tilted by up to 3.4° — an italic subtitle, 4.28° at worst across 60 perturbed
    /// frames — while the lone line on region-comic-en-3's second card never went below 6.85°.
    /// </remarks>
    private const double LoneMinDegrees = 6;

    /// <summary>How much longer than thick a line on its own has to be to be drawn tilted.</summary>
    /// <remarks>
    /// Stricter than <see cref="MinLengthToThickness"/>. A short box's angle is the detector's fit to
    /// a blob: a level name plate measured 3.0–4.6 times as long as thick and came out at up to 9.7°.
    /// A real slanted line close to this ratio — a sign at 20°, 4.2–8.4 across the same perturbations
    /// — is drawn tilted in some frames and level in others.
    /// </remarks>
    private const double LoneMinLengthToThickness = 5;

    /// <summary>
    /// Level boxes for the lines of every tilted cluster, the rest untouched. A long, clearly tilted
    /// line that is no cluster's keeps its box and is only marked to be drawn along its slope.
    /// Returns <paramref name="blocks"/> itself when there is nothing to straighten or mark.
    /// </summary>
    internal static List<OcrTextBlock> Straighten(List<OcrTextBlock> blocks)
    {
        var count = blocks.Count;
        if (count == 0)
            return blocks;

        var reliable = blocks.Select(IsReliable).ToArray();
        if (reliable.Count(r => r) < 2)
            return MarkLone(blocks, blocks, Enumerable.Repeat(-1, count).ToArray(), 0);

        var parent = Enumerable.Range(0, count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        for (var i = 0; i < count; i++)
        for (var j = i + 1; j < count; j++)
        {
            if (!reliable[i] || !reliable[j]) continue;
            if (Math.Abs(Angle(blocks[i]) - Angle(blocks[j])) > MaxAngleSpread) continue;
            if (!blocks[i].LayoutBounds.IntersectsWith(blocks[j].LayoutBounds)) continue;
            parent[Find(i)] = Find(j);
        }

        var clusters = Enumerable.Range(0, count)
            .Where(i => reliable[i])
            .GroupBy(Find)
            .Where(cluster => cluster.Count() >= 2)
            .Select(cluster => cluster.ToList())
            .ToList();

        // What is not a trusted line of its own — a short word, a username whose box is too square
        // to give an angle, a mark — rides with the cluster whose line it sits on.
        var member = new int[count];
        Array.Fill(member, -1);
        for (var c = 0; c < clusters.Count; c++)
            foreach (var i in clusters[c])
                member[i] = c;
        for (var i = 0; i < count; i++)
        {
            if (member[i] >= 0) continue;
            var centre = Centre(blocks[i].LayoutBounds);
            for (var c = 0; c < clusters.Count && member[i] < 0; c++)
                if (clusters[c].Any(k => blocks[k].LayoutBounds.Contains(centre)))
                    member[i] = c;
        }

        var result = blocks.ToList();
        for (var c = 0; c < clusters.Count; c++)
        {
            var lines = clusters[c];
            var all = Enumerable.Range(0, count).Where(i => member[i] == c).ToList();
            var pivot = new Point(
                all.Average(i => Centre(blocks[i].LayoutBounds).X),
                all.Average(i => Centre(blocks[i].LayoutBounds).Y));
            var angles = all.ToDictionary(i => i, i => LocalAngle(blocks, lines, i));

            // A card turned rigidly keeps its margin square to its lines; a card seen in
            // perspective keeps its letters upright and its margin vertical while the lines slope.
            // Rotating the second turns its margin into a slant — 0.35 line heights per line on the
            // 30° card, past the set-solid rule's 0.35 — so whichever transform lines the left edges
            // up better is the one that describes the card. Measured 0.05 against 0.35 on the 30°
            // card (sheared) and 0.03 against 0.17 on the 9.4° one (rotated).
            Rect Rotated(int i) => RotatedBox(blocks[i], reliable[i], -angles[i], pivot);
            Rect Sheared(int i) => ShearedBox(blocks[i], angles[i], pivot);
            var sheared = LeftMisalignment(lines, Sheared) < LeftMisalignment(lines, Rotated);
            var place = sheared ? (Func<int, Rect>)Sheared : Rotated;
            var boxes = all.ToDictionary(i => i, place);

            // Level, the boxes can still overlap: with the letters upright on a sloped line, each
            // letter's width times the sine of the slope is counted into the line's thickness. The
            // 30° card's lines sat 0.54–0.58 of a thickness apart, against 0.94–1.05 for the same
            // chat drawn level, so every pair still read as one line set on top of the other. The
            // cluster's own line pitch caps the thickness; it only ever thins a box, so lines that
            // do not overlap — a list, a single line per entry — keep the one they have.
            var cap = LinePitch(lines.Select(i => boxes[i]));
            foreach (var i in all)
            {
                var box = boxes[i];
                if (cap is { } height && box.Height > height)
                    box = new Rect(box.X, box.Y + (box.Height - height) / 2, box.Width, height);

                var block = blocks[i];
                result[i] = block with
                {
                    LayoutBounds = box,
                    LayoutGlyphHeight = OnnxOcrEngine.LayoutGlyphHeightFor(block.LayoutScript, box, block.Text),
                    UprightLayoutBounds = block.UprightLayoutBounds ?? block.LayoutBounds,
                    // What the overlays need to draw the group this line ends up in along the
                    // card rather than across it — see TiltedText.
                    TiltedLines =
                    [
                        new TiltedLine(c, sheared, angles[i], reliable[i] ? Length(block) : 0,
                            Centre(block.LayoutBounds), LevelSize(block, box, sheared), Outline(block)),
                    ],
                };
            }
        }

        return MarkLone(blocks, clusters.Count == 0 ? blocks : result, member, clusters.Count);
    }

    /// <summary>
    /// <paramref name="current"/> with every long, clearly tilted line of <paramref name="blocks"/>
    /// that is no cluster's marked as one of its own, numbered on from
    /// <paramref name="nextCluster"/>; <paramref name="current"/> itself when there is none.
    /// </summary>
    /// <remarks>
    /// Only marked: its box stays the upright one, so grouping sees exactly what it saw before, and
    /// what changes is how the group it ends up alone in is drawn — along the line, as a rotated card
    /// is, since one line cannot say whether its card was turned or seen in perspective. A group that
    /// takes in anything else is drawn level, as before; see <see cref="CombineTilted"/>.
    /// </remarks>
    private static List<OcrTextBlock> MarkLone(
        List<OcrTextBlock> blocks, List<OcrTextBlock> current, int[] member, int nextCluster)
    {
        List<OcrTextBlock>? result = null;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (member[i] >= 0 || !IsLone(block)) continue;

            var line = block.LineGeometry!.Value;
            result ??= current.ToList();
            result[i] = current[i] with
            {
                TiltedLines =
                [
                    new TiltedLine(nextCluster++, false, line.AngleDegrees, line.Length,
                        Centre(block.LayoutBounds), new Size(line.Length, line.Thickness), Outline(block)),
                ],
            };
        }
        return result ?? current;
    }

    /// <summary>
    /// Puts back the box the detector drew on everything <see cref="Straighten"/> touched, once the
    /// grouping that needed the level one is done, and says how each group made wholly of tilted
    /// lines is to be drawn — see <see cref="TiltedText"/>. Returns <paramref name="groups"/> itself
    /// when nothing was straightened or marked.
    /// </summary>
    internal static List<OcrTextBlock> Restore(List<OcrTextBlock> groups)
    {
        if (!groups.Any(group => group.UprightLayoutBounds.HasValue || group.TiltedLines is not null))
            return groups;

        return groups
            .Select(group => group.UprightLayoutBounds.HasValue || group.TiltedLines is not null
                ? group with
                {
                    // A lone line was only marked, and kept its own box throughout.
                    LayoutBounds = group.UprightLayoutBounds ?? group.LayoutBounds,
                    UprightLayoutBounds = null,
                    TiltedLines = null,
                    Tilt = group.TiltedLines is { } lines ? TiltedText.From(lines) : null,
                }
                : group)
            .ToList();
    }

    /// <summary>
    /// The tilted lines several blocks were made of, for a group built from them: null unless every
    /// one of them was tilted, so a group with a level piece in it is drawn as level text is.
    /// </summary>
    internal static IReadOnlyList<TiltedLine>? CombineTilted(IReadOnlyList<OcrTextBlock> blocks) =>
        blocks.All(block => block.TiltedLines is not null)
            ? [.. blocks.SelectMany(block => block.TiltedLines!)]
            : null;

    /// <summary>
    /// The upright box several lines cover between them, for a group built from them: null when
    /// none of them was straightened, so a level page carries nothing new.
    /// </summary>
    internal static Rect? CombineUpright(IReadOnlyList<OcrTextBlock> blocks) =>
        blocks.Any(block => block.UprightLayoutBounds.HasValue)
            ? blocks.Select(block => block.UprightLayoutBounds ?? block.LayoutBounds).Aggregate(Rect.Union)
            : null;

    private static bool IsReliable(OcrTextBlock block) =>
        block.LineGeometry is { } line &&
        line.Length >= line.Thickness * MinLengthToThickness &&
        Math.Abs(line.AngleDegrees) is >= MinDegrees and <= MaxDegrees;

    private static bool IsLone(OcrTextBlock block) =>
        block.LineGeometry is { } line &&
        line.Length >= line.Thickness * LoneMinLengthToThickness &&
        Math.Abs(line.AngleDegrees) is >= LoneMinDegrees and <= MaxDegrees;

    private static double Angle(OcrTextBlock block) => block.LineGeometry!.Value.AngleDegrees;

    private static double Length(OcrTextBlock block) => block.LineGeometry!.Value.Length;

    /// <summary>
    /// The angle of the lines nearest this one, weighted by length.
    /// </summary>
    /// <remarks>
    /// A card in perspective does not have one angle: the 30° card drifts from 30.9° at the top to
    /// 22.9° at the bottom. Turned by one figure for the whole card, the short lines far from the
    /// middle — a username, a last word — landed several pixels off, and the boundary between the
    /// card's last two comments went with them.
    /// </remarks>
    private static double LocalAngle(List<OcrTextBlock> blocks, List<int> lines, int index)
    {
        var centre = Centre(blocks[index].LayoutBounds);
        var nearest = lines
            .OrderBy(i => (Centre(blocks[i].LayoutBounds) - centre).Length)
            .Take(LocalAngleLines)
            .ToList();

        return nearest.Sum(i => Angle(blocks[i]) * Length(blocks[i])) / nearest.Sum(i => Length(blocks[i]));
    }

    /// <summary>
    /// The median left-edge offset between neighbouring lines, in line heights.
    /// </summary>
    private static double LeftMisalignment(List<int> lines, Func<int, Rect> place)
    {
        var boxes = lines.Select(place).OrderBy(box => box.Y + box.Height / 2).ToList();
        var offsets = boxes
            .Zip(boxes.Skip(1), (a, b) => Math.Abs(a.Left - b.Left) / ((a.Height + b.Height) / 2))
            .OrderBy(offset => offset)
            .ToList();

        return offsets.Count > 0 ? offsets[offsets.Count / 2] : 0;
    }

    /// <summary>
    /// The cluster's tight line pitch — the lower quartile of the distance between neighbouring
    /// lines, so a few wider gaps between comments do not raise it. Null with nothing to measure.
    /// </summary>
    private static double? LinePitch(IEnumerable<Rect> boxes)
    {
        var centres = boxes.Select(box => box.Y + box.Height / 2).OrderBy(y => y).ToList();
        var pitches = centres
            .Zip(centres.Skip(1), (a, b) => b - a)
            .Where(pitch => pitch > 2)
            .OrderBy(pitch => pitch)
            .ToList();

        return pitches.Count > 0 ? pitches[pitches.Count / 4] : null;
    }

    /// <summary>
    /// The box turned about <paramref name="pivot"/>. A trusted line keeps its own length and
    /// thickness at its new centre: the box around its turned quadrilateral would grow by its length
    /// times the sine of whatever angle it differs from its neighbours by, some 20px on the 30° card.
    /// </summary>
    private static Rect RotatedBox(OcrTextBlock block, bool reliable, double degrees, Point pivot)
    {
        var radians = degrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        var turned = Corners(block).Select(point =>
        {
            double dx = point.X - pivot.X, dy = point.Y - pivot.Y;
            return new Point(pivot.X + dx * cos - dy * sin, pivot.Y + dx * sin + dy * cos);
        }).ToList();
        var box = Enclosing(turned);

        if (!reliable || block.LineGeometry is not { } line)
            return box;

        var centre = Centre(box);
        return new Rect(centre.X - line.Length / 2, centre.Y - line.Thickness / 2, line.Length, line.Thickness);
    }

    /// <summary>The box sheared level about <paramref name="pivot"/>: x stays, y loses the slope.</summary>
    private static Rect ShearedBox(OcrTextBlock block, double degrees, Point pivot)
    {
        var slope = Math.Tan(degrees * Math.PI / 180);
        return Enclosing(Corners(block).Select(point => new Point(point.X, point.Y - (point.X - pivot.X) * slope)));
    }

    /// <summary>
    /// The detector's quadrilateral, rebuilt from its centre and its measurements; the upright box's
    /// corners for a block that carries none.
    /// </summary>
    private static Point[] Corners(OcrTextBlock block)
    {
        var box = block.LayoutBounds;
        if (block.LineGeometry is not { } line)
            return [box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight];

        var centre = Centre(box);
        var radians = line.AngleDegrees * Math.PI / 180;
        var along = new Vector(Math.Cos(radians), Math.Sin(radians)) * (line.Length / 2);
        var across = new Vector(-Math.Sin(radians), Math.Cos(radians)) * (line.Thickness / 2);
        return [centre + along + across, centre + along - across, centre - along + across, centre - along - across];
    }

    /// <summary>
    /// How much room a line takes on the level card, for drawing: the box grouping measured, except
    /// that a sheared line is only as long as its run across the card.
    /// </summary>
    /// <remarks>
    /// Sheared level, the detector's rectangle becomes a parallelogram with slanted ends, and the
    /// box round it reaches past the text at both ends by half the line's thickness times the sine
    /// of the slope — 8px each side on the 30° card, which put the first comment's translation off
    /// the card's edge. The letters are upright, so the text's own ends are the middles of those
    /// slanted ends: its length times the cosine of its angle apart.
    /// </remarks>
    private static Size LevelSize(OcrTextBlock block, Rect box, bool sheared) =>
        sheared && block.LineGeometry is { } line
            ? new Size(line.Length * Math.Cos(line.AngleDegrees * Math.PI / 180), box.Height)
            : box.Size;

    /// <summary>The quadrilateral of <see cref="Corners"/>, corner after corner round its edge.</summary>
    private static Point[] Outline(OcrTextBlock block)
    {
        if (block.LineGeometry is null)
        {
            var box = block.LayoutBounds;
            return [box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft];
        }

        var corners = Corners(block);
        return [corners[3], corners[1], corners[0], corners[2]];
    }

    private static Rect Enclosing(IEnumerable<Point> points)
    {
        var list = points.ToList();
        double left = list.Min(p => p.X), top = list.Min(p => p.Y);
        return new Rect(left, top, list.Max(p => p.X) - left, list.Max(p => p.Y) - top);
    }

    private static Point Centre(Rect box) => new(box.X + box.Width / 2, box.Y + box.Height / 2);
}
