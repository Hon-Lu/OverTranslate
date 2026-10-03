using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Vector = System.Windows.Vector;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Whether a group of vertical columns is tilted, and by how much, from the quadrilaterals the
/// detector drew round its columns.
/// </summary>
/// <remarks>
/// <para>Calling a level column tilted is the worst way this can go wrong: the translation is then
/// drawn askew over writing that is straight. Missing a slight tilt costs almost nothing, because a
/// column a few degrees off drawn straight is still read as the column it replaces. So every
/// threshold here sits on the safe side of a measured gap rather than in the middle of it.</para>
///
/// <para>MEASURED over the 171 vertical pages of the corpus (vertical-image-ja/ja2/ja3,
/// vertical-manga-web) and the five tilted pages of vertical-image-ja4, with every group judged
/// tilted checked by eye. Straight columns read up to 2.5° off vertical — a 慧月様 at 2.3°, a
/// frieren そんなことはない at 2.6°, 俺の本職は at 2.9° on its tilted member — and the smallest real
/// tilt in the corpus was 4.7°. <see cref="FromDegrees"/> sits between them. At 4° the column
/// pipeline finds 14 of the 19 tilted groups of ja4 and every one of the three tilted groups in the
/// corpus (ja3 432/012 at −12°, 472/013 at 7°, mit-b at −5.5°), and calls nothing straight tilted.
/// The five it misses on ja4 are 2.3–3.8°.</para>
///
/// <para>The rule is the one the horizontal cards use (see <see cref="TiltedLayout"/>): a lone line
/// does not say the page is turned. Two or more columns have to agree — the same way, all past the
/// floor, within <see cref="MaxSpread"/> of each other. A single column is believed only when it is
/// long (<see cref="LoneLength"/>) and clearly turned (<see cref="LoneFromDegrees"/>), which is
/// what a tilted strip of paper with one line on it looks like (ja4 封印は… at 7.8°, 返事は… at
/// 12.3°).</para>
/// </remarks>
internal static class TiltedColumns
{
    /// <summary>How far from vertical every agreeing column has to be.</summary>
    internal const double FromDegrees = 4;

    /// <summary>How far apart the agreeing columns' angles may be.</summary>
    internal const double MaxSpread = 3;

    /// <summary>
    /// Past this a "column" is closer to a diagonal than to writing that runs down the page, which
    /// nothing in the corpus is; it is left upright rather than guessed at.
    /// </summary>
    internal const double ToDegrees = 30;

    /// <summary>How much longer than thick a column's quadrilateral has to be before its angle counts.</summary>
    /// <remarks>
    /// The detector's fit to a short blob can come out turned on a straight page; the same two to one
    /// <see cref="OcrLineGeometry.MinLengthToThickness"/> asks of a line.
    /// </remarks>
    internal const double TrustedLength = 2;

    /// <summary>How long a column has to be to say on its own that it is tilted.</summary>
    internal const double LoneLength = 5;

    /// <summary>How far a column alone has to be turned.</summary>
    internal const double LoneFromDegrees = 6;

    /// <summary>
    /// A column's angle as its deviation from vertical: negative when its foot lies right of its
    /// head. This is the turn that sets an upright column onto it — see <see cref="TiltedText.Degrees"/>.
    /// </summary>
    internal static double Deviation(OcrLineGeometry line) =>
        line.AngleDegrees > 0 ? line.AngleDegrees - 90 : line.AngleDegrees + 90;

    /// <summary>Whether a quadrilateral's longer side runs down the page rather than across it.</summary>
    internal static bool RunsDown(OcrLineGeometry line) =>
        Math.Abs(line.AngleDegrees) > OcrLineGeometry.TiltedToDegrees;

    /// <summary>
    /// The angle a group of columns is tilted by, or null when it is not to be called tilted.
    /// </summary>
    /// <param name="columns">Every column of the group; those too short to trust are passed over.</param>
    /// <param name="trustedLength">
    /// <see cref="TrustedLength"/>, or more for quadrilaterals from a reduced picture, where a short
    /// column's fit comes out at barely two to one.
    /// </param>
    internal static double? Angle(IEnumerable<OcrLineGeometry> columns, double trustedLength = TrustedLength)
    {
        var trusted = columns
            .Where(line => RunsDown(line) && line.Length >= line.Thickness * trustedLength)
            .ToList();
        if (trusted.Count == 0) return null;

        if (trusted.Count == 1)
        {
            var line = trusted[0];
            double alone = Deviation(line);
            return line.Length >= line.Thickness * LoneLength &&
                   Math.Abs(alone) is >= LoneFromDegrees and <= ToDegrees
                ? alone
                : null;
        }

        var angles = trusted.Select(Deviation).ToList();
        if (angles.Any(angle => Math.Abs(angle) is < FromDegrees or > ToDegrees)) return null;
        if (angles.Any(angle => angle > 0) && angles.Any(angle => angle < 0)) return null;
        if (angles.Max() - angles.Min() > MaxSpread) return null;

        return trusted.Sum(line => Deviation(line) * line.Length) / trusted.Sum(line => line.Length);
    }

    /// <summary>
    /// How a group of columns is to be drawn, or null when it is not tilted — see <see cref="Angle"/>.
    /// </summary>
    /// <remarks>Every column needs its quadrilateral: one without has nothing to be erased inside.</remarks>
    internal static TiltedText? For(IReadOnlyList<OcrTextBlock> columns)
    {
        if (columns.Count == 0 || columns.Any(column => column.LineGeometry is null)) return null;
        if (Angle(columns.Select(column => column.LineGeometry!.Value)) is not { } degrees) return null;

        return TiltedText.FromColumns(degrees, [.. columns.Select(column => Quad(Box(column), column.LineGeometry!.Value))]);
    }

    /// <summary>
    /// The quadrilateral of a line, rebuilt from its centre and measurements, corner after corner
    /// round its edge with the first side running along the line.
    /// </summary>
    internal static Point[] Quad(Rect box, OcrLineGeometry line)
    {
        var centre = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        var radians = line.AngleDegrees * Math.PI / 180;
        var along = new Vector(Math.Cos(radians), Math.Sin(radians)) * (line.Length / 2);
        var across = new Vector(-Math.Sin(radians), Math.Cos(radians)) * (line.Thickness / 2);
        return [centre - along - across, centre + along - across, centre + along + across, centre - along + across];
    }

    // The detector's own box: the quadrilateral's centre is its centre.
    private static Rect Box(OcrTextBlock column) =>
        column.LayoutBounds is { IsEmpty: false, Width: > 0, Height: > 0 } layout ? layout : column.Bounds;
}
