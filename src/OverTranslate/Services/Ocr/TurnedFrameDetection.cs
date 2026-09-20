using System.Drawing;
using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// A second reading of the frame turned a quarter turn, for the pieces the upright one never found.
/// </summary>
/// <remarks>
/// <para>Detection is the one stage of the vertical pipeline that still sees the page the way it was
/// written. Recognition does not: every crop is turned before it is read, which is PaddleOCR's own
/// method and what <see cref="VerticalOcrGeometry.ForRecognition"/> does. So the orientation bias
/// that comes of training on rows lands entirely on detection, and it shows up as whole balloons
/// that are never found — not as bad text, because a box that does not exist is never read.</para>
///
/// <para>MEASURED, over the 15 comic pages in <c>.ai/test-images/vertical-image-ja2</c>: reading the
/// upright frame misses three balloons the old whole-frame rotation found — <c>ああ 次の探索の準備か</c>,
/// <c>冗談でも何でもない</c>, <c>とはいえ</c>. The upright pass is otherwise the better of the two, and
/// by a distance: it is what gives the columns and the balloon boundaries their meaning, which is
/// the whole reason the page is no longer turned. So this adds to it and never replaces it.</para>
///
/// <para>ADDITIVE, and that is the difference from the reference implementation in this domain.
/// manga-image-translator's <c>det_auto_rotate</c> votes on the aspect ratio of what the first pass
/// found and, if the page looks horizontal, re-runs turned and uses THAT result instead. It answers
/// a coarser question — "did I read the page the wrong way round" — and on a page this pipeline
/// already reads correctly it would either never fire or throw away a correct answer. The two
/// orientations here are complementary rather than ranked.</para>
///
/// <para>Costed before it was built. Recognition is per box and dominates, so a second detection is
/// not a second pass: measured on three of those pages, detection is 37–47% of a screenshot read and
/// 11–28% of a whole-page read at 960, and the turned pass costs what the upright one does. Only the
/// boxes the upright pass missed are recognised, and there are three of them across fifteen pages,
/// so the extra recognition does not register.</para>
/// </remarks>
internal static class TurnedFrameDetection
{
    /// <summary>
    /// How much of the smaller box two readings may share before the second is taken to be the same
    /// piece of writing.
    /// </summary>
    /// <remarks>
    /// Deliberately low. What this pass is for is writing the upright one did not find at all, so a
    /// box landing on one that already exists is noise at best and a doubled reading at worst — the
    /// same column recognised twice and drawn twice, one translation on top of the other. It also
    /// covers a column the upright pass found in fragments: the turned pass's whole column contains
    /// each fragment, so its overlap against the smaller of the two is 1.
    /// </remarks>
    private const double SamePieceOfWriting = 0.30;

    /// <summary>Off only for measuring what it is worth; the app always leaves it on.</summary>
    internal static bool Enabled { get; set; } = true;

    /// <summary>
    /// The frame turned the way the whole-frame rotation used to turn it, before this branch moved
    /// the turn onto the individual crops.
    /// </summary>
    /// <remarks>
    /// The same call the old path made, kept the same on purpose: <see cref="ToUpright(Rect, int)"/>
    /// is its inverse and the pair of them is the one piece of this that was already proven — the
    /// old path read these pages correctly through exactly this transform.
    /// </remarks>
    internal static Bitmap Turn(Bitmap source)
    {
        var turned = new Bitmap(source);
        turned.RotateFlip(RotateFlipType.Rotate270FlipNone);
        return turned;
    }

    /// <summary>Where a rectangle on the turned frame sits on the upright one.</summary>
    /// <param name="sourceWidth">The upright frame's width, which the turn pivots around.</param>
    internal static Rect ToUpright(Rect turned, int sourceWidth) => new(
        sourceWidth - (turned.Y + turned.Height),
        turned.X,
        turned.Height,
        turned.Width);

    /// <summary>Whether a piece only this pass found is worth adding.</summary>
    /// <remarks>
    /// A stricter bar than the upright pass is held to, and deliberately so: this one is adding to
    /// a reading that is already right, so the burden of proof is on it, and what a false positive
    /// costs here is a bubble painted over the artwork.
    ///
    /// MEASURED over the 24 comic pages at both sizes. Of 23 additions, the nine that are actually
    /// balloons are every one of them two characters or more — <c>ああ次の探索の準備か</c>,
    /// <c>冗談でも何でもない</c>, <c>とはいえ</c>, <c>俺が不要な存在</c> — while a single character
    /// is a page number or a mark in the picture every time: <c>h</c>, <c>墨</c>, <c>子</c>,
    /// <c>云</c>, <c>0</c>, <c>4</c>, <c>6</c>. A balloon holds a sentence; a lone glyph that one
    /// orientation sees and the other does not is the drawing.
    /// </remarks>
    internal static bool WorthKeeping(OcrTextBlock block) =>
        block.Text.Count(character => !char.IsWhiteSpace(character)) >= 2;

    /// <summary>The same, for everything a read block carries that is measured in pixels.</summary>
    internal static OcrTextBlock ToUpright(OcrTextBlock block, int sourceWidth) => block with
    {
        Bounds = ToUpright(block.Bounds, sourceWidth),
        LayoutBounds = ToUpright(block.LayoutBounds, sourceWidth),
        // Refilled by the vertical grouping from the mapped bounds; carrying the turned frame's
        // rectangles past this point would put one overlay box in a coordinate system of its own.
        SourceLineBounds = null,
    };

    /// <summary>
    /// Which of the turned pass's boxes sit somewhere the upright pass found nothing, as indices
    /// into <paramref name="turned"/>.
    /// </summary>
    internal static List<int> PiecesTheUprightPassMissed(
        IReadOnlyList<Rect> upright, IReadOnlyList<Rect> turned, int sourceWidth)
    {
        var missed = new List<int>();
        var taken = new List<Rect>(upright);

        for (var i = 0; i < turned.Count; i++)
        {
            var candidate = ToUpright(turned[i], sourceWidth);
            if (taken.Any(other => Overlap(candidate, other) > SamePieceOfWriting))
                continue;

            // Against the ones already taken as well, so two turned boxes covering the same place
            // cannot both come through.
            taken.Add(candidate);
            missed.Add(i);
        }

        return missed;
    }

    /// <summary>How much of the smaller of two rectangles the two of them share.</summary>
    private static double Overlap(Rect a, Rect b)
    {
        var shared = Rect.Intersect(a, b);
        if (shared.IsEmpty) return 0;

        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller <= 0 ? 0 : shared.Width * shared.Height / smaller;
    }
}
