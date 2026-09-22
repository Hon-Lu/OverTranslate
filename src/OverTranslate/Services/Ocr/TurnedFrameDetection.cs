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
    /// How much of a turned box the upright pass must already cover before the second reading is
    /// taken to be the same piece of writing.
    /// </summary>
    /// <remarks>
    /// <para>What this pass is for is writing the upright one did not find at all, so a box landing
    /// on one that already exists is noise at best and a doubled reading at worst — the same column
    /// recognised twice and drawn twice, one translation on top of the other. It also covers a
    /// column the upright pass found in fragments: the fragments lie inside the turned pass's whole
    /// column and between them cover it. See <see cref="AlreadyRead"/> for what is measured against
    /// what, which changed after this number was chosen and left it standing.</para>
    ///
    /// <para>MEASURED as a window rather than picked, over the 15 comic pages, because both ends of
    /// it cost text. It began at 0.30, and that is low enough to refuse a column the upright pass
    /// never read. Where one detector quad is drawn around a column, the reading beside it and the
    /// HEAD of the balloon, the upright pass reads that quad as the middle column alone — so the
    /// head IS covered, by a box whose text is not the head's. On 2026-09-20 19 14 57.png the head
    /// is オレは, this pass finds it at 1069,995 36x84, it shares 0.56 of itself with that box, and
    /// the balloon was translated as 正しい判断をしたまでだ without it.</para>
    ///
    /// <para>The window: at 0.50 nothing changes; at 0.60 that head comes back and a balloon that
    /// had been merged into its neighbour separates; 0.70 reads the same; at 0.75 the first doubled
    /// column appears (<c>ねえお姉…お姉</c>) and by 0.85 there are fifteen of them —
    /// <c>改めて改めて</c>, <c>でも料理は本当にでも料理は本当に</c>, <c>6565</c>. This sits in the
    /// middle of that window, and the count of overlapping translation boxes over the corpus does
    /// not move at it.</para>
    ///
    /// <para>That window was measured against the SMALLER of the two rectangles, and the number
    /// survives the change of denominator because the case it was calibrated on is unaffected: the
    /// head <c>オレは</c> IS the smaller of its pair, so 0.56 of the smaller and 0.56 of the
    /// candidate are the same number. What moved is everything else. A speck the upright pass read
    /// inside a column it missed falls from 0.79 to 0.02, which is the whole point. And a column the
    /// upright pass really did read falls too — to 0.58 on <c>剣士…？</c>, because the turned box is
    /// as wide as the balloon around it — so the doubled columns at 0.75 and above are no longer
    /// what this bar holds back. <see cref="SaysWhatWasAlreadyRead"/> holds them back, by asking the
    /// question the rectangles were never able to answer.</para>
    /// </remarks>
    private const double SamePieceOfWriting = 0.65;

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

    /// <summary>
    /// Whether a piece this pass read is the upright pass saying the same thing again.
    /// </summary>
    /// <remarks>
    /// <para>The geometric test in <see cref="PiecesTheUprightPassMissed"/> is asked before
    /// anything is recognised, so all it can compare is rectangles — and a detector quad is wider
    /// than the ink it holds. MEASURED on
    /// <c>.ai/test-images/vertical-image-ja2/2026-09-20 19 14 59 (2).png</c>: the column
    /// <c>剣士…？</c> and its reading <c>けんし</c> are read upright as two boxes 44 and 16 wide,
    /// this pass finds the same column as one box 102 wide because the balloon around it is empty,
    /// and the two upright boxes cover 0.58 of that. The balloon head <c>オレは</c> this pass exists
    /// to recover is covered 0.56, so there is no bar between them — the rectangles do not carry
    /// the difference.</para>
    ///
    /// <para>The text does. A quarter turn is not a second opinion about WHICH words are there; it
    /// is a second chance at finding them at all. So a candidate that comes back saying what the
    /// upright pass already said, where it already said it, is the same column read twice, and
    /// adding it paints one translation on top of another.</para>
    ///
    /// <para>Asked only against a reading of two characters or more. A lone glyph the upright pass
    /// caught inside a column it otherwise missed — the stray <c>か</c> on
    /// 2026-09-20 19 14 59.png — shares its one character with half the sentences on the page, and
    /// letting it speak for them is the very veto this pass had to be freed from.</para>
    /// </remarks>
    internal static bool SaysWhatWasAlreadyRead(
        OcrTextBlock candidate, IReadOnlyList<OcrTextBlock> upright)
    {
        if (SameWriting.Squeeze(candidate.Text).Length == 0) return false;

        foreach (var block in upright)
        {
            if (SameWriting.Squeeze(block.Text).Length < 2) continue;
            if (Overlap(candidate.Bounds, block.Bounds) <= SharesThePlace) continue;
            if (SameWriting.SaysTheSame(candidate.Text, block.Text)) return true;
        }

        return false;
    }

    /// <summary>How much of the smaller box two readings share before they are in the same place.</summary>
    private const double SharesThePlace = 0.3;

    /// <summary>How much of the smaller of two rectangles the two of them share.</summary>
    private static double Overlap(Rect a, Rect b)
    {
        var shared = Rect.Intersect(a, b);
        if (shared.IsEmpty) return 0;

        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller <= 0 ? 0 : shared.Width * shared.Height / smaller;
    }

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
            // Against the ones already taken as well, so two turned boxes covering the same place
            // cannot both come through.
            if (AlreadyRead(candidate, taken) > SamePieceOfWriting) continue;

            taken.Add(candidate);
            missed.Add(i);
        }

        return missed;
    }

    /// <summary>
    /// How much of a candidate column the upright pass has already read, over all its boxes at
    /// once.
    /// </summary>
    /// <remarks>
    /// <para>Asked of the CANDIDATE and of every box together, and both halves of that are load
    /// bearing.</para>
    ///
    /// <para>It used to be asked of the smaller of the two rectangles, one box at a time, and that
    /// let a speck veto a sentence. MEASURED on
    /// <c>.ai/test-images/vertical-image-ja2/2026-09-20 19 14 59.png</c>: the detector throws one
    /// quad around the two columns <c>パーティを組む</c> and <c>資格なんてない！！</c>, the crop of
    /// two columns at once reads as nothing, and separately the upright pass reads a stray 19x23
    /// <c>か</c> inside the left column. This pass finds both columns cleanly, at 0.88 and 0.89 —
    /// and against the SMALLER rectangle that 19x23 speck covers 0.79 of itself and 0.84 of itself,
    /// so both columns were refused and sixteen characters left off the page. The page ended
    /// <c>いざって時に仲間を見捨てるような奴らに</c>, missing what it is they have no right to do.</para>
    ///
    /// <para>Together rather than one at a time is what keeps the other half of the bargain. A
    /// column the upright pass read in FRAGMENTS is covered by no single fragment, so a
    /// candidate-side test taken pairwise would add the whole column beside the pieces of it and
    /// draw the balloon twice. Unioned, the fragments cover it between them and it is refused,
    /// which is what the pairwise smaller-side test was giving for that case.</para>
    /// </remarks>
    private static double AlreadyRead(Rect candidate, IReadOnlyList<Rect> read)
    {
        var area = candidate.Width * candidate.Height;
        if (area <= 0) return 0;

        var pieces = read
            .Select(box => Rect.Intersect(candidate, box))
            .Where(piece => !piece.IsEmpty && piece.Width > 0 && piece.Height > 0)
            .ToList();
        if (pieces.Count == 0) return 0;

        // Coordinate compression: the pieces cut the candidate into a grid, and a cell is covered
        // or it is not. Exact, and the piece count here is the handful of boxes that touch one
        // column.
        var xs = pieces.SelectMany(p => new[] { p.Left, p.Right }).Distinct().Order().ToArray();
        var ys = pieces.SelectMany(p => new[] { p.Top, p.Bottom }).Distinct().Order().ToArray();
        var covered = 0.0;
        for (var i = 0; i + 1 < xs.Length; i++)
        for (var j = 0; j + 1 < ys.Length; j++)
        {
            var middle = new System.Windows.Point((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2);
            if (pieces.Any(piece => piece.Contains(middle)))
                covered += (xs[i + 1] - xs[i]) * (ys[j + 1] - ys[j]);
        }

        return covered / area;
    }
}
