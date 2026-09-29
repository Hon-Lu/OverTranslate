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
    /// Turns the page's read blocks into what is translated: one block per sentence.
    /// </summary>
    /// <remarks>
    /// <para>Bubbles whose outlines overlap are one run of speech, and their blocks are joined into
    /// one group, right to left and top to bottom. The transcripts write two or three touching
    /// bubbles as one sentence — the same speaker carrying on — and the detector, correctly, gives
    /// each bubble its own box, so without this the sentence is translated in pieces. Checked group by
    /// group: 58 of 64 joins were the same speaker; the other 5–6 were two speakers whose bubbles
    /// touch, which the geometry cannot tell apart (raising the overlap bar loses good joins first:
    /// 15% of the smaller bubble took ja2 from 138 whole sentences to 132). Joining only blocks inside
    /// one bubble was measured first and never fired — the detector does not split a bubble.</para>
    ///
    /// <para>Rows (<see cref="AcrossRatio"/>) are kept out of the joins and marked
    /// <see cref="OcrTextBlock.RunsAcross"/>. <paramref name="passedOn"/> — what the column pipeline
    /// read from the long blocks — comes back as it was, already grouped by that pipeline.</para>
    /// </remarks>
    internal static List<OcrTextBlock> Assemble(
        IReadOnlyList<MangaBlock> blocks, IReadOnlyList<RectangleF> bubbles, IReadOnlyList<OcrTextBlock> passedOn)
    {
        var owner = new Dictionary<int, List<int>>();
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

            if (best >= 0)
            {
                if (!owner.TryGetValue(best, out var members)) owner[best] = members = [];
                members.Add(i);
            }
        }

        // Bubbles that touch at all are one run of speech.
        var parent = Enumerable.Range(0, bubbles.Count).ToArray();
        int Root(int a)
        {
            while (parent[a] != a) a = parent[a] = parent[parent[a]];
            return a;
        }

        for (int a = 0; a < bubbles.Count; a++)
        for (int b = a + 1; b < bubbles.Count; b++)
            if (Shared(bubbles[a], bubbles[b]) > 0)
                parent[Root(a)] = Root(b);

        var runs = new Dictionary<int, List<int>>();
        foreach (var (bubble, members) in owner)
        {
            var root = Root(bubble);
            if (!runs.TryGetValue(root, out var run)) runs[root] = run = [];
            run.AddRange(members);
        }

        var joinedInto = new Dictionary<int, int>();
        var joined = new List<List<int>>();
        foreach (var run in runs.Values.Where(run => run.Count >= 2))
        {
            foreach (var member in run) joinedInto[member] = joined.Count;
            joined.Add(ReadingOrder(run, blocks));
        }

        // Each group where its first-read member was; rows and lone blocks where they were.
        var result = new List<OcrTextBlock>();
        var emitted = new HashSet<int>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (!joinedInto.TryGetValue(i, out var group))
            {
                result.Add(Single(blocks[i]));
                continue;
            }

            if (emitted.Add(group))
                result.Add(Joined([.. joined[group].Select(member => blocks[member])]));
        }

        result.AddRange(passedOn);
        return result;
    }

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

    private static OcrTextBlock Single(MangaBlock block)
    {
        var across = RunsAcross(block.Bounds);
        var bounds = ToRect(block.Bounds);
        var glyph = GlyphSize(bounds, block.Text, across);
        var script = LayoutScriptDetection.For(block.Text);
        return new OcrTextBlock(
            block.Text, bounds, null, glyph, block.Confidence, script, bounds, ScriptGlyph(script, glyph))
        {
            RunsAcross = across,
        };
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
