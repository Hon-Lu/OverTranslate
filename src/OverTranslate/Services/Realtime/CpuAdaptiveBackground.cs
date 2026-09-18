using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace OverTranslate.Services.Realtime;

internal record CpuTextRegion(double X, double Y, double Width, double Height, double? GlyphHeight);

/// <summary>Stateless CPU segmentation: the text, and the outline and antialiasing drawn with it.</summary>
internal static class CpuTextMask
{
    /// <summary>How much picture is taken in around a recognition box, as a share of glyph height.</summary>
    /// <remarks>
    /// Everything here is measured inside one box, so this is two things at once: the context the two
    /// hats and their Otsu thresholds are computed over, and the room the mask has to grow past the
    /// box. It is not an allowance the mask is entitled to fill. Widening it feeds more picture into
    /// the thresholds, and at .12 clamped to six — what it was while the mask was a band of fixed
    /// width around the body — the card corpus put a third of its tiles onto the half-resolution
    /// repair and the game corpus tripled the mask islands that hold no text at all.
    /// </remarks>
    private const double BoxPadding = .09;

    /// <summary>Contrast a pixel needs to continue the text's fade outward.</summary>
    /// <inheritdoc cref="Build"/>
    private const double OutlineTail = 10;

    /// <summary>How far that fade may be followed away from the text.</summary>
    /// <remarks>
    /// Swept over five corpora. Each pixel buys less than the one before it and costs more picture:
    /// from three to four, what is left along the game corpus's glyphs falls 22.3% to 17.6% for two
    /// hundredths of a point of disturbed picture, and from four to five it falls 17.6% to 15.1% for
    /// two more — while on the video and Latin corpora the fifth pixel erases nothing further and
    /// disturbs a twentieth of a point more. Four is where the two curves cross.
    /// </remarks>
    private const int OutlineGrowth = 4;

    /// <summary>Both hats' bodies, followed outward along the text's own fade.</summary>
    /// <remarks>
    /// <para>Text is whatever stands out from its surroundings inside a recognition box, in either
    /// direction, so both hats get a body mask and the union is taken. Nothing votes on which
    /// direction the text is. That vote existed — the louder hat over the box was taken for the text
    /// and the other one searched for its outline — and it is wrong exactly where it costs most: a
    /// line of bright text on a dark scene has gaps between its glyphs narrower than the kernel, so
    /// the closing behind the black hat fills them and the dark response answers for the whole band
    /// of picture <em>between</em> the words, which is more area than the strokes are. The box voted
    /// dark, the body mask became the background between the glyphs, and the glyphs survived as
    /// whatever the outline search could reach from one of those filled gaps — a stroke with
    /// neighbours was found, a stroke standing on its own was not. In Japanese the lone strokes are
    /// the punctuation, which is why a line could come back with every exclamation mark still on it.
    /// Deciding the polarity by each hat's peak instead of its mean was tried and is worse: a single
    /// bright speck then decides the line. Taking both bodies makes the question moot.</para>
    ///
    /// <para>What the body leaves is the fade — an outline, a shadow, or plain antialiasing, all of
    /// them a ramp from the text down to the picture, with no single threshold that separates the
    /// bottom of the ramp from the picture itself. High enough not to eat the scene is high enough to
    /// leave the last pixel or two of every stroke, which is the dotted contour a reader sees tracing
    /// erased words. So the body is the seed of a hysteresis: it is followed outward while the
    /// response stays above <see cref="OutlineTail"/> and no further than
    /// <see cref="OutlineGrowth"/>, which is the text's own fade wherever it happens to be, rather
    /// than a band of fixed width around every glyph. <see cref="OutlineTail"/> is low enough to be a
    /// genuine second threshold and high enough that film grain is not a path to walk along; swept at
    /// 6, 10, 14 and 18 it barely moves what is erased while the masked share of the frame falls
    /// steadily as it rises.</para>
    ///
    /// <para>The band is what this replaced, and that is the difference a reader sees. Of the pixels
    /// around a glyph that the source has darker than the scene — which is what an outline is — the
    /// share still darker after the repair, against the share of the frame the repair visibly changes
    /// where no text or outline ever was:</para>
    ///
    /// <code>
    ///   corpus                left behind, band -> fade    picture disturbed, band -> fade
    ///   chat-room (panel)            36.5%      28.9%            1.11%      1.14%
    ///   ja-game (dialogue)           38.2%      17.6%            0.12%      0.05%
    ///   ja-card                      68.6%      70.8%            0.22%      0.08%
    ///   ja-video                      9.9%       8.2%            0.63%      0.38%
    ///   en                           14.2%      18.0%            0.52%      0.39%
    /// </code>
    ///
    /// <para>Mask islands holding no text at all — a smudge over clean picture, which is what a band
    /// around a Latin box produces, since the box stands well clear of its glyphs and the band fills
    /// with whatever was in there — fall from 1.0 to 0.2 per frame on the Latin corpus and from 1.4
    /// to 0.5 on the game corpus. The card corpus moves a quarter of its tiles back from the
    /// half-resolution repair to the full-resolution one, because it is a thick hole that routes a
    /// tile there. What it costs is the two corpora above where more is left behind: the card corpus,
    /// whose subtitles already sit on a dark band, and Latin text, whose outline is thicker than the
    /// fade this follows. Both were judged the better trade against a picture the erase leaves
    /// alone.</para>
    /// </remarks>
    public static Mat Build(Mat source, IReadOnlyList<CpuTextRegion> lines)
    {
        var mask = new Mat(source.Size(), MatType.CV_8UC1, Scalar.Black);
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
            using var one = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            foreach (var line in lines)
            {
                if (!double.IsFinite(line.X + line.Y + line.Width + line.Height) || line.Width <= 0 || line.Height <= 0) continue;
                double height = Math.Max(1, Math.Min(line.GlyphHeight ?? line.Height, line.Height));
                int padding = Math.Clamp((int)Math.Ceiling(height * BoxPadding), 2, 4);
                var box = Clip(line.X - padding, line.Y - padding, line.X + line.Width + padding,
                    line.Y + line.Height + padding, source.Size());
                if (box.Width == 0 || box.Height == 0) continue;
                using var roi = new Mat(gray, box);
                using var light = new Mat();
                using var dark = new Mat();
                int size = Math.Clamp((int)Math.Round(height * .55) | 1, 5, 25);
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(size, size));
                Cv2.MorphologyEx(roi, light, MorphTypes.TopHat, kernel);
                Cv2.MorphologyEx(roi, dark, MorphTypes.BlackHat, kernel);
                using var seed = new Mat();
                using var darker = new Mat();
                Body(light, seed);   // What stands out brighter than its surroundings,
                Body(dark, darker);  // and what stands out darker. Either one can be the text.
                Cv2.BitwiseOr(seed, darker, seed);
                using var strongest = new Mat();
                Cv2.Max(light, dark, strongest);
                GrowAlongTail(seed, strongest, one);
                Cv2.Dilate(seed, seed, one); // The antialiased end of whatever the growth stopped on.
                using var target = new Mat(mask, box);
                Cv2.BitwiseOr(target, seed, target);
            }
            // Expand by two further pixels to cover faint halos. Applied after merging the boxes so
            // the expansion is not clipped at the edge of one.
            //
            // Two rather than one. One was tried when the growth was new, on the reasoning that the
            // growth reaches the fade where it actually is while a blind margin pays for every glyph
            // everywhere, and a reader reported that version as colour left along the edge of erased
            // words. It measures the same way here: dropping the second pixel leaves more behind on
            // every corpus — 32.6% against 28.9% on the panel corpus, 22.3% against 17.6% on the
            // game corpus, 78.4% against 70.8% on the card corpus — to disturb between a tenth and a
            // quarter of a point less of the picture.
            using var antialias = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            Cv2.Dilate(mask, mask, antialias, iterations: 2);
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }

    /// <summary>
    /// One response's body: Otsu, then the same threshold again with a floor under it, so a response
    /// with nothing in it cannot be split into one anyway.
    /// </summary>
    /// <remarks>
    /// A method rather than two copies of four lines, because the two hats have to be thresholded
    /// identically by construction: the union of the two is the mask's seed, and a difference between
    /// them would be read as a difference in the picture.
    /// </remarks>
    private static void Body(Mat response, Mat into)
    {
        double otsu = Cv2.Threshold(response, into, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        Cv2.Threshold(response, into, Math.Max(18, otsu * .8), 255, ThresholdTypes.Binary);
    }

    /// <summary>Extends the seed along the text's own fade — see <see cref="Build"/>.</summary>
    /// <remarks>
    /// A geodesic dilation: grow by a pixel, keep only what the tail threshold allows, put the seed
    /// back so a step can never lose ground. Both mats are the recognition box, not the frame, and
    /// the loop runs a fixed number of times rather than to stability — the point is a bounded reach,
    /// and a run to stability would follow a scene edge for as far as that edge happens to be dark.
    /// </remarks>
    private static void GrowAlongTail(Mat seed, Mat response, Mat one)
    {
        using var tail = new Mat();
        Cv2.Threshold(response, tail, OutlineTail, 255, ThresholdTypes.Binary);
        using var grown = new Mat();
        for (int step = 0; step < OutlineGrowth; step++)
        {
            Cv2.Dilate(seed, grown, one);
            Cv2.BitwiseAnd(grown, tail, grown);
            Cv2.BitwiseOr(grown, seed, seed);
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
