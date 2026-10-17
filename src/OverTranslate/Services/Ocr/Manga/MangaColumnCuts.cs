using System.Drawing;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// Where a block of columns is cut so that CPT-flex reads it in pieces it reads well, found from
/// CRAFT's character map (<see cref="MangaColumnDetector"/>).
/// </summary>
/// <remarks>
/// <para>CPT-flex reads a block whole, and reads it well up to about forty characters; past that,
/// in a block of several columns, whole columns go missing. MEASURED on 301 balloons of real
/// columns set side by side two to four at a time: 29% of characters wrong at 45–59, 57% at 60 and
/// over, 21.6% over all of them (14.4% at half size). Cut into runs of whole columns of up to
/// <see cref="PieceLimit"/> characters and read piece by piece, they come back at 3.6% (5.0%); the
/// five hardest real blocks of the corpora at 20% against 61%, vertical-image-ja5's balloon of
/// sixteen short columns among them, 26 of its 60 wrong whole and one cut. The evaluation set's 2031 blocks, which the
/// manga models already read well, came out the same or better at both sizes.</para>
///
/// <para>The characters are CRAFT's: blobs of its region map, gathered into columns by where they
/// stand across. A column much narrower than the page's columns is furigana, which sits to the right
/// of the column it belongs to and goes with it, so no cut ever parts the two
/// (<see cref="RubyWidth"/>). The cut runs down the gap between two runs of columns, where it
/// crosses the least ink (<see cref="LeastInkLine"/>).</para>
///
/// <para>Nothing is cut — null, the block is not this class's — when CRAFT does not see columns:
/// fewer than two, characters that are not stacked, lines that run across
/// (<see cref="AcrossVote"/>), or columns that lean (<see cref="MaxLean"/>).</para>
/// </remarks>
internal static class MangaColumnCuts
{
    /// <summary>
    /// A block this many times as wide as tall or more is asked for its columns (<see cref="Asks"/>).
    /// </summary>
    /// <remarks>
    /// <para>Balloons of several columns are about as wide as tall or wider; a single column, or two,
    /// is narrower.</para>
    ///
    /// <para>A long column of columns — what <see cref="MangaPageLayout.IsLong"/> keeps from the manga
    /// models — is not asked. MEASURED on the two in the corpora, at five scales: the zang recap box,
    /// cut a column at a time, lost the first words of its first column at every scale; the ja3
    /// spine, a title over three columns of credits in another size, lost the credits. The column
    /// pipeline reads both right at full size.</para>
    /// </remarks>
    internal const double CutFrom = 1.3;

    /// <summary>
    /// A block this many times as tall as wide or more is asked too: a long narration of a few
    /// columns, ja5a's 200×1157 of three, which CPT-flex read whole with four characters wrong and cut
    /// with none.
    /// </summary>
    internal const double CutTallFrom = 4;

    /// <summary>Whether a block the manga models read is asked for its columns.</summary>
    internal static bool Asks(RectangleF block) =>
        block.Width >= CutFrom * block.Height || block.Height >= CutTallFrom * block.Width;

    /// <summary>A region score above this is part of a character.</summary>
    internal const float RegionFloor = 0.35f;

    /// <summary>A region or link score above this joins characters into a word, for <see cref="AcrossVote"/>.</summary>
    internal const float LinkFloor = 0.3f;

    /// <summary>The fewest map cells a character has; fewer is a speck.</summary>
    internal const int MinCharacterCells = 3;

    /// <summary>A column narrower than this share of the block's wider columns is furigana.</summary>
    internal const double RubyWidth = 0.6;

    /// <summary>How many characters a block may hold and still be read whole.</summary>
    internal const double WholeLimit = 40;

    /// <summary>The most characters one piece holds, unless a single column holds more.</summary>
    internal const double PieceLimit = 30;

    /// <summary>
    /// The words' area-weighted log of height over width below which the lines run across: about
    /// two and a quarter times as wide as tall.
    /// </summary>
    internal const double AcrossVote = -0.8;

    /// <summary>How far a column's characters may drift across per pixel down before it leans: 4°.</summary>
    internal const double MaxLean = 0.07;

    /// <summary>Above and below a piece's characters, the margin it keeps, in block pixels.</summary>
    private const int PieceMargin = 4;

    /// <param name="Region">CRAFT's region scores, <paramref name="Width"/> by <paramref name="Height"/>, row by row.</param>
    /// <param name="Affinity">CRAFT's link scores, the same size.</param>
    /// <param name="Cell">Block pixels to one map cell.</param>
    /// <param name="Used">Map cells across and down that hold the block; the rest is padding.</param>
    internal sealed record Maps(float[] Region, float[] Affinity, int Width, int Height, double Cell, Size Used);

    /// <summary>
    /// The pieces of <paramref name="block"/> to read, right to left, in page coordinates — one, the
    /// block itself, when it is columns short enough to read whole — or null when CRAFT does not
    /// see columns to cut along.
    /// </summary>
    internal static IReadOnlyList<RectangleF>? Pieces(Maps maps, RectangleF block, LumaPage luma)
    {
        if (Vote(maps) < AcrossVote) return null;

        var characters = Characters(maps);
        var columns = Columns(characters);
        if (columns.Count == 0 || Leans(columns)) return null;

        var bases = Bases(columns);
        if (bases.Count < 2 || Median(bases.Select(b => (b.Bottom - b.Top) / Math.Max(1, b.Width))) < 2) return null;
        if (bases.Sum(Estimate) <= WholeLimit) return [block];

        // Runs of whole columns, right to left, each up to the limit.
        var runs = new List<List<Base>>();
        double count = 0;
        foreach (var column in Enumerable.Reverse(bases))
        {
            double estimate = Estimate(column);
            if (runs.Count == 0 || count + estimate > PieceLimit)
            {
                runs.Add([]);
                count = 0;
            }
            runs[^1].Add(column);
            count += estimate;
        }
        if (runs.Count == 1) return [block];

        // The cut between run i and run i + 1: down the gap, where it crosses the least ink.
        var cuts = new double[runs.Count - 1];
        for (int i = 0; i < cuts.Length; i++)
        {
            double left = runs[i][^1].Left, right = runs[i + 1][0].RubyRight;
            double top = Math.Min(runs[i].Min(b => b.Top), runs[i + 1].Min(b => b.Top));
            double bottom = Math.Max(runs[i].Max(b => b.Bottom), runs[i + 1].Max(b => b.Bottom));
            cuts[i] = LeastInkLine(luma, block, (left + right) / 2, right, left, top, bottom);
        }

        var pieces = new List<RectangleF>(runs.Count);
        for (int i = 0; i < runs.Count; i++)
        {
            double x1 = i == 0 ? block.Width : cuts[i - 1];
            double x0 = i == runs.Count - 1 ? 0 : cuts[i];
            double y0 = Math.Max(0, runs[i].Min(b => b.Top) - PieceMargin);
            double y1 = Math.Min(block.Height, runs[i].Max(b => b.Bottom) + PieceMargin);
            pieces.Add(new RectangleF(
                (float)(block.X + x0), (float)(block.Y + y0), (float)(x1 - x0), (float)(y1 - y0)));
        }
        return pieces;
    }

    /// <summary>A character's box, in block pixels.</summary>
    internal readonly record struct Character(double Left, double Top, double Right, double Bottom)
    {
        internal double Width => Right - Left;
        internal double Center => (Left + Right) / 2;
    }

    /// <summary>A column of characters, left to right in the block; <see cref="Width"/> is its characters' median.</summary>
    internal sealed record Column(double Left, double Right, double Top, double Bottom, double Width, List<Character> Characters);

    /// <summary>A column with its furigana, the right edge taking the furigana in.</summary>
    internal sealed record Base(double Left, double Right, double RubyRight, double Top, double Bottom, double Width);

    /// <summary>Blobs of the region map, 4-connected, in block pixels.</summary>
    internal static List<Character> Characters(Maps maps)
    {
        int w = Math.Min(maps.Width, maps.Used.Width), h = Math.Min(maps.Height, maps.Used.Height);
        var found = new List<Character>();
        foreach (var (x0, y0, x1, y1, cells) in Blobs(w, h, at => maps.Region[(at / w) * maps.Width + at % w] > RegionFloor))
            if (cells >= MinCharacterCells)
                found.Add(new Character(x0 * maps.Cell, y0 * maps.Cell, x1 * maps.Cell, y1 * maps.Cell));
        return found;
    }

    /// <summary>
    /// Which way the words run: the area-weighted log of their height over width, words being the
    /// characters with the links between them; positive runs down. Words under a tenth of the
    /// largest are left out.
    /// </summary>
    internal static double Vote(Maps maps)
    {
        var words = Blobs(maps.Width, maps.Height,
                at => maps.Region[at] > RegionFloor || maps.Affinity[at] > LinkFloor)
            .Where(blob => blob.Cells >= 4)
            .ToList();
        if (words.Count == 0) return 0;
        int largest = words.Max(word => word.Cells);
        double sum = 0, weight = 0;
        foreach (var (x0, y0, x1, y1, cells) in words)
        {
            if (cells < 0.1 * largest) continue;
            sum += cells * Math.Log((double)(y1 - y0) / (x1 - x0));
            weight += cells;
        }
        return sum / weight;
    }

    /// <summary>Characters gathered by where they stand across, left to right.</summary>
    internal static List<Column> Columns(List<Character> characters)
    {
        var columns = new List<Column>();
        var widths = new List<List<double>>();
        foreach (var c in characters.OrderBy(c => c.Center))
        {
            if (columns.Count > 0)
            {
                var last = columns[^1];
                double middle = (last.Left + last.Right) / 2;
                if (c.Center - middle < 0.5 * Math.Max(last.Width, c.Width) && c.Center < last.Right + 0.2 * c.Width)
                {
                    widths[^1].Add(c.Width);
                    last.Characters.Add(c);
                    columns[^1] = last with
                    {
                        Left = Math.Min(last.Left, c.Left), Right = Math.Max(last.Right, c.Right),
                        Top = Math.Min(last.Top, c.Top), Bottom = Math.Max(last.Bottom, c.Bottom),
                        Width = Median(widths[^1]),
                    };
                    continue;
                }
            }
            columns.Add(new Column(c.Left, c.Right, c.Top, c.Bottom, c.Width, [c]));
            widths.Add([c.Width]);
        }
        return columns;
    }

    /// <summary>
    /// The columns with furigana folded into the column to its left; furigana with no column to its
    /// left is dropped.
    /// </summary>
    internal static List<Base> Bases(List<Column> columns)
    {
        double wide = Percentile(columns.Select(c => c.Width), 0.75);
        var bases = new List<Base>();
        foreach (var column in columns)
        {
            if (column.Width < RubyWidth * wide)
            {
                if (bases.Count > 0) bases[^1] = bases[^1] with { RubyRight = Math.Max(bases[^1].RubyRight, column.Right) };
                continue;
            }
            bases.Add(new Base(column.Left, column.Right, column.Right, column.Top, column.Bottom, column.Width));
        }
        return bases;
    }

    /// <summary>A column's characters, from its length in its characters' width.</summary>
    internal static double Estimate(Base column) => Math.Max(1, (column.Bottom - column.Top) / Math.Max(1, column.Width));

    /// <summary>
    /// Whether the columns lean: over those of four characters or more, the median of how far
    /// their characters drift across for each pixel down.
    /// </summary>
    internal static bool Leans(List<Column> columns)
    {
        var slopes = new List<double>();
        foreach (var column in columns.Where(c => c.Characters.Count >= 4))
        {
            var cs = column.Characters;
            double my = cs.Average(c => (c.Top + c.Bottom) / 2), mx = cs.Average(c => c.Center);
            double sxy = 0, syy = 0;
            foreach (var c in cs)
            {
                double dy = (c.Top + c.Bottom) / 2 - my;
                sxy += dy * (c.Center - mx);
                syy += dy * dy;
            }
            if (syy > 0) slopes.Add(Math.Abs(sxy / syy));
        }
        return slopes.Count > 0 && Median(slopes) > MaxLean;
    }

    /// <summary>
    /// The line down the gap between <paramref name="from"/> and <paramref name="to"/> (block pixels)
    /// that crosses the least ink from <paramref name="top"/> to <paramref name="bottom"/>, the one
    /// nearest <paramref name="at"/> of those.
    /// </summary>
    /// <remarks>
    /// CRAFT's boxes are not the ink's: a stroke reaches past them, and in a tight balloon the boxes
    /// of neighbouring columns overlap. Cut down the middle of the boxes' gap, bt-original3's だから!!
    /// was cut in two. Refusing to cut wherever the line meets any ink cost more than it saved —
    /// MEASURED on the composite balloons: 8.2% of characters wrong refusing a line that crosses a
    /// quarter of a character's width of ink, 3.6% always cutting where the ink is least.
    /// </remarks>
    internal static double LeastInkLine(LumaPage luma, RectangleF block, double at, double from, double to, double top, double bottom)
    {
        var (threshold, dark) = luma.Ink(block);
        int y0 = Math.Max(0, (int)(block.Y + top)), y1 = Math.Min(luma.Height, (int)Math.Ceiling(block.Y + bottom));
        int lo = Math.Max(0, (int)Math.Floor(block.X + Math.Min(from, to)));
        int hi = Math.Min(luma.Width - 1, (int)Math.Ceiling(block.X + Math.Max(from, to)));
        double target = block.X + at;
        int best = -1, bestInk = int.MaxValue;
        for (int x = lo; x <= hi; x++)
        {
            int ink = 0;
            for (int y = y0; y < y1; y++)
                if (dark ? luma[x, y] <= threshold : luma[x, y] > threshold) ink++;
            if (ink < bestInk || (ink == bestInk && Math.Abs(x - target) < Math.Abs(best - target)))
            {
                best = x;
                bestInk = ink;
            }
        }
        return best < 0 ? at : best - block.X;
    }

    private static List<(int X0, int Y0, int X1, int Y1, int Cells)> Blobs(int width, int height, Func<int, bool> on)
    {
        var blobs = new List<(int, int, int, int, int)>();
        var seen = new bool[width * height];
        var queue = new Queue<int>();
        for (int start = 0; start < seen.Length; start++)
        {
            if (seen[start] || !on(start)) continue;
            seen[start] = true;
            queue.Enqueue(start);
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, cells = 0;
            while (queue.Count > 0)
            {
                int at = queue.Dequeue(), x = at % width, y = at / width;
                cells++;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                if (x > 0) Visit(at - 1);
                if (x < width - 1) Visit(at + 1);
                if (y > 0) Visit(at - width);
                if (y < height - 1) Visit(at + width);
            }
            blobs.Add((x0, y0, x1 + 1, y1 + 1, cells));
        }
        return blobs;

        void Visit(int at)
        {
            if (seen[at] || !on(at)) return;
            seen[at] = true;
            queue.Enqueue(at);
        }
    }

    private static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    // As NumPy's percentile: linear between the two nearest ranks.
    private static double Percentile(IEnumerable<double> values, double q)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return 0;
        double rank = q * (sorted.Count - 1);
        int below = (int)Math.Floor(rank);
        int above = Math.Min(sorted.Count - 1, below + 1);
        return sorted[below] + (rank - below) * (sorted[above] - sorted[below]);
    }
}
