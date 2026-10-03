using System.Drawing;
using System.Windows;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// What the manga models' blocks become before they are translated: which ones are handed to the
/// column pipeline instead, which ones are rows, and which ones are one sentence spread over several
/// bubbles.
/// </summary>
internal static class MangaPageLayout
{
    /// <summary>
    /// A block this many times longer than it is wide (either way round) is read by the column
    /// pipeline rather than by manga-ocr.
    /// </summary>
    /// <remarks>
    /// manga-ocr squeezes every block into 224×224. A recap box of two small columns 79×738 is shrunk
    /// seven times along its length and comes back as a sentence that is not on the page; a single
    /// 1098px column loses characters. The three blocks that went wrong in the corpora were 9.3, 15.4
    /// and 15.9; the longest balloon manga-ocr read correctly was 5.9 (ja2 マントの紋章で…). 6, 7 and
    /// 8 measured the same; 4 sent six good ja2 balloons away and lost two sentences. A ratio does not
    /// change with the resolution of the capture, which a length in pixels would. The wide case
    /// catches the ja3 page-top title, which the column pipeline reads and manga-ocr does not.
    /// </remarks>
    internal const double LongBlockRatio = 7;

    /// <summary>
    /// A block this many times wider than it is tall is a line of writing across the page: a name
    /// plate, a caption. It is drawn as a row and is never part of a bubble group.
    /// </summary>
    /// <remarks>
    /// 1.2 caught squat balloons of several short columns too; at 2.5 the name plate that had been
    /// merged into a speech bubble (剣聖 オリヴァー・カーディフ) stays apart and no score moves.
    /// </remarks>
    internal const double AcrossRatio = 2.5;

    // A text block belongs to the smallest bubble that holds at least this much of it.
    private const double BubbleCover = 0.6;

    // Columns of one group sharing more than this much of the narrower one's width are one band.
    private const double BandOverlap = 0.5;

    internal static bool IsLong(RectangleF block) =>
        block.Height >= LongBlockRatio * block.Width || block.Width >= LongBlockRatio * block.Height;

    internal static bool RunsAcross(RectangleF block) => block.Width > AcrossRatio * block.Height;

    /// <summary>
    /// Where a long block is cut out for the column pipeline: the block and 6px of page around it,
    /// so the column pipeline's own detector sees the edge of the writing rather than starting on it.
    /// </summary>
    internal static Rectangle LongBlockCrop(RectangleF block, int pageWidth, int pageHeight)
    {
        const int pad = 6;
        int x = (int)block.X, y = (int)block.Y;
        int right = x + (int)(block.Right - block.X), bottom = y + (int)(block.Bottom - block.Y);
        return Rectangle.FromLTRB(
            Math.Max(0, x - pad), Math.Max(0, y - pad),
            Math.Min(pageWidth, right + pad), Math.Min(pageHeight, bottom + pad));
    }

    /// <summary>
    /// The pixels manga-ocr reads for a block: the box rounded to whole pixels as Pillow's crop does
    /// (half to even), kept inside the page.
    /// </summary>
    internal static Rectangle RecognitionCrop(RectangleF block, int pageWidth, int pageHeight)
    {
        int x0 = Math.Clamp((int)Math.Round(block.Left), 0, pageWidth - 1);
        int y0 = Math.Clamp((int)Math.Round(block.Top), 0, pageHeight - 1);
        int x1 = Math.Clamp((int)Math.Round(block.Right), x0 + 1, pageWidth);
        int y1 = Math.Clamp((int)Math.Round(block.Bottom), y0 + 1, pageHeight);
        return Rectangle.FromLTRB(x0, y0, x1, y1);
    }

    /// <summary>
    /// Turns the page's read blocks into what is translated: one block per piece of writing that is
    /// read as one — a reading block.
    /// </summary>
    /// <remarks>
    /// <para>A reading block is what a reader takes in as one run of text, the way a paragraph is:
    /// columns set side by side, read right to left. One balloon may hold several — the page sets a
    /// second remark below the first, or steps it down and to the left, and even when one person
    /// says both the reader reads them as two. Each is translated on its own. Joining two blocks
    /// one above the other is the worst a join can do: the columns of the joined block are read
    /// right to left across both, so the first column of the upper block is followed by the first
    /// column of the lower one and the sentences are shuffled together.</para>
    ///
    /// <para>So two blocks are only ever joined when they run side by side
    /// (<see cref="SideBySide"/>) — and then when they are in one balloon, in two balloons that are
    /// one shape (<see cref="OneBalloon"/>), or both outside every balloon and close
    /// (<see cref="OneCaption"/>). The detector already gives each reading block a box of its own:
    /// labelling the 58 vertical pages at hand block by block
    /// (<c>.ai/vertical-ja3-handoff/overmerge/blocks-*.json</c>), no box held two, and only four
    /// pairs of boxes were one block — every one of them side by side.</para>
    ///
    /// <para>Rows (<see cref="AcrossRatio"/>) are kept out of the joins and marked
    /// <see cref="OcrTextBlock.RunsAcross"/>. <paramref name="passedOn"/> — what the column pipeline
    /// read from the long blocks — comes back as it was, already grouped by that pipeline.</para>
    /// </remarks>
    /// <param name="luma">
    /// The page in grey. Without it nothing is looked at between the blocks: balloons are then one
    /// shape whenever they overlap, and no panel border stands between two captions.
    /// </param>
    internal static List<OcrTextBlock> Assemble(
        IReadOnlyList<MangaBlock> blocks, IReadOnlyList<RectangleF> bubbles, IReadOnlyList<OcrTextBlock> passedOn,
        LumaPage? luma = null)
    {
        var owner = new int?[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i].Bounds;
            if (RunsAcross(block)) continue;

            int best = -1;
            for (int j = 0; j < bubbles.Count; j++)
            {
                if (Shared(block, bubbles[j]) >= BubbleCover * Area(block) &&
                    (best < 0 || Area(bubbles[j]) < Area(bubbles[best])))
                    best = j;
            }

            owner[i] = best;
        }

        var parent = Enumerable.Range(0, blocks.Count).ToArray();
        int Root(int a)
        {
            while (parent[a] != a) a = parent[a] = parent[parent[a]];
            return a;
        }

        for (int a = 0; a < blocks.Count; a++)
        for (int b = a + 1; b < blocks.Count; b++)
        {
            if (owner[a] is not { } inA || owner[b] is not { } inB) continue;
            if (!SideBySide(blocks[a].Bounds, blocks[b].Bounds)) continue;

            bool together = (inA, inB) switch
            {
                ( >= 0, >= 0) when inA == inB => true,
                ( >= 0, >= 0) => OneBalloon(blocks[a], blocks[b], bubbles[inA], bubbles[inB], luma),
                ( < 0, < 0) => OneCaption(blocks[a], blocks[b], luma),
                _ => false,
            };
            if (together) parent[Root(a)] = Root(b);
        }

        var runs = new Dictionary<int, List<int>>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (!runs.TryGetValue(Root(i), out var run)) runs[Root(i)] = run = [];
            run.Add(i);
        }

        // Each group where its first-read member was; rows and lone blocks where they were.
        var result = new List<OcrTextBlock>();
        var emitted = new HashSet<int>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var run = runs[Root(i)];
            if (run.Count == 1)
                result.Add(Single(blocks[i], luma));
            else if (emitted.Add(Root(i)))
                result.Add(Joined([.. ReadingOrder(run, blocks).Select(member => blocks[member])]));
        }

        result.AddRange(passedOn);
        return result;
    }

    /// <summary>
    /// How much of the shorter of two blocks' height the two must share to be side by side, the only
    /// way two blocks are ever one reading block.
    /// </summary>
    /// <remarks>
    /// <para>MEASURED on the 58 vertical pages labelled block by block, over every pair of boxes
    /// within three characters of each other. The four pairs that are one block share 0.79–0.93 of
    /// the shorter one's height: a first column set a little higher than the rest. What the user
    /// named as two blocks shares less: 空から降り注いだ…／兄の命を… (zang), one sentence stepped down
    /// and to the left, shares 0.59; いや、これから…／偉業ですら… and 一度目の“魔法”は、／物心が付いた…,
    /// one above the other, share nothing. Asked of every join on those pages, it takes the blocks
    /// that held two blocks one above the other from 38 to none and two blocks stepped or corner to
    /// corner from 7 to none, and parts nothing that was one block. It is the only bar that does
    /// both: at 0.7 one stepped pair is still joined, at 0.8 one of the four comes apart.</para>
    ///
    /// <para>Side by side it decides nothing, and nothing here does: two remarks side by side in one
    /// balloon, or in two lobes of one shape, are as close as the columns of one remark — ただ、 and
    /// それだけのことだ。 share 0.86 of a height and stand 0.4 of a character apart, the columns of one
    /// block 0.2–0.7. Joined, they still read in the right order, which is why they are left to the
    /// rules below: 12 such joins remain on the 58 pages.</para>
    /// </remarks>
    internal const double SideBySideShare = 0.75;

    internal static bool SideBySide(RectangleF a, RectangleF b) =>
        Along(a.Top, a.Bottom, b.Top, b.Bottom) >= SideBySideShare * Math.Min(a.Height, b.Height);

    /// <summary>
    /// Whether two blocks in two different balloons are in one shape: the balloons overlap, the
    /// paper inside one balloon runs on into the other, and no panel border lies between them.
    /// </summary>
    /// <remarks>
    /// <para>Balloons one speaker runs on through are drawn as one outline with lobes, and the
    /// paper runs from lobe to lobe; a balloon laid over another — a speaker cutting in — keeps its
    /// own outline across the one underneath, and two balloons meeting at a point share a sliver at
    /// most. MEASURED on the 46 transcribed pages, of the balloon pairs the box rule joined: all 39
    /// labelled one group are one shape at a grey level of 128, and 7 of the 9 that held two groups
    /// are not. Eroding the paper to cut thin necks changed nothing up to 6px and lost joins from
    /// 8px.</para>
    ///
    /// <para>Lobes set corner to corner or one above the other — ch50/008's 後宮内のどこか… over
    /// 泥水が冷たくて…, with no outline between them for the paper test to find — are kept apart by
    /// <see cref="SideBySide"/> before this is asked.</para>
    /// </remarks>
    private static bool OneBalloon(MangaBlock a, MangaBlock b, RectangleF inA, RectangleF inB, LumaPage? luma) =>
        Shared(inA, inB) > 0 &&
        (luma is null || (luma.SamePaper(RectangleF.Union(inA, inB), a.Bounds, b.Bounds) &&
                          !luma.RuledBetween(a.Bounds, b.Bounds)));

    /// <summary>
    /// Whether two blocks outside every balloon — already side by side — are one caption: their
    /// type is about one size, their boxes cross or stand a sliver apart, and no panel border lies
    /// between.
    /// </summary>
    /// <remarks>
    /// <para>MEASURED over every pair of balloonless blocks within four characters of each other on
    /// the 46 transcribed pages, labelled by hand. Side by side, the pairs of one caption were 0.29
    /// of a character apart or less and the nearest two captions 0.43 (a column at the page's edge,
    /// 次号より休載です): the bar is 0.3.</para>
    ///
    /// <para>One size, because a gap measured in the larger of two types is a gap the larger type
    /// makes small: a sound effect lettered big beside a line of small writing sits a sliver of ITS
    /// characters away: 遠っ beside ならば芳春様や… on ja3 432/010. The transcribed pages lose
    /// nothing at twice.</para>
    /// </remarks>
    private static bool OneCaption(MangaBlock a, MangaBlock b, LumaPage? luma)
    {
        if (luma is not null && luma.RuledBetween(a.Bounds, b.Bounds)) return false;

        double sizeA = GlyphSize(ToRect(a.Bounds), a.Text, across: false);
        double sizeB = GlyphSize(ToRect(b.Bounds), b.Text, across: false);
        if (Math.Max(sizeA, sizeB) > CaptionSizeRatio * Math.Min(sizeA, sizeB)) return false;
        if (Shared(a.Bounds, b.Bounds) > 0) return true;

        var gap = -Along(a.Bounds.Left, a.Bounds.Right, b.Bounds.Left, b.Bounds.Right);
        return gap <= CaptionSideGap * Math.Max(sizeA, sizeB);
    }

    // In characters of the larger of the two blocks' type.
    private const double CaptionSideGap = 0.3;
    // The larger type of two blocks of one caption is at most this many times the smaller.
    private const double CaptionSizeRatio = 2;

    // Length two spans have in common; negative is the gap between them.
    private static double Along(double startA, double endA, double startB, double endB) =>
        Math.Min(endA, endB) - Math.Max(startA, startB);

    /// <summary>
    /// Columns right to left; blocks that share most of their width are one band, top to bottom.
    /// </summary>
    private static List<int> ReadingOrder(List<int> members, IReadOnlyList<MangaBlock> blocks)
    {
        var bands = new List<List<int>>();
        foreach (var member in members
                     .OrderByDescending(m => blocks[m].Bounds.Right)
                     .ThenBy(m => blocks[m].Bounds.Top))
        {
            var bounds = blocks[member].Bounds;
            var band = bands.FirstOrDefault(band => band.Any(other =>
            {
                var o = blocks[other].Bounds;
                return Math.Min(bounds.Right, o.Right) - Math.Max(bounds.Left, o.Left) >
                       BandOverlap * Math.Min(bounds.Width, o.Width);
            }));
            if (band is null) bands.Add([member]);
            else band.Add(member);
        }

        return [.. bands.SelectMany(band => band.OrderBy(m => blocks[m].Bounds.Top))];
    }

    /// <summary>
    /// One block as it was read. A row set in several lines carries them, as the horizontal
    /// pipeline's groups do — see <see cref="MangaTextRows"/>.
    /// </summary>
    private static OcrTextBlock Single(MangaBlock block, LumaPage? luma)
    {
        var across = RunsAcross(block.Bounds);
        var bounds = ToRect(block.Bounds);
        var glyph = GlyphSize(bounds, block.Text, across);
        var lines = across && luma is not null ? Lines(block, luma) : null;
        if (lines is not null)
        {
            // √(w·h/n) is the same over the whole block as over each of its lines; what changes is
            // the height it may not exceed, which is now a line's.
            var heights = lines.Select(line => line.Height).Order().ToList();
            glyph = Math.Min(glyph, heights[heights.Count / 2]);
        }

        var script = LayoutScriptDetection.For(block.Text);
        return new OcrTextBlock(
            block.Text, bounds, lines, glyph, block.Confidence, script, bounds, ScriptGlyph(script, glyph))
        {
            RunsAcross = across,
        };
    }

    /// <summary>
    /// The block cut across into its lines, edge to edge: each reaches halfway to the next, so the
    /// furigana between two lines and the margin round them stay inside the lines, which are what
    /// both overlays erase. Null when it is one line.
    /// </summary>
    private static List<Rect>? Lines(MangaBlock block, LumaPage luma)
    {
        var rows = MangaTextRows.Find(luma, block.Bounds, block.Text.Count(c => !char.IsWhiteSpace(c)));
        if (rows.Count < 2) return null;

        var lines = new List<Rect>(rows.Count);
        double top = block.Bounds.Top;
        for (int i = 0; i < rows.Count; i++)
        {
            double bottom = i + 1 < rows.Count
                ? (rows[i].Bottom + rows[i + 1].Top) / 2.0
                : block.Bounds.Bottom;
            lines.Add(new Rect(block.Bounds.Left, top, block.Bounds.Width, bottom - top));
            top = bottom;
        }

        return lines;
    }

    private static OcrTextBlock Joined(List<MangaBlock> parts)
    {
        var text = string.Concat(parts.Select(part => part.Text));
        var lines = parts.Select(part => ToRect(part.Bounds)).ToList();
        var bounds = lines.Aggregate(Rect.Union);
        var sizes = parts
            .Select((part, i) => GlyphSize(lines[i], part.Text, across: false))
            .OrderBy(size => size)
            .ToList();
        var glyph = sizes[sizes.Count / 2];
        var weight = parts.Sum(part => Math.Max(1, part.Text.Length));
        var confidence = parts.Sum(part => part.Confidence * Math.Max(1, part.Text.Length)) / weight;
        var script = LayoutScriptDetection.For(text);
        return new OcrTextBlock(text, bounds, lines, glyph, confidence, script, bounds, ScriptGlyph(script, glyph));
    }

    /// <summary>
    /// The size of one character, from the block's area and how many characters were read in it.
    /// </summary>
    /// <remarks>
    /// A block holds several columns and the models do not say where each one is, so this is the
    /// area estimate the column pipeline also falls back on for a box it could not split: √(w·h/n),
    /// never more than the width of a column (or the height of a row) can hold. It errs large, since
    /// the gaps between columns count as writing; both overlays shrink the type to fit the block
    /// before they let it grow past it, so large is the safe side to err on.
    /// </remarks>
    // MEASURED on the 46 transcribed pages: against the column pipeline's own per-column pitch on the
    // same writing (347 blocks), √(w·h/n) comes out 1.45 times too large (IQR 1.33–1.59), and about
    // the same at every implied column count — the gaps between columns and the ragged column ends
    // are a near-constant share of a block. Divided out, the median error is 5%.
    private const double AreaOverPitch = 1.45;

    internal static double GlyphSize(Rect bounds, string text, bool across)
    {
        var characters = text.Count(c => !char.IsWhiteSpace(c));
        var side = across ? bounds.Height : bounds.Width;
        return characters > 0
            ? Math.Min(side, Math.Sqrt(bounds.Width * bounds.Height / characters) / AreaOverPitch)
            : side;
    }

    private static double? ScriptGlyph(OcrLayoutScript script, double glyph) =>
        script is OcrLayoutScript.Cjk or OcrLayoutScript.Latin ? glyph : null;

    private static Rect ToRect(RectangleF r) => new(r.X, r.Y, r.Width, r.Height);

    private static double Shared(RectangleF a, RectangleF b)
    {
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return Math.Max(0, w) * Math.Max(0, h);
    }

    private static double Area(RectangleF r) => (double)r.Width * r.Height;
}

/// <summary>One block the manga models found and read.</summary>
internal readonly record struct MangaBlock(RectangleF Bounds, string Text, double Confidence);

/// <summary>A page in grey, one byte a pixel, row after row.</summary>
internal sealed class LumaPage(byte[] pixels, int width, int height)
{
    // Paper is lighter than this and ink darker, on the scale Pillow's convert("L") gives.
    private const int PaperLevel = 128;
    // Darker than this counts as a ruled line: the border of a panel.
    private const int RuleLevel = 100;
    // How much of one row (or column) between two blocks has to be ruled for the space to be a border.
    private const double RuleCover = 0.9;

    internal int Width => width;
    internal int Height => height;

    internal byte this[int x, int y] => pixels[y * width + x];

    /// <summary>
    /// Whether the paper around block <paramref name="a"/> runs on, inside <paramref name="area"/>, to
    /// the paper around block <paramref name="b"/>. White text on black is asked the other way round;
    /// a block on paper and one on black are never on the same paper.
    /// </summary>
    internal bool SamePaper(RectangleF area, RectangleF a, RectangleF b)
    {
        var region = Clip(area);
        if (region.Width <= 0 || region.Height <= 0) return false;

        bool light = Median(Rectangle.Intersect(Clip(a), region)) >= PaperLevel;
        if (light != Median(Rectangle.Intersect(Clip(b), region)) >= PaperLevel) return false;

        // 4-connected regions of paper, labelled inside the area only: a balloon that runs off the
        // edge of its panel must not reach the next one round the page.
        var label = new int[region.Width * region.Height];
        var queue = new Queue<int>();
        int next = 0;
        for (int start = 0; start < label.Length; start++)
        {
            if (label[start] != 0 || !Paper(region, start, light)) continue;
            label[start] = ++next;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int at = queue.Dequeue(), x = at % region.Width, y = at / region.Width;
                Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
            }
        }

        int main = MostOf(a), other = MostOf(b);
        return main > 0 && main == other;

        void Visit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= region.Width || y >= region.Height) return;
            int at = y * region.Width + x;
            if (label[at] != 0 || !Paper(region, at, light)) return;
            label[at] = next;
            queue.Enqueue(at);
        }

        // The region that covers most of a block's box: the paper the writing sits on.
        int MostOf(RectangleF block)
        {
            var box = Rectangle.Intersect(Clip(block), region);
            var counts = new Dictionary<int, int>();
            for (int y = box.Top; y < box.Bottom; y++)
            for (int x = box.Left; x < box.Right; x++)
            {
                int l = label[(y - region.Top) * region.Width + (x - region.Left)];
                if (l > 0) counts[l] = counts.GetValueOrDefault(l) + 1;
            }

            return counts.Count == 0 ? 0 : counts.MaxBy(pair => pair.Value).Key;
        }
    }

    /// <summary>
    /// Whether a panel border runs through the space between two blocks: a row of it (for blocks
    /// one above the other) or a column (side by side) that is nearly all ruled, over the width (or
    /// height) the two have in common. On black, a white rule.
    /// </summary>
    internal bool RuledBetween(RectangleF a, RectangleF b)
    {
        bool dark = Median(Clip(a)) < PaperLevel && Median(Clip(b)) < PaperLevel;
        double left = Math.Max(a.Left, b.Left), right = Math.Min(a.Right, b.Right);
        double top = Math.Max(a.Top, b.Top), bottom = Math.Min(a.Bottom, b.Bottom);

        if (right > left && bottom < top)
        {
            // One above the other: the rows between the upper one's foot and the lower one's head.
            var strip = Clip(RectangleF.FromLTRB((float)left, (float)bottom, (float)right, (float)top));
            for (int y = strip.Top; y < strip.Bottom; y++)
            {
                int ruled = 0;
                for (int x = strip.Left; x < strip.Right; x++) if (Ruled(x, y, dark)) ruled++;
                if (strip.Width > 0 && ruled >= RuleCover * strip.Width) return true;
            }
        }
        else if (bottom > top && right < left)
        {
            var strip = Clip(RectangleF.FromLTRB((float)right, (float)top, (float)left, (float)bottom));
            for (int x = strip.Left; x < strip.Right; x++)
            {
                int ruled = 0;
                for (int y = strip.Top; y < strip.Bottom; y++) if (Ruled(x, y, dark)) ruled++;
                if (strip.Height > 0 && ruled >= RuleCover * strip.Height) return true;
            }
        }

        return false;
    }

    private bool Ruled(int x, int y, bool dark) =>
        dark ? 255 - this[x, y] < RuleLevel : this[x, y] < RuleLevel;

    private bool Paper(Rectangle region, int at, bool light)
    {
        byte value = this[region.Left + at % region.Width, region.Top + at / region.Width];
        return light ? value >= PaperLevel : value < PaperLevel;
    }

    // The box in whole pixels as NumPy slices it — [int(top):int(bottom), int(left):int(right)] —
    // kept inside the page.
    private Rectangle Clip(RectangleF box)
    {
        int left = Math.Clamp((int)box.Left, 0, width), top = Math.Clamp((int)box.Top, 0, height);
        int right = Math.Clamp((int)box.Right, left, width), bottom = Math.Clamp((int)box.Bottom, top, height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    internal double Median(Rectangle box)
    {
        if (box.Width <= 0 || box.Height <= 0) return 0;
        var histogram = new int[256];
        for (int y = box.Top; y < box.Bottom; y++)
        for (int x = box.Left; x < box.Right; x++)
            histogram[this[x, y]]++;

        // As NumPy takes it: of an even count, the mean of the two middle values.
        int count = box.Width * box.Height, seen = 0, low = -1;
        for (int v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (low < 0 && seen > (count - 1) / 2) low = v;
            if (seen > count / 2) return (low + v) / 2.0;
        }

        return 255;
    }
}
