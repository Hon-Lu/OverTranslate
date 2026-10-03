using System.Drawing;
using System.Drawing.Imaging;
using SkiaSharp;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// Which of the manga models' column blocks are tilted, and by how much: the blocks are boxed
/// upright, so the angle has to be found in the picture.
/// </summary>
/// <remarks>
/// <para>Two sources, and both have to agree before a block is drawn tilted
/// (<see cref="InkAgreement"/>) — calling a straight column tilted is the worst way this can go
/// wrong, see <see cref="TiltedColumns"/>.</para>
///
/// <para>The first is the ink's own angle (<see cref="InkAngle"/>): cheap, and it finds 16 of the 17
/// blocks the column detector calls tilted over the vertical corpus and vertical-image-ja4. On its
/// own it is not to be trusted — over 1636 column blocks it put 207 past 3°, most of them straight —
/// so it only says which blocks are worth a second look.</para>
///
/// <para>The second is the column detector's quadrilaterals, which is what the column pipeline
/// judges by. Run on each candidate's crop it costs 25–40ms a block; its cost goes with the area it
/// is handed (about 0.3µs a pixel), so the candidates are cut out of the grey page, reduced to
/// <see cref="Scale"/> and set side by side in one picture, and the detector runs once over that
/// (<see cref="MaxArea"/> bounds it, which is what holds the step to 15ms a page). A reduced column's fit comes out at barely two to one, so the
/// quadrilaterals are held to <see cref="TrustedLength"/> rather than the column pipeline's two.</para>
///
/// <para>MEASURED (Release, CPU) over the 171 vertical pages and the five of ja4, this step alone:
/// 5.0ms a page at the median, 9.2 at the 90th percentile, 11.2 at worst; no block outside ja4
/// called tilted, straight or not; 8 of the 18 tilted blocks of ja4 found. The cap is what costs those: at 0.35 with no cap the same rule found
/// 11, and took 15–29ms on those pages.</para>
/// </remarks>
internal static class MangaColumnAngles
{
    /// <summary>How far from vertical the ink has to lean for a block to be looked at again.</summary>
    internal const double InkFromDegrees = 3;

    /// <summary>How far apart the two sources may be on one block.</summary>
    internal const double InkAgreement = 2;

    /// <summary>The reduction the candidates are detected at.</summary>
    internal const double Scale = 0.35;

    /// <summary>The furthest the reduction goes to stay under <see cref="MaxArea"/>.</summary>
    internal const double MinScale = 0.27;

    /// <summary>The most pixels handed to the detector for one page: about 9ms of detection.</summary>
    internal const double MaxArea = 22_000;

    /// <summary>How long a reduced column's quadrilateral has to be, against its thickness.</summary>
    internal const double TrustedLength = 2.5;

    private const double InkRange = 20;
    private const int InkPoints = 500;
    private const int Gap = 12;

    /// <summary>
    /// <paramref name="groups"/> with <see cref="OcrTextBlock.Tilt"/> set on those made wholly of
    /// tilted column blocks; the same list when there are none.
    /// </summary>
    /// <param name="groups">What <see cref="MangaPageLayout.Assemble"/> made of <paramref name="blocks"/>.</param>
    /// <param name="detect">The column detector over a bitmap: its quadrilaterals, nothing read.</param>
    internal static List<OcrTextBlock> Apply(
        List<OcrTextBlock> groups, IReadOnlyList<MangaBlock> blocks, LumaPage luma,
        Func<Bitmap, IReadOnlyList<SKPointI[]>> detect)
    {
        var candidates = new List<(int Block, double Ink)>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var bounds = blocks[i].Bounds;
            if (MangaPageLayout.RunsAcross(bounds) || MangaPageLayout.IsLong(bounds)) continue;
            double ink = InkAngle(luma, bounds);
            if (!double.IsNaN(ink) && Math.Abs(ink) >= InkFromDegrees) candidates.Add((i, ink));
        }
        if (candidates.Count == 0) return groups;

        var angles = Measure(candidates, blocks, luma, detect);
        if (angles.Count == 0) return groups;

        List<OcrTextBlock>? result = null;
        for (int g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            if (group.RunsAcross || group.Tilt is not null) continue;
            var members = group.Lines.Select(line => IndexOf(blocks, line)).ToList();
            if (members.Count == 0 || members.Any(member => member < 0 || !angles.ContainsKey(member))) continue;

            var found = members.Select(member => angles[member]).ToList();
            var degrees = found.Select(entry => entry.Degrees).ToList();
            if (degrees.Max() - degrees.Min() > TiltedColumns.MaxSpread) continue;
            double weight = found.Sum(entry => entry.Weight);
            double angle = found.Sum(entry => entry.Degrees * entry.Weight) / weight;
            if (TiltedText.FromColumns(angle, [.. found.SelectMany(entry => entry.Quads)]) is not { } tilt) continue;

            result ??= [.. groups];
            result[g] = group with { Tilt = tilt };
        }
        return result ?? groups;
    }

    private static int IndexOf(IReadOnlyList<MangaBlock> blocks, Rect line)
    {
        for (int i = 0; i < blocks.Count; i++)
            if (Same(blocks[i].Bounds, line)) return i;
        return -1;
    }

    private static bool Same(RectangleF block, Rect line) =>
        Math.Abs(block.X - line.X) < 0.5 && Math.Abs(block.Y - line.Y) < 0.5 &&
        Math.Abs(block.Width - line.Width) < 0.5 && Math.Abs(block.Height - line.Height) < 0.5;

    /// <summary>
    /// The candidates cut out with a margin, reduced, set in one row and detected once; for each
    /// block both sources agree on, its angle, the length its columns weigh, and their quadrilaterals
    /// on the page.
    /// </summary>
    private static Dictionary<int, (double Degrees, double Weight, List<Point[]> Quads)> Measure(
        List<(int Block, double Ink)> candidates, IReadOnlyList<MangaBlock> blocks, LumaPage luma,
        Func<Bitmap, IReadOnlyList<SKPointI[]>> detect)
    {
        var crops = candidates.ToDictionary(c => c.Block, c => Crop(blocks[c.Block].Bounds, luma.Width, luma.Height));

        // The widest the cap allows, and when even the narrowest reduction is too much, the blocks
        // leaning least are left out first: they are the likeliest to be straight.
        var kept = candidates.OrderByDescending(c => Math.Abs(c.Ink)).ToList();
        double scale = Scale;
        while (Area(kept, crops, scale) > MaxArea)
        {
            if (scale > MinScale) scale = Math.Max(MinScale, scale - 0.01);
            else if (kept.Count > 1) kept.RemoveAt(kept.Count - 1);
            else return [];
        }

        var slots = new List<(int Block, double Ink, Rectangle Crop, Rectangle Slot)>();
        int x = Gap, height = 0;
        foreach (var (block, ink) in kept)
        {
            var crop = crops[block];
            int w = Math.Max(1, (int)Math.Round(crop.Width * scale)), h = Math.Max(1, (int)Math.Round(crop.Height * scale));
            slots.Add((block, ink, crop, new Rectangle(x, Gap, w, h)));
            x += w + Gap;
            height = Math.Max(height, h);
        }

        IReadOnlyList<SKPointI[]> quads;
        using (var mosaic = Mosaic(luma, x, height + 2 * Gap, [.. slots.Select(slot => (slot.Crop, slot.Slot))]))
            quads = detect(mosaic);

        var angles = new Dictionary<int, (double, double, List<Point[]>)>();
        foreach (var (block, ink, crop, slot) in slots)
        {
            double sx = (double)crop.Width / slot.Width, sy = (double)crop.Height / slot.Height;
            var lines = new List<OcrLineGeometry>();
            var onPage = new List<Point[]>();
            foreach (var quad in quads)
            {
                double cx = quad.Average(p => p.X), cy = quad.Average(p => p.Y);
                if (cx < slot.Left || cx >= slot.Right || cy < slot.Top || cy >= slot.Bottom) continue;
                if (OcrLineGeometry.FromQuad(quad) is not { } reduced) continue;
                // The angle does not change with an even reduction; the measurements go back to the page's.
                var line = reduced with { Length = reduced.Length * sx, Thickness = reduced.Thickness * sx };
                lines.Add(line);
                var centre = new Point(crop.X + (cx - slot.X) * sx, crop.Y + (cy - slot.Y) * sy);
                onPage.Add(TiltedColumns.Quad(new Rect(centre, centre), line));
            }

            if (TiltedColumns.Angle(lines, TrustedLength) is not { } angle) continue;
            if (Math.Abs(angle - ink) > InkAgreement) continue;
            double weight = lines.Where(line => TiltedColumns.RunsDown(line)).Sum(line => line.Length);
            angles[block] = (angle, Math.Max(1, weight), onPage);
        }
        return angles;
    }

    /// <summary>
    /// The crops in grey on mid-grey, each reduced into its slot by averaging the page pixels that
    /// fall in each mosaic pixel.
    /// </summary>
    /// <remarks>
    /// From the grey page the manga path already holds rather than the capture, so nothing is
    /// converted or drawn through GDI: that cost 2–3ms a page of the 15 this step may take.
    /// </remarks>
    private static Bitmap Mosaic(LumaPage luma, int width, int height, List<(Rectangle Crop, Rectangle Slot)> slots)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 128;
            pixels[i + 3] = 255;
        }

        foreach (var (crop, slot) in slots)
        {
            double sx = (double)crop.Width / slot.Width, sy = (double)crop.Height / slot.Height;
            for (int v = 0; v < slot.Height; v++)
            {
                int top = crop.Y + (int)(v * sy), bottom = Math.Max(top + 1, Math.Min(crop.Bottom, crop.Y + (int)((v + 1) * sy)));
                for (int u = 0; u < slot.Width; u++)
                {
                    int left = crop.X + (int)(u * sx), right = Math.Max(left + 1, Math.Min(crop.Right, crop.X + (int)((u + 1) * sx)));
                    int sum = 0, count = 0;
                    for (int y = top; y < bottom; y++)
                        for (int xx = left; xx < right; xx++) { sum += luma[xx, y]; count++; }
                    byte grey = (byte)(sum / Math.Max(1, count));
                    int at = ((slot.Y + v) * width + slot.X + u) * 4;
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = grey;
                }
            }
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static double Area(List<(int Block, double Ink)> kept, Dictionary<int, Rectangle> crops, double scale)
    {
        if (kept.Count == 0) return 0;
        double width = Gap + kept.Sum(c => Math.Round(crops[c.Block].Width * scale) + Gap);
        double height = kept.Max(c => Math.Round(crops[c.Block].Height * scale)) + 2 * Gap;
        return width * height;
    }

    // The block and a tenth of its shorter side of page round it, so the detector sees its edge.
    private static Rectangle Crop(RectangleF block, int width, int height)
    {
        int pad = Math.Max(6, (int)(Math.Min(block.Width, block.Height) * 0.1));
        return Rectangle.FromLTRB(
            Math.Max(0, (int)block.X - pad), Math.Max(0, (int)block.Y - pad),
            Math.Min(width, (int)Math.Ceiling(block.Right) + pad), Math.Min(height, (int)Math.Ceiling(block.Bottom) + pad));
    }

    /// <summary>
    /// How far the columns in a block lean from vertical by their ink (sign as
    /// <see cref="TiltedColumns.Deviation"/>), or NaN when the block does not say.
    /// </summary>
    /// <remarks>
    /// <para>The ink is the minority side of Otsu's threshold over the block. Its points are
    /// projected across each candidate direction; the direction whose profile is sharpest — columns
    /// and the gaps between them — wins. ±20° in 2.5° steps, then 0.5° and 0.25° round the best.</para>
    ///
    /// <para>A sample of at most <see cref="InkPoints"/> points, one per cell of a grid at a
    /// pseudo-random offset inside its cell. A sample taken on the grid itself projects into exact
    /// bins at 0° and 45° whatever the writing does: that is what had the first version answer 0.0
    /// for every ja4 block, and 45° for half the straight blocks it called tilted. An answer at the
    /// edge of the range is no answer.</para>
    /// </remarks>
    internal static double InkAngle(LumaPage luma, RectangleF block)
    {
        int x0 = Math.Max(0, (int)block.X), y0 = Math.Max(0, (int)block.Y);
        int x1 = Math.Min(luma.Width, (int)Math.Ceiling(block.Right)), y1 = Math.Min(luma.Height, (int)Math.Ceiling(block.Bottom));
        if (x1 - x0 < 8 || y1 - y0 < 8) return double.NaN;

        var histogram = new int[256];
        int stride = Math.Max(1, (int)Math.Sqrt((x1 - x0) * (y1 - y0) / 5000.0));
        int total = 0;
        for (int y = y0; y < y1; y += stride)
            for (int x = x0; x < x1; x += stride) { histogram[luma[x, y]]++; total++; }
        int threshold = Otsu(histogram, total), below = 0;
        for (int k = 0; k <= threshold; k++) below += histogram[k];
        bool dark = below <= total - below;

        int ink = Math.Min(below, total - below) * stride * stride;
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(ink / (double)InkPoints)));
        var xs = new List<float>();
        var ys = new List<float>();
        for (int y = y0; y < y1; y += step)
            for (int x = x0; x < x1; x += step)
            {
                uint hash = (uint)(x * 73856093) ^ (uint)(y * 19349663);
                hash ^= hash >> 13; hash *= 0x5bd1e995; hash ^= hash >> 15;
                int px = x + (int)(hash % (uint)step), py = y + (int)((hash >> 8) % (uint)step);
                if (px >= x1 || py >= y1) continue;
                var value = luma[px, py];
                if (dark ? value <= threshold : value > threshold) { xs.Add(px - x0); ys.Add(py - y0); }
            }
        if (xs.Count < 20) return double.NaN;

        double bin = step;
        int bins = (int)(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / bin) + 2;
        double middle = bins / 2;
        var profile = new int[bins];
        var across = xs.ToArray();
        var down = ys.ToArray();
        double best = 0;
        long bestScore = -1;
        void Search(double from, double to, double by)
        {
            for (double degrees = from; degrees <= to + 1e-9; degrees += by)
            {
                double radians = degrees * Math.PI / 180;
                double nx = Math.Cos(radians) / bin, ny = Math.Sin(radians) / bin;
                Array.Clear(profile);
                for (int k = 0; k < across.Length; k++)
                {
                    int at = (int)(across[k] * nx + down[k] * ny + middle);
                    if ((uint)at < (uint)bins) profile[at]++;
                }
                long score = 0;
                foreach (var count in profile) score += (long)count * count;
                if (score > bestScore) { bestScore = score; best = degrees; }
            }
        }

        Search(-InkRange, InkRange, 2.5);
        double coarse = best;
        Search(coarse - 2.5, coarse + 2.5, 0.5);
        double fine = best;
        Search(fine - 0.5, fine + 0.5, 0.25);
        return Math.Abs(best) >= InkRange - 0.5 ? double.NaN : best;
    }

    private static int Otsu(int[] histogram, int total)
    {
        double sum = 0;
        for (int i = 0; i < 256; i++) sum += i * histogram[i];
        double sumBelow = 0, best = -1;
        int weightBelow = 0, threshold = 127;
        for (int i = 0; i < 256; i++)
        {
            weightBelow += histogram[i];
            if (weightBelow == 0) continue;
            int weightAbove = total - weightBelow;
            if (weightAbove == 0) break;
            sumBelow += i * histogram[i];
            double meanBelow = sumBelow / weightBelow, meanAbove = (sum - sumBelow) / weightAbove;
            double between = (double)weightBelow * weightAbove * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best) { best = between; threshold = i; }
        }
        return threshold;
    }
}
