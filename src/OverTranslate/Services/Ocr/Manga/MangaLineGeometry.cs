using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SkiaSharp;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>Which way a block's lines run, by the column detector's own lines.</summary>
internal enum MangaLineDirection
{
    /// <summary>Not asked, or nothing in the block long enough to say: the box's shape decides.</summary>
    Unsure,
    Across,
    Down,
}

/// <summary>
/// Which way the writing in each block runs, from the lines the column detector finds in it — asked
/// before anything is read, so each block can go to the reader that can read it.
/// </summary>
/// <remarks>
/// <para>The manga detector draws a box round a block and says nothing of its lines, and the manga
/// path went by the box's shape (<see cref="MangaPageLayout.AcrossRatio"/>). That was wrong for 47
/// of the 1742 blocks of the vertical and comic corpora that could be told: 44 rows taken for
/// columns — English balloons, a caption of nine lines across in a box 1.30 times as wide as tall,
/// page numbers, titles — and three blocks of columns taken for rows, among them vertical-image-ja5's
/// balloon of sixteen columns, which the detector boxes 2.48–2.88 times as wide as tall depending on
/// the framing, either side of the 2.5 that makes a row. That one is the overlay that came out one
/// line across in some frames and in columns in others.</para>
///
/// <para>Each line the detector finds has a length along itself and a thickness across it, and a
/// line within 45° of level runs across. The block goes the way most of the length of its lines
/// goes. Only the sign is used: the lines of a block almost always all agree, and where they do not
/// the box's shape is not better placed to say. A line not <see cref="LongLine"/> times as long as
/// it is thick is one or two characters and says nothing either way; a block with none keeps its
/// shape's answer.</para>
///
/// <para>Two things make the detector's lines trustworthy, and both were measured as necessary.
/// <b>All the blocks are detected together</b>, cut out and set side by side on white
/// (<see cref="Mosaic"/>): detected one block at a time, the same detection turned 7 blocks of
/// columns into rows, and 0 this way. <b>Every block is detected at the same size of line</b>
/// (<see cref="LineThickness"/>): at one fixed reduction the size of the type follows the size of
/// the page, and on a page reduced to 0.75 the reduction that was right at 1.0 put 8 blocks of
/// columns at 11–15px a line, where the detector joins neighbouring short columns into one box
/// across. Those were not close calls the sign could have been spared by a margin — every one of
/// them scored all across — so the remedy is the size, not a bar on the score. A first, coarse
/// detection (<see cref="FirstPassHeight"/>) measures each block's lines; the second sets each
/// block at its own scale.</para>
///
/// <para>Only blocks <see cref="WideFrom"/> are asked. Every block wrongly called columns by its
/// shape but two very small ones is at least 1.3 times as wide as tall; and of the 124 blocks
/// between 0.8 and 1.25 times as wide as tall, the lines and the shape agree on every one, so a
/// block that drifts across this bar from frame to frame gets the same answer either side of it.
/// It also keeps the cost to the 13% of the page's text area that is in wide blocks.</para>
/// </remarks>
// MEASURED (scratchpad r4, findings-4/5) over the 1753 manga blocks of the vertical and comic
// corpora at page scales 0.5, 0.65, 0.75, 0.9, 1.0, 1.1, 1.25 and 1.5: no block the shape had right
// was turned at any scale, and 38–43 of the 47 it had wrong were put right (the rest are a page
// number, two titles and three blocks of a few small characters, which keep the shape's answer as
// before). Over 12 pages framed 55 ways each — the origin moved up to 10px and the scale 0.9–1.1 —
// no block changed direction from one framing to another. The second detection's lines came out
// 22–24px thick at the median at every page scale (p10 18–21, p90 28–34). On the 79 of 201 pages
// with a wide block it cost 30ms at the median and 82 at the 90th percentile, on the CPU.
internal static class MangaLineGeometry
{
    /// <summary>
    /// A block is asked when it is at least this many times as wide as it is tall; a narrower one
    /// keeps the direction of its shape.
    /// </summary>
    internal const double WideFrom = 1;

    /// <summary>
    /// How much longer than thick a line has to be to say which way it runs. The column pipeline's
    /// own bar for the same question (<see cref="VerticalColumnGrouping.AcrossAlongItself"/>).
    /// </summary>
    internal const double LongLine = VerticalColumnGrouping.AcrossAlongItself;

    /// <summary>The thickness, in pixels, every block's lines are set at for the deciding detection.</summary>
    /// <remarks>
    /// MEASURED across the eight page scales: at 16px a block was turned wrongly at 0.5 and at 0.75,
    /// at 24px one at 1.5, at 20px none at any — so about 18–22px is the safe band, and 20 sits in
    /// it with roughly a fifth to spare either side. The reduction that failed at 0.75 put lines at
    /// 11–15px, a quarter to a half below this.
    /// </remarks>
    internal const double LineThickness = 20;

    /// <summary>The first detection, which only measures, sees each block no taller than this.</summary>
    /// <remarks>
    /// Holds its cost to the number of blocks rather than the size of the page. A block taller than
    /// this is reduced, and its lines may come out joined two or three together: the thickness it
    /// reports is then too large and the second detection a little small, which the 18–22px band
    /// absorbs — at 128 none of the eight scales turned a block wrongly.
    /// </remarks>
    internal const double FirstPassHeight = 128;

    // How far one block's scale may go, so that a line measured absurdly thin or thick cannot blow
    // a block up or shrink it to nothing.
    private const double SmallestScale = 0.1;
    private const double LargestScale = 3;

    private const int Gap = 16;
    private const int MosaicWidth = 1600;

    /// <summary>
    /// The direction of each block, in the order given: <see cref="MangaLineDirection.Unsure"/> for
    /// one that was not asked or had no line long enough to say.
    /// </summary>
    /// <param name="page">The page the blocks are on, in its own coordinates.</param>
    /// <param name="detect">The column detector over a bitmap: its quadrilaterals, nothing read.</param>
    internal static MangaLineDirection[] Directions(
        Bitmap page, IReadOnlyList<RectangleF> blocks, Func<Bitmap, IReadOnlyList<SKPointI[]>> detect)
    {
        var directions = new MangaLineDirection[blocks.Count];
        var crops = blocks.Select(block => Crop(block, page.Width, page.Height)).ToArray();
        var asked = blocks.Select((block, i) =>
            block.Width >= WideFrom * block.Height && crops[i].Width >= 2 && crops[i].Height >= 2).ToArray();
        if (!asked.Any(a => a)) return directions;

        var first = asked.Select((a, i) => a ? Math.Min(1.0, FirstPassHeight / blocks[i].Height) : 0).ToArray();
        var thickness = Lines(page, crops, first, detect)
            .Select((lines, i) => Thickness(lines, first[i]))
            .ToArray();

        var second = thickness
            .Select((t, i) => asked[i] && t > 0 ? Math.Clamp(LineThickness / t, SmallestScale, LargestScale) : 0)
            .ToArray();
        var found = Lines(page, crops, second, detect);
        for (int i = 0; i < blocks.Count; i++)
            directions[i] = Vote(found[i], second[i]);
        return directions;
    }

    /// <summary>The way most of the length of a block's long lines runs.</summary>
    /// <param name="scale">What the block was scaled by when they were found; 0 when it was not.</param>
    internal static MangaLineDirection Vote(IEnumerable<(double Length, double Thickness, bool Level)> lines, double scale)
    {
        double across = 0, down = 0;
        foreach (var (length, thickness, level) in lines)
        {
            if (!Counts(length, thickness, scale)) continue;
            if (level) across += length;
            else down += length;
        }

        return across == down ? MangaLineDirection.Unsure
            : across > down ? MangaLineDirection.Across
            : MangaLineDirection.Down;
    }

    // Long enough to say which way it runs, and at least two pixels thick where it was found.
    private static bool Counts(double length, double thickness, double scale) =>
        thickness * scale >= 2 && length >= LongLine * thickness;

    // The thickness of a block's lines in page pixels: the median of the long ones, or of all of
    // them when none is long; 0 when none was found.
    private static double Thickness(List<(double Length, double Thickness, bool Level)> lines, double scale)
    {
        if (lines.Count == 0) return 0;
        var chosen = lines.Where(line => Counts(line.Length, line.Thickness, scale)).ToList();
        var sorted = (chosen.Count > 0 ? chosen : lines).Select(line => line.Thickness).Order().ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// Each block's lines, in page pixels: the blocks at their scales (0 leaves one out) set side by
    /// side, detected once, and every line given to the block its centre falls in.
    /// </summary>
    private static List<(double Length, double Thickness, bool Level)>[] Lines(
        Bitmap page, Rectangle[] crops, double[] scales, Func<Bitmap, IReadOnlyList<SKPointI[]>> detect)
    {
        var lines = crops.Select(_ => new List<(double, double, bool)>()).ToArray();
        var slots = Layout(crops, scales, out int width, out int height);
        if (slots.Count == 0) return lines;

        IReadOnlyList<SKPointI[]> quads;
        using (var mosaic = Mosaic(page, width, height, slots))
            quads = detect(mosaic);

        foreach (var quad in quads)
        {
            if (quad.Length < 4) continue;
            double cx = quad.Average(p => p.X), cy = quad.Average(p => p.Y);
            int k = slots.FindIndex(slot => slot.Slot.Contains((int)cx, (int)cy));
            if (k < 0) continue;
            var (length, thickness, level) = Measure(quad);
            double scale = scales[slots[k].Block];
            lines[slots[k].Block].Add((length / scale, thickness / scale, level));
        }
        return lines;
    }

    /// <summary>
    /// A quadrilateral's length along itself, its thickness across, and whether its length runs
    /// within 45° of level.
    /// </summary>
    internal static (double Length, double Thickness, bool Level) Measure(SKPointI[] quad)
    {
        double a = Distance(quad[0], quad[1]), b = Distance(quad[1], quad[2]);
        bool firstLevel = Math.Abs(quad[1].X - quad[0].X) >= Math.Abs(quad[1].Y - quad[0].Y);
        return a >= b ? (a, b, firstLevel) : (b, a, !firstLevel);

        static double Distance(SKPointI p, SKPointI q) => Math.Sqrt((double)(p.X - q.X) * (p.X - q.X) + (double)(p.Y - q.Y) * (p.Y - q.Y));
    }

    private static List<(int Block, Rectangle Crop, Rectangle Slot)> Layout(
        Rectangle[] crops, double[] scales, out int width, out int height)
    {
        var slots = new List<(int, Rectangle, Rectangle)>();
        int x = Gap, y = Gap, row = 0;
        width = 0;
        for (int i = 0; i < crops.Length; i++)
        {
            if (scales[i] <= 0) continue;
            int w = Math.Max(2, (int)Math.Round(crops[i].Width * scales[i]));
            int h = Math.Max(2, (int)Math.Round(crops[i].Height * scales[i]));
            if (x + w + Gap > MosaicWidth && x > Gap)
            {
                x = Gap;
                y += row + Gap;
                row = 0;
            }

            slots.Add((i, crops[i], new Rectangle(x, y, w, h)));
            x += w + Gap;
            row = Math.Max(row, h);
            width = Math.Max(width, x);
        }

        height = y + row + Gap;
        return slots;
    }

    private static Bitmap Mosaic(Bitmap page, int width, int height, List<(int Block, Rectangle Crop, Rectangle Slot)> slots)
    {
        var mosaic = new Bitmap(Math.Max(width, 32), Math.Max(height, 32), PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(mosaic);
        graphics.Clear(Color.White);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        foreach (var (_, crop, slot) in slots)
            graphics.DrawImage(page, slot, crop, GraphicsUnit.Pixel);
        return mosaic;
    }

    // The block with the margin the column pipeline is handed it with, inside the page; empty when
    // the block lies outside it.
    private static Rectangle Crop(RectangleF block, int pageWidth, int pageHeight)
    {
        if (!(block.Width > 0 && block.Height > 0) || float.IsNaN(block.X) || float.IsNaN(block.Y)) return Rectangle.Empty;
        var crop = MangaPageLayout.LongBlockCrop(block, pageWidth, pageHeight);
        return crop.Width > 0 && crop.Height > 0 ? crop : Rectangle.Empty;
    }
}
