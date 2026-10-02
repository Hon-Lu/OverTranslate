using SkiaSharp;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// A detected line measured along and across its own writing direction, from the quadrilateral the
/// detector drew around it rather than from the upright rectangle that encloses that quadrilateral.
/// </summary>
/// <remarks>
/// The detector returns a rotated box. Everything downstream works with its upright bounds, and for
/// a level line the two are the same. For a tilted one they are not: the upright box is as tall as
/// the line's thickness plus its length times the sine of the tilt, so a chat card set at 30° —
/// measured in region-comic-en-3 — had a 357px line 33px thick reported as a box 211px tall. The
/// glyph height the overlay font is sized from then came out at 0.82 of that, 173px for text whose
/// capitals are 17px: the pitch clamp that normally rescues a loose box only runs on a box more than
/// twice as wide as it is tall, and a tilted line stops being that at around 20°.
///
/// Measured along the line instead, the same estimate lands on 14–19px for every line of that card.
/// </remarks>
/// <param name="Length">The longer side of the quadrilateral: how far the line runs.</param>
/// <param name="Thickness">The shorter side: how thick the line is across its own direction.</param>
/// <param name="AngleDegrees">
/// How far the longer side is turned from horizontal, between -90 and 90.
/// </param>
internal readonly record struct OcrLineGeometry(double Length, double Thickness, double AngleDegrees)
{
    /// <summary>
    /// The tilt from which the line is measured along itself for the overlay font.
    /// </summary>
    /// <remarks>
    /// Below it the upright box is kept, so a level page reads exactly as it always has. A long line
    /// still clears the pitch clamp's two-to-one test at 15°; the slightly tilted lines in the corpus
    /// (region-comic-en-3, 5–10°) were already sized correctly from the upright box and must not move.
    /// </remarks>
    public const double TiltedFromDegrees = 15;

    /// <summary>
    /// Past this the longer side runs closer to vertical than horizontal, which on the horizontal
    /// pipeline is a box narrower than it is tall — a lone letter, a stray mark — and not a line
    /// tilted that far. Its "length" would be its height, so it is left to the upright box.
    /// </summary>
    public const double TiltedToDegrees = 45;

    /// <summary>
    /// How much longer than thick a quadrilateral has to be before its angle is believed.
    /// </summary>
    /// <remarks>
    /// The detector fits its box to a blob, and on a short blob the fit can come out turned on a
    /// page that is perfectly level: measured over 85 level captures (688 blocks), the one box that
    /// read as tilted was a two-character 免費 on a web page, 35x21 at -17°. The same two-to-one
    /// test the pitch clamp uses: a box that is not clearly longer than it is thick has no direction
    /// to speak of. It costs one tilted word in region-comic-en-3, "TOO." at 51x28, which keeps the
    /// upright estimate.
    /// </remarks>
    public const double MinLengthToThickness = 2;

    /// <summary>Whether this line is turned far enough for its upright box to misstate its size.</summary>
    public bool IsTilted =>
        Length >= Thickness * MinLengthToThickness &&
        Math.Abs(AngleDegrees) is >= TiltedFromDegrees and <= TiltedToDegrees;

    /// <summary>The geometry of a detector quadrilateral, or null when it is not one.</summary>
    /// <param name="points">The box's four corners in order, as the detector returns them.</param>
    public static OcrLineGeometry? FromQuad(IReadOnlyList<SKPointI>? points)
    {
        if (points is not { Count: 4 })
            return null;

        double first = Distance(points[0], points[1]);
        double second = Distance(points[1], points[2]);
        if (first <= 0 || second <= 0)
            return null;

        var (from, to) = first >= second ? (points[0], points[1]) : (points[1], points[2]);
        double angle = Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI;
        if (angle > 90) angle -= 180;
        else if (angle <= -90) angle += 180;

        return new OcrLineGeometry(Math.Max(first, second), Math.Min(first, second), angle);
    }

    private static double Distance(SKPointI a, SKPointI b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));
}
