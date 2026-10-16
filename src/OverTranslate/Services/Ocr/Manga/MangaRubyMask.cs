namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// Paints a block's ruby over with its background before Hayai reads it: Hayai reads ruby into the
/// text (めいきゅういぐち迷宮入り口) where manga-ocr learned to leave it out.
/// </summary>
/// <remarks>
/// <para>A block is cut into lines across its writing — columns for a block at least as tall as it
/// is wide, rows otherwise — by where there is ink, and each line into glyphs along it. The type
/// size is the median glyph length. A line is ruby when it is at most <see cref="RubyWidth"/> of the
/// type size across, its glyphs at most <see cref="RubyGlyph"/> of it long and mostly about as long
/// as the line is wide, and it lies within half a type size of a line of body text (0.8–1.6 type
/// sizes across). Lines touching the block's edge are never ruby, nor taken as body text: they are
/// as often a balloon's outline or a neighbour's text. Each row of the ruby line is then cleared of
/// the ink that lies wholly inside it, so body glyphs that reach into it keep their strokes.</para>
///
/// <para>MEASURED on the shared evaluation set (findings-A3): 279 of the 1417 blocks are touched.
/// Hayai's character error rate on the hard blocks (art, horizontal, Latin, photo) falls from 6.05%
/// to 4.97% at the page's size and from 9.18% to 7.89% at half; clean dialogue 0.76% to 0.69% at
/// the page's size and level at half; ruby read into the text, 6 blocks to 4. At the type size from
/// the 75th percentile instead of the median, glyphs run together at half size made the type look
/// twice its size, and whole columns of body text were cleared as ruby.</para>
/// </remarks>
internal static class MangaRubyMask
{
    internal const double RubyWidth = 0.6;
    internal const double RubyGlyph = 0.65;
    private const int InkContrast = 80;

    /// <summary>Clears the ruby of a packed RGB block in place; whether any was found.</summary>
    internal static bool Apply(byte[] rgb, int width, int height)
    {
        var gray = new int[width * height];
        for (int i = 0; i < gray.Length; i++)
            gray[i] = (rgb[i * 3] * 19595 + rgb[i * 3 + 1] * 38470 + rgb[i * 3 + 2] * 7471 + 0x8000) >> 16;
        int background = Background(gray, width, height);

        bool columns = height >= width;
        // Along: the writing direction; across: the lines' axis.
        int along = columns ? height : width, across = columns ? width : height;
        var ink = new bool[across][];
        for (int x = 0; x < across; x++)
        {
            ink[x] = new bool[along];
            for (int y = 0; y < along; y++)
                ink[x][y] = Math.Abs(gray[columns ? y * width + x : x * width + y] - background) > InkContrast;
        }

        var ruby = RubyLines(ink, along);
        if (ruby.Count == 0) return false;

        var value = (byte)background;
        foreach (var (start, end) in ruby)
        {
            for (int y = 0; y < along; y++)
            {
                bool keep = false;
                int x = 0;
                while (x < across)
                {
                    if (!ink[x][y])
                    {
                        x++;
                        continue;
                    }

                    int first = x;
                    while (x < across && ink[x][y]) x++;
                    if (x <= start || first >= end) continue;
                    if (first >= start - 1 && x <= end + 1) Paint(first, x, y);
                    else keep = true;
                }

                if (!keep) Paint(start, end, y);
            }
        }

        return true;

        void Paint(int from, int to, int y)
        {
            for (int x = from; x < to; x++)
            {
                int p = (columns ? y * width + x : x * width + y) * 3;
                rgb[p] = rgb[p + 1] = rgb[p + 2] = value;
            }
        }
    }

    /// <summary>The ruby lines, as ranges across the block.</summary>
    /// <param name="ink">Ink by line position across, then along.</param>
    internal static List<(int Start, int End)> RubyLines(bool[][] ink, int along)
    {
        int across = ink.Length;
        var profile = new bool[across];
        for (int x = 0; x < across; x++) profile[x] = ink[x].Count(v => v) >= Math.Max(1, 0.01 * along);
        var lines = Runs(profile);
        var found = new List<(int, int)>();
        if (lines.Count < 2) return found;

        var glyphs = new List<List<(int Start, int End)>>();
        var lengths = new List<int>();
        foreach (var (start, end) in lines)
        {
            var any = new bool[along];
            for (int x = start; x < end; x++)
                for (int y = 0; y < along; y++)
                    any[y] |= ink[x][y];
            var line = Runs(any);
            glyphs.Add(line);
            lengths.AddRange(line.Where(g => g.Start > 0 && g.End < along).Select(g => g.End - g.Start));
        }

        if (lengths.Count < 3) return found;
        lengths.Sort();
        int type = lengths[lengths.Count / 2];
        if (type < 6) return found;

        for (int k = 0; k < lines.Count; k++)
        {
            var (start, end) = lines[k];
            int size = end - start;
            var line = glyphs[k];
            if (size > RubyWidth * type || start == 0 || end == across || line.Count < 2) continue;
            var glyphLengths = line.Select(g => g.End - g.Start).Order().ToList();
            if (glyphLengths[glyphLengths.Count / 2] > RubyGlyph * type) continue;
            if (glyphLengths.Count(l => l >= 0.3 * size && l <= 1.6 * size) < 0.6 * line.Count) continue;
            bool beside = false;
            foreach (int j in new[] { k - 1, k + 1 })
            {
                if (j < 0 || j >= lines.Count) continue;
                var (otherStart, otherEnd) = lines[j];
                int gap = j < k ? start - otherEnd : otherStart - end;
                int otherSize = otherEnd - otherStart;
                beside |= otherSize >= 0.8 * type && otherSize <= 1.6 * type
                    && otherStart > 0 && otherEnd < across && gap <= 0.5 * type;
            }

            if (beside) found.Add((start, end));
        }

        return found;
    }

    /// <summary>The median grey of the block's edge, the four sides each counted whole.</summary>
    private static int Background(int[] gray, int width, int height)
    {
        var edge = new List<int>(2 * (width + height));
        for (int x = 0; x < width; x++) edge.Add(gray[x]);
        for (int x = 0; x < width; x++) edge.Add(gray[(height - 1) * width + x]);
        for (int y = 0; y < height; y++) edge.Add(gray[y * width]);
        for (int y = 0; y < height; y++) edge.Add(gray[y * width + width - 1]);
        edge.Sort();
        int n = edge.Count;
        return n % 2 == 1 ? edge[n / 2] : (edge[n / 2 - 1] + edge[n / 2]) / 2;
    }

    private static List<(int Start, int End)> Runs(bool[] set)
    {
        var runs = new List<(int, int)>();
        int i = 0;
        while (i < set.Length)
        {
            if (!set[i])
            {
                i++;
                continue;
            }

            int start = i;
            while (i < set.Length && set[i]) i++;
            runs.Add((start, i));
        }

        return runs;
    }
}
