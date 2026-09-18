using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace OverTranslate.Services.Realtime;

internal record CpuTextRegion(double X, double Y, double Width, double Height, double? GlyphHeight);

internal sealed record CpuMask(Mat Core, Mat Outline, Mat Combined) : IDisposable
{
    public void Dispose() { Core.Dispose(); Outline.Dispose(); Combined.Dispose(); }
}

/// <summary>Stateless CPU segmentation. A text core and its nearby outline are separate masks.</summary>
internal static class CpuTextMask
{
    /// <summary>How far past the glyph body an outline is looked for, as a share of glyph height.</summary>
    /// <remarks>
    /// This is the reach of the whole outline search: nothing further from the body than this can be
    /// recognised as belonging to the text, whatever its contrast. It was .09 clamped to four pixels,
    /// which is under the outline a burnt-in subtitle is actually drawn with at the size realtime
    /// reads one — an English anime subtitle measured 44px of glyph carrying an outline plus its
    /// antialiasing well past four — so the outermost ring of every letter was outside the search
    /// before any threshold had a say.
    /// </remarks>
    private const double OutlineReach = .12;

    /// <summary>Contrast a pixel needs to be taken for outline on its own.</summary>
    /// <inheritdoc cref="OutlineTail"/>
    private const double OutlineSeed = 12;

    /// <summary>Contrast a pixel needs to be taken for outline when it continues one.</summary>
    /// <remarks>
    /// The two thresholds are a hysteresis, and the reason for it is what the leftover actually looks
    /// like: not a missed letter but a dotted dark contour tracing where the text was, which is the
    /// outline's antialiased tail. That tail is a ramp from the outline down to the picture, so no
    /// single threshold separates it — high enough not to eat the picture is high enough to leave the
    /// last pixel or two of every stroke behind, and that is exactly the row of dashes a reader sees
    /// under an erased subtitle.
    ///
    /// So the seed says what is certainly outline and the tail says what may continue one, and only
    /// pixels reachable from a seed are taken. Growth is bounded by <see cref="OutlineGrowth"/>, which
    /// is what keeps a dark scene edge that happens to touch a letter from being followed across the
    /// frame. It also carries the outline past <see cref="OutlineReach"/>, which the seed alone cannot:
    /// the seed is only looked for within reach of the body, and a stroke's outline is not.
    ///
    /// How far it gets is bounded by the recognition box as well, because everything here is computed
    /// inside one. That is why this is worth most on a Latin line, whose box stands well clear of its
    /// glyphs, and least on a CJK one, whose box sits on them. Padding the box to give the growth room
    /// was tried and is wrong: the box is also what the two hats and the Otsu threshold are measured
    /// over, so widening it changes which polarity is taken for the text — over the ja-card corpus,
    /// where the band behind the subtitle is already dark, that alone left half again as much behind
    /// as shipping did.
    ///
    /// Swept at 6, 10, 14 and 18 over the three subtitle corpora, what is erased hardly moves — under
    /// a fifth of a point between the extremes — while how much of the frame is masked falls steadily
    /// as it rises. So it is set by the other end: low enough to be a genuine second threshold, high
    /// enough that film grain and compression noise are not a path for the growth to walk along.
    /// </remarks>
    private const double OutlineTail = 10;

    /// <summary>How many pixels the tail may be followed away from a seed.</summary>
    /// <inheritdoc cref="OutlineTail"/>
    private const int OutlineGrowth = 6;

    public static CpuMask Build(Mat source, IReadOnlyList<CpuTextRegion> lines)
    {
        var core = new Mat(source.Size(), MatType.CV_8UC1, Scalar.Black);
        var outline = new Mat(source.Size(), MatType.CV_8UC1, Scalar.Black);
        var combined = new Mat(source.Size(), MatType.CV_8UC1, Scalar.Black);
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
            foreach (var line in lines)
            {
                if (!double.IsFinite(line.X + line.Y + line.Width + line.Height) || line.Width <= 0 || line.Height <= 0) continue;
                double height = Math.Max(1, Math.Min(line.GlyphHeight ?? line.Height, line.Height));
                int reach = Math.Clamp((int)Math.Ceiling(height * OutlineReach), 2, 6);
                var box = Clip(line.X - reach, line.Y - reach, line.X + line.Width + reach,
                    line.Y + line.Height + reach, source.Size());
                if (box.Width == 0 || box.Height == 0) continue;
                using var roi = new Mat(gray, box);
                using var light = new Mat();
                using var dark = new Mat();
                using var binary = new Mat();
                int size = Math.Clamp((int)Math.Round(height * .55) | 1, 5, 25);
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(size, size));
                Cv2.MorphologyEx(roi, light, MorphTypes.TopHat, kernel);
                Cv2.MorphologyEx(roi, dark, MorphTypes.BlackHat, kernel);
                // Which polarity the outline search runs against. Not which one the text is — that
                // is what this used to be taken for, and it is wrong often enough to matter; see
                // Body. All it decides now is which way round the body/outline pair is tried.
                bool brightText = Cv2.Mean(light).Val0 >= Cv2.Mean(dark).Val0;
                var body = brightText ? light : dark;
                var opposite = brightText ? dark : light;
                Body(body, binary);
                using var reverse = new Mat();
                Body(opposite, reverse);
                using var fringe = new Mat();
                using var near = new Mat();
                using var fringeContrast = new Mat();
                using var one = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
                using var expansion = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(reach * 2 + 1, reach * 2 + 1));
                Cv2.Dilate(binary, near, expansion);
                Cv2.Threshold(opposite, fringeContrast, OutlineSeed, 255, ThresholdTypes.Binary);
                Cv2.BitwiseAnd(near, fringeContrast, fringe);
                GrowAlongTail(fringe, opposite, one);
                Cv2.Dilate(fringe, fringe, one); // Include the antialiased edge of a detected outline.
                Cv2.Dilate(binary, binary, one); // One pixel for anti-aliasing, not a whole line band.
                Cv2.Dilate(reverse, reverse, one);
                Cv2.BitwiseOr(binary, reverse, binary);
                using var coreTarget = new Mat(core, box);
                using var outlineTarget = new Mat(outline, box);
                Cv2.BitwiseOr(coreTarget, binary, coreTarget);
                Cv2.BitwiseOr(outlineTarget, fringe, outlineTarget);
            }
            Cv2.BitwiseOr(core, outline, combined);
            using var antialias = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            // Expand the detected glyph/outline by two further pixels to cover faint halos.
            // Apply after merging ROIs so the expansion is not clipped at an OCR box edge.
            //
            // Two, and GrowAlongTail as well. It was cut to one when the growth was added, on the
            // reasoning that the growth follows the outline's tail where it actually is instead of
            // paying for every glyph everywhere to reach one — and that reasoning still holds, the
            // growth is worth most of what is erased here. What was wrong was the measurement that
            // said the second pixel could then go: it counted what is left in the ring just outside
            // the MASK, and a smaller mask puts that ring further from the glyph, where there is
            // nothing left to find either way. It favours the smaller mask by construction.
            //
            // Measured again over a band fixed by the text rather than by the mask — of the pixels
            // around a glyph that the source has darker than the scene, which is what an outline is,
            // the share still darker than the scene after the repair:
            //
            //   corpus                  before the growth   growth + 1px   growth + 2px
            //   chat-room (panel)                  37.8%          39.8%          36.8%
            //   ja-game (dialogue)                 46.5%          53.5%          40.1%
            //   ja-card                            74.9%          75.2%          68.8%
            //   ja-video                           17.3%          11.0%           9.7%
            //   en                                 30.7%          13.5%          12.3%
            //
            // One pixel is worse than what it replaced on the two corpora of game text — a reader
            // reported exactly that, as colour left along the edge of erased words — while two
            // pixels and the growth together are the best of the three everywhere. The cost is the
            // masked share of the frame: 28.0% to 30.7% on chat-room, 1.76% to 1.92% on ja-game,
            // 5.77% to 6.23% on ja-card, and a third of ja-card's tiles moving from the
            // full-resolution repair to the shared half-resolution one, because a thicker hole is
            // what decides that. That is the trade being made: slightly more picture interpolated,
            // and no contour tracing where the words were.
            Cv2.Dilate(combined, combined, antialias, iterations: 2);
            return new(core, outline, combined);
        }
        catch { core.Dispose(); outline.Dispose(); combined.Dispose(); throw; }
    }

    /// <summary>
    /// One response's body mask: Otsu, then the same threshold again with a floor under it so a
    /// response with nothing in it cannot be split into one anyway.
    /// </summary>
    /// <remarks>
    /// <para>Run over both hats rather than only the one the vote picked, which is why it is a
    /// method: the two have to be thresholded the same way by construction, not by two copies of it
    /// staying in step.</para>
    ///
    /// <para>The vote compares the mean of the two hats over the box and takes the louder one for
    /// the text, on the reasoning that a glyph carries more contrast energy than its background. A
    /// line of bright text on a dark scene breaks that, and the kernel is why: the gaps between
    /// glyphs are narrower than it is, so the closing behind the black hat fills them, and the black
    /// hat then answers for the whole band of picture <em>between</em> the glyphs — which is more
    /// area than the strokes themselves. Measured on the frame this was reported from (white text,
    /// grey 254, on a dark prison scene): over the line, mean black hat 65.1 against mean top hat
    /// 47.6, so the vote said dark, and the body mask became the background between the words.</para>
    ///
    /// <para>The glyphs are not lost when that happens, because the outline search picks them up
    /// from the other side: they are the "outline" around that body. But it only reaches
    /// <see cref="OutlineReach"/> from a seed and then follows a tail, and the seeds are exactly the
    /// filled gaps — so a stroke with other strokes beside it is found and a stroke standing on its
    /// own is not, because closing fills nothing around it and the response there is flat. In the
    /// same frame the black hat inside the kanji cluster's gaps averages 118 while beside the
    /// exclamation mark's bar it averages 5.3 and peaks at 9, against a seed of
    /// <see cref="OutlineSeed"/> and a body threshold of 80. In Japanese those lone strokes are the
    /// punctuation, which is why what stayed on screen was every exclamation mark in the line while
    /// the kanji beside them all went. Measured over four subtitle corpora, the leftover blobs of
    /// 20px or more are 11 on the game corpus (five of them one frame's five exclamation marks), 4
    /// on the card corpus and 2 on the video corpus.</para>
    ///
    /// <para>So both hats get a body mask and the union is taken. It costs nothing measurable,
    /// because in practice the second one lands almost entirely inside what the outline search had
    /// already covered — over the same four corpora the masked share of the frame moves by at most
    /// 0.01 of a point (1.77% to 1.77%, 5.76% to 5.77%, 8.88% to 8.89%, 8.25% to 8.25%) and the
    /// mask costs 6.8ms against 7.0ms — and the leftovers above go to zero.</para>
    ///
    /// <para>Running the whole outline search both ways as well was measured and rejected. It is the
    /// only variant that also empties the Latin corpus's leftovers, and reading those frames is what
    /// settles it: they are not text. They are the picture inside a Latin recognition box, which
    /// stands well clear of its glyphs — a shoe, a floorboard, a drum stand. The masked share rises
    /// by a tenth to a fifth of itself (1.77% to 2.12%, 5.76% to 6.79%) to rub those out, which is
    /// more of the picture interpolated away for no text erased. Deciding the polarity by each hat's
    /// peak instead of its mean was also tried: it is worse on the card corpus (38 leftover blobs
    /// against 4) because a single bright speck decides the whole line.</para>
    /// </remarks>
    private static void Body(Mat response, Mat into)
    {
        double otsu = Cv2.Threshold(response, into, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        Cv2.Threshold(response, into, Math.Max(18, otsu * .8), 255, ThresholdTypes.Binary);
    }

    /// <summary>Extends a seeded outline along its own antialiased tail — see <see cref="OutlineTail"/>.</summary>
    /// <remarks>
    /// A geodesic dilation: grow by a pixel, keep only what the tail threshold allows, put the seed
    /// back so a step can never lose ground. Both mats are the recognition box, not the frame, and the
    /// loop runs a fixed number of times rather than to stability — the point is a bounded reach, and
    /// a run to stability would follow a scene edge for as far as that edge happens to be dark.
    /// </remarks>
    private static void GrowAlongTail(Mat fringe, Mat opposite, Mat one)
    {
        using var tail = new Mat();
        Cv2.Threshold(opposite, tail, OutlineTail, 255, ThresholdTypes.Binary);
        using var grown = new Mat();
        for (int step = 0; step < OutlineGrowth; step++)
        {
            Cv2.Dilate(fringe, grown, one);
            Cv2.BitwiseAnd(grown, tail, grown);
            Cv2.BitwiseOr(grown, fringe, fringe);
        }
    }

    private static Rect Clip(double left, double top, double right, double bottom, Size size)
    {
        int x = (int)Math.Clamp(Math.Floor(left), 0, size.Width);
        int y = (int)Math.Clamp(Math.Floor(top), 0, size.Height);
        int r = (int)Math.Clamp(Math.Ceiling(right), x, size.Width);
        int b = (int)Math.Clamp(Math.Ceiling(bottom), y, size.Height);
        return new(x, y, r - x, b - y);
    }
}

internal sealed record CpuRepair(Mat Image, int FullTiles, int ReducedTiles) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

/// <summary>No background history, GPU, model, or shared mutable frame state.</summary>
internal static class CpuHoleRepair
{
    public static CpuRepair Repair(Mat source, Mat mask, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (source.Type() != MatType.CV_8UC3 || mask.Type() != MatType.CV_8UC1 || source.Size() != mask.Size())
            throw new ArgumentException("Expected matching BGR image and byte mask.");
        var result = source.Clone();
        Mat? coarse = null;
        try
        {
            using var distance = new Mat();
            Cv2.DistanceTransform(mask, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
            int full = 0, reduced = 0;
            const int tile = 96, guard = 24;
            int width = source.Width, height = source.Height;
            for (int y = 0; y < height; y += tile)
            for (int x = 0; x < width; x += tile)
            {
                token.ThrowIfCancellationRequested();
                var area = new Rect(x, y, Math.Min(tile, width - x), Math.Min(tile, height - y));
                using var tileMask = new Mat(mask, area);
                if (Cv2.CountNonZero(tileMask) == 0) continue;
                using var tileDistance = new Mat(distance, area);
                Cv2.MinMaxLoc(tileDistance, out _, out double radius);
                var context = new Rect(Math.Max(0, x - guard), Math.Max(0, y - guard),
                    Math.Min(width, area.Right + guard) - Math.Max(0, x - guard),
                    Math.Min(height, area.Bottom + guard) - Math.Max(0, y - guard));
                using var frame = new Mat(source, context);
                using var holes = new Mat(mask, context);
                // Refuse an unconstrained fill: this crop contains no observation at all.
                if (Cv2.CountNonZero(holes) == holes.Rows * holes.Cols) continue;
                using var filled = new Mat();
                if (radius <= 5 || Math.Min(context.Width, context.Height) < 8)
                {
                    Cv2.Inpaint(frame, holes, filled, 3, InpaintTypes.NS);
                    full++;
                }
                else
                {
                    coarse ??= RepairReduced(source, mask);
                    using var coarseTile = new Mat(coarse, area);
                    using var coarseTarget = new Mat(result, area);
                    coarseTile.CopyTo(coarseTarget, tileMask);
                    reduced++;
                    continue;
                }
                // Every tile reads original pixels; only its own interior is published. The guard
                // makes neighbouring glyphs unavailable as donors without duplicating writes.
                var local = new Rect(x - context.X, y - context.Y, area.Width, area.Height);
                using var interior = new Mat(filled, local);
                using var target = new Mat(result, area);
                interior.CopyTo(target, tileMask);
            }
            token.ThrowIfCancellationRequested();
            return new(result, full, reduced);
        }
        catch { result.Dispose(); throw; }
        finally { coarse?.Dispose(); }
    }
    internal static Mat RepairReduced(Mat source, Mat mask)
    {
        // Compute only the missing content at half resolution; composite at native resolution.
        // Expanding before area resampling prevents a thin masked stroke from disappearing.
        using var expanded = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.Dilate(mask, expanded, kernel);
        var size = new Size(Math.Max(1, source.Width / 2), Math.Max(1, source.Height / 2));
        using var small = new Mat();
        using var smallMask = new Mat();
        Cv2.Resize(source, small, size, interpolation: InterpolationFlags.Area);
        Cv2.Resize(expanded, smallMask, size, interpolation: InterpolationFlags.Area);
        Cv2.Threshold(smallMask, smallMask, 0, 255, ThresholdTypes.Binary);
        using var filled = new Mat();
        Cv2.Inpaint(small, smallMask, filled, 3, InpaintTypes.NS);
        using var full = new Mat();
        Cv2.Resize(filled, full, source.Size(), interpolation: InterpolationFlags.Linear);
        var result = source.Clone();
        full.CopyTo(result, mask);
        return result;
    }

}
