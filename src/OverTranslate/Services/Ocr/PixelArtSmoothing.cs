using SkiaSharp;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Finds a picture that is an integer nearest-neighbour enlargement — a retro game shown at 2x,
/// 3x or 4x — and redraws it with smooth interpolation at a fixed number of pixels per block, for
/// the text to be detected and recognised on.
/// </summary>
/// <remarks>
/// <para>The detector is not shift-invariant on pixel art. Its convolutions step by 2 to 32 pixels,
/// and a picture made of hard-edged 4x4 blocks has most of its energy at exactly the block
/// frequency, so where the block grid falls against the network's own grid decides what it sees.
/// Found on a Pokemon Yellow battle screen at 4x (640x576): framed exactly it read ten lines,
/// framed 2px inside (636x572) it read PI, N, AR and EMBER. The detector input size was the same
/// both times — under <see cref="Realtime.RealtimeDetectorSize.DownscaleMinSide"/> the region is
/// read at native size, and the stride alignment rounds 636 and 640 to the same 656 — so nothing
/// but the 2px shift changed. Cutting 0–8px off the left and top in every combination, the
/// characters read went 54 at most and 7 at the worst, and shifts of a multiple of 4 always read
/// well.</para>
///
/// <para>Not a Game Boy oddity. Over 61 pixel-art screens (the screen-rpg corpora at 2x, 3x and
/// 4x, and the Stardew Valley and VA-11 Hall-A grabs of the web corpora), cut at the 16 offsets
/// 0–3px in both axes, 95 of the 976 readings came out at under half of what that screen reads
/// at its best, and several 3x screens read almost nothing at the offset the corpus happens to
/// hold: a Mother 1+2 battle as "a d a m", a Golden Sun battle as "Fight". Aligning the block grid
/// to the origin does not fix it — at 3x no offset lines up with a stride of 32, and those two
/// screens read badly at offset 0, which IS aligned.</para>
///
/// <para>What does fix it is taking the hard steps away, so there is no block frequency left for
/// the strides to alias with: shrink each block to the one pixel it stands for, then enlarge that
/// back with bilinear interpolation. The redraw is then the same picture wherever the user's box
/// starts, up to one sixteenth of Skia's interpolation step at 3x. Over the same 976 readings,
/// those under half of the best went from 95 to 7, and the characters read at the corpus's own
/// offset from 3927 to 4342. The two Mother and Golden Sun screens read their party's names, and
/// the Dragon Warrior III dialogue that came back as single words reads as its four lines.</para>
///
/// <para>The redraw first kept the captured size, so a game at 2x was still detected at 2 pixels a
/// block and one at 6x at 6, and only the detector saw it; recognition cropped from the pixels as
/// captured. That left Japanese reading badly, and differently by window size: a pixel font's
/// kana and kanji fill their 8–12 pixel cell edge to edge with a pixel or two between lines, and
/// at 2 or 3 pixels a block the detector joins a menu's stacked lines into one box (ドグ, マグ and
/// ラグ came back as one glyph, スプラッシャー vanished under フラッドウォー) or cuts a line into
/// single glyphs. A picture is therefore read at <see cref="ReadingBlock"/> pixels per block
/// whatever it was captured at — enlarged from a small window, reduced from a large one — and
/// recognition crops from that same redraw, so the two need no mapping between them and the
/// reading no longer depends on the window's size either. Recognising the redraw rather than the
/// captured pixels costs some spaces in English ("used EMBER" as "usedEMBER"), as it did when it
/// was first measured, and recognising the captured pixels at the redraw's boxes read 545 rather
/// than 564 of the 711 Japanese readings in <see cref="ReadingBlock"/>'s table.</para>
///
/// <para>What is left out on purpose: columns of vertical text (the comic path, measured on other
/// grounds and never pixel art in the corpora) and any picture without a single clean grid on both
/// axes — a game drawn with a smoothing filter, a window with its frame around the game, a capture
/// at a fractional scale. Those read as they did before.</para>
/// </remarks>
internal static class PixelArtSmoothing
{
    /// <summary>The largest enlargement looked for; a 1080p Game Boy at 7x is still inside it.</summary>
    internal const int MaxBlock = 8;

    // Lines sampled per axis when looking for the grid: a fixed cost whatever the region's size,
    // and still hundreds of steps on any game screen.
    private const int SampledLines = 64;

    // A difference between neighbouring pixels counted as a step; below it, a gradient or JPEG noise.
    private const int StepThreshold = 24;

    // Nearest-neighbour enlargement puts every step on the grid; 0.95 leaves room for a JPEG of one
    // (the Stardew Valley grab is a JPEG and is found at 2x) and none for real text.
    private const double MinShare = 0.95;
    private const int MinSteps = 64;

    // A flat picture with a couple of edges folds onto one residue of every block size.
    private const int MinDistinctEdges = 8;

    internal readonly record struct Grid(int BlockX, int PhaseX, int BlockY, int PhaseY);

    /// <summary>
    /// The block size and where the first whole block starts, on each axis; null unless both axes
    /// are a clean nearest-neighbour enlargement.
    /// </summary>
    internal static Grid? Find(SKBitmap bitmap)
    {
        // Across first: almost nothing that is not pixel art gets past one axis, so the second
        // scan is only paid for the pictures this is for.
        if (FindAxis(bitmap, horizontal: true) is not { } x) return null;
        if (FindAxis(bitmap, horizontal: false) is not { } y) return null;
        return new Grid(x.Block, x.Phase, y.Block, y.Phase);
    }

    /// <summary>
    /// The redraw at the captured size, or null when it is not pixel art and the captured pixels
    /// are the answer. For a look with the detector alone, where the caller has chosen the size to
    /// look at; a read goes through <see cref="ForReading"/>.
    /// </summary>
    internal static SKBitmap? ForDetection(SKBitmap source) =>
        Smooth(source, new SKSamplingOptions(SKFilterMode.Linear));

    internal static SKBitmap? Smooth(SKBitmap source, SKSamplingOptions sampling)
    {
        if (Find(source) is not { } grid) return null;
        return Redraw(source, grid, sampling);
    }

    /// <summary>
    /// Pixels per block the picture is read at, detection and recognition alike, whatever the
    /// block size it was captured at.
    /// </summary>
    /// <remarks>
    /// MEASURED on the Japanese pixel-art screens (screen-rpg-ja: 17 screens, 79 transcribed lines,
    /// each framed as its own windows and the frame moved 0–2px in both axes, 711 readings in all):
    ///
    /// <code>
    ///   pixels per block   lines read right
    ///   as captured (2–3)   499/711  70%
    ///   2                   505/711
    ///   3                   535/711
    ///   4                   564/711  79%
    ///   5                   554/711
    ///   6                   547/711
    /// </code>
    ///
    /// The same screens enlarged twice over first (4x and 6x, as a 1080p window shows them) read
    /// 231/316 as captured and 251/316 at 4 — the same 79%, since at a fixed reading scale the
    /// picture handed over no longer depends on the window's size. Bicubic in place of bilinear
    /// for the redraw read 551.
    ///
    /// <para>The same for realtime translation, which costs more on a small window: the detector's
    /// work grows with the picture's area. MEASURED live (primary monitor, a dialogue box's text
    /// swapped every 3 seconds, 17 passes each), the median recognition time per pass went 129 →
    /// 235ms on a FFIV PSP box at 2x (890x167), 212 → 193ms on a DQ6 box at 3x and 288 → 237ms at
    /// 6x. Reading a 2x window at 3 instead would have cost 186ms and read 530 rather than 556 of
    /// the 711 realtime readings; one scale for both flows was chosen over that.</para>
    /// </remarks>
    internal const int ReadingBlock = 4;

    /// <summary>
    /// The picture to read pixel art from — detection and recognition both — redrawn smooth at
    /// <see cref="ReadingBlock"/> pixels per block, or null when it is not pixel art and the
    /// captured pixels are the answer. A point in it is the point divided by the scale on each
    /// axis in the captured picture.
    /// </summary>
    internal static Reading? ForReading(SKBitmap source)
    {
        if (Find(source) is not { } grid) return null;
        return new Reading(
            RedrawAt(source, grid, ReadingBlock, ReadingBlock, new SKSamplingOptions(SKFilterMode.Linear)),
            (double)ReadingBlock / grid.BlockX,
            (double)ReadingBlock / grid.BlockY);
    }

    internal sealed record Reading(SKBitmap Bitmap, double ScaleX, double ScaleY) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }

    // One pixel per block, sampled in the block's middle, enlarged back over the same area. A block
    // the frame cut through is completed rather than dropped, so the edge keeps its pixels.
    internal static SKBitmap Redraw(SKBitmap source, Grid grid, SKSamplingOptions sampling) =>
        RedrawAt(source, grid, grid.BlockX, grid.BlockY, sampling);

    // The same at perX by perY pixels per block: the captured picture scaled by perX / BlockX
    // across and perY / BlockY down, with no offset, so its own pixels need no other mapping.
    internal static SKBitmap RedrawAt(SKBitmap source, Grid grid, int perX, int perY, SKSamplingOptions sampling)
    {
        int w = source.Width, h = source.Height;
        // Blocks start at the phase; a partial block before it is completed by padding.
        int dx = (grid.BlockX - grid.PhaseX) % grid.BlockX;
        int dy = (grid.BlockY - grid.PhaseY) % grid.BlockY;
        int nw = (w + dx + grid.BlockX - 1) / grid.BlockX;
        int nh = (h + dy + grid.BlockY - 1) / grid.BlockY;

        using var native = new SKBitmap(nw, nh, source.ColorType, source.AlphaType);
        var from = source.GetPixelSpan();
        var to = native.GetPixelSpan();
        int fromRow = source.RowBytes, toRow = native.RowBytes;
        for (int j = 0; j < nh; j++)
        {
            int sy = Math.Clamp(j * grid.BlockY - dy + grid.BlockY / 2, 0, h - 1);
            for (int i = 0; i < nw; i++)
            {
                int sx = Math.Clamp(i * grid.BlockX - dx + grid.BlockX / 2, 0, w - 1);
                from.Slice(sy * fromRow + sx * 4, 4).CopyTo(to.Slice(j * toRow + i * 4, 4));
            }
        }

        // In blocks of the target size: the captured picture's left edge is dx / BlockX of a block
        // into the first one.
        var smooth = new SKBitmap(
            (w * perX + grid.BlockX - 1) / grid.BlockX,
            (h * perY + grid.BlockY - 1) / grid.BlockY,
            source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(smooth);
        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(native);
        float left = -(float)dx * perX / grid.BlockX, top = -(float)dy * perY / grid.BlockY;
        canvas.DrawImage(
            image,
            new SKRect(left, top, left + nw * perX, top + nh * perY),
            sampling);
        return smooth;
    }

    private static (int Block, int Phase)? FindAxis(SKBitmap bitmap, bool horizontal)
    {
        int length = horizontal ? bitmap.Width : bitmap.Height;
        int across = horizontal ? bitmap.Height : bitmap.Width;
        if (length < 2 * MaxBlock || across < 1) return null;

        var pixels = bitmap.GetPixelSpan();
        int rowBytes = bitmap.RowBytes;
        int bpp = bitmap.BytesPerPixel;
        if (bpp != 4) return null;

        var counts = new int[length];
        long total = 0;
        int lines = Math.Min(SampledLines, across);
        for (int l = 0; l < lines; l++)
        {
            int a = (int)((l + 0.5) * across / lines);
            for (int b = 1; b < length; b++)
            {
                int o0 = horizontal ? a * rowBytes + (b - 1) * 4 : (b - 1) * rowBytes + a * 4;
                int o1 = horizontal ? o0 + 4 : o0 + rowBytes;
                int d = Math.Max(
                    Math.Abs(pixels[o0] - pixels[o1]),
                    Math.Max(Math.Abs(pixels[o0 + 1] - pixels[o1 + 1]), Math.Abs(pixels[o0 + 2] - pixels[o1 + 2])));
                if (d > StepThreshold)
                {
                    counts[b]++;
                    total++;
                }
            }
        }

        if (total < MinSteps) return null;

        (int Block, int Phase)? best = null;
        Span<long> bins = stackalloc long[MaxBlock];
        Span<int> distinct = stackalloc int[MaxBlock];
        for (int k = 2; k <= MaxBlock; k++)
        {
            bins[..k].Clear();
            distinct[..k].Clear();
            for (int b = 0; b < length; b++)
            {
                if (counts[b] == 0) continue;
                bins[b % k] += counts[b];
                distinct[b % k]++;
            }

            int phase = 0;
            for (int p = 1; p < k; p++)
                if (bins[p] > bins[phase]) phase = p;

            if (bins[phase] >= MinShare * total && distinct[phase] >= MinDistinctEdges)
                best = (k, phase);
        }

        return best;
    }
}
