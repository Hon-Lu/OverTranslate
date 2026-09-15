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
                int reach = Math.Clamp((int)Math.Ceiling(height * .09), 2, 4);
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
                // The text body generally carries more contrast energy than its outline. Choosing
                // one polarity avoids treating both every bright edge and every dark edge as text.
                bool brightText = Cv2.Mean(light).Val0 >= Cv2.Mean(dark).Val0;
                var body = brightText ? light : dark;
                var opposite = brightText ? dark : light;
                double threshold = Cv2.Threshold(body, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                Cv2.Threshold(body, binary, Math.Max(18, threshold * .8), 255, ThresholdTypes.Binary);
                using var fringe = new Mat();
                using var near = new Mat();
                using var fringeContrast = new Mat();
                using var one = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
                using var expansion = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(reach * 2 + 1, reach * 2 + 1));
                Cv2.Dilate(binary, near, expansion);
                Cv2.Threshold(opposite, fringeContrast, 12, 255, ThresholdTypes.Binary);
                Cv2.BitwiseAnd(near, fringeContrast, fringe);
                Cv2.Dilate(fringe, fringe, one); // Include the antialiased edge of a detected outline.
                Cv2.Dilate(binary, binary, one); // One pixel for anti-aliasing, not a whole line band.
                using var coreTarget = new Mat(core, box);
                using var outlineTarget = new Mat(outline, box);
                Cv2.BitwiseOr(coreTarget, binary, coreTarget);
                Cv2.BitwiseOr(outlineTarget, fringe, outlineTarget);
            }
            Cv2.BitwiseOr(core, outline, combined);
            using var antialias = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            // Expand the detected glyph/outline by one additional pixel to cover faint halos.
            // Apply after merging ROIs so the expansion is not clipped at an OCR box edge.
            Cv2.Dilate(combined, combined, antialias, iterations: 2);
            return new(core, outline, combined);
        }
        catch { core.Dispose(); outline.Dispose(); combined.Dispose(); throw; }
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
