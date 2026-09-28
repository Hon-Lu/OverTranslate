namespace OverTranslate.Services.Ocr;

/// <summary>
/// Gives the detector's probability map to the columns before anything on it is framed across: a
/// row the model lit over the heads of columns standing side by side is cut at the columns and each
/// head joined to the column under it.
/// </summary>
/// <remarks>
/// <para>What it is for. MEASURED on
/// <c>vertical-image-ja3/zang-songnofuriren-001-147hua/2026-09-28 19 08 58 (2).png</c>: where several
/// columns start at the same height, the model itself lights their first characters as one row —
/// <c>代遠大勢魔</c> is a single 184x15 bar in the map, 14 pixels above the columns it was taken from,
/// and <c>光</c> and <c>降</c> are each a bar with their reading beside them. The library frames that
/// row as writing across; the columns are framed from their second character, or in pieces, and
/// <c>空から降り注いだ</c> is read as <c>空からり如注の</c>. Dropping the row afterwards
/// (<see cref="VerticalColumnGrouping"/>) does not bring the characters back, and a threshold does
/// not either: the bar is as bright as the columns.</para>
///
/// <para>So the columns are found first, on the map as it is, and the rows are what is left:</para>
/// <list type="number">
/// <item>Blobs of the thresholded map, not dilated, eight-connected as the library's contours are.
/// A blob this much wider than tall (<see cref="RowShape"/>) is a row; any other is a piece of a
/// column — a whole column, or one character of it.</item>
/// <item>Where a piece stands just above or below a row, the part of the row over it is a piece too
/// (the row's height a character's, the gap under a character). This repeats, so a row two
/// characters above a column reaches it through the row in between.</item>
/// <item>Pieces over each other, a character's gap apart, are one column. A column must be half
/// again as long as it is wide and hold at least one piece that is not part of a row.</item>
/// <item>A row that gave a piece to a column is cut: what lies between the columns it gave to is
/// narrower than a character — the readings beside the heads — and is taken off the map; a wider
/// remainder stays, apart from the columns by a gutter the library's one-pixel dilation cannot
/// cross, and is framed across as before.</item>
/// <item>The gaps along a column that took a head are bridged down its middle, so the library frames
/// the column as one box from its first character.</item>
/// </list>
///
/// <para>Nothing else on the map changes: a page with no row standing on a column is handed to the
/// library as the model wrote it, and its boxes are the library's own.</para>
/// </remarks>
internal static class ColumnsFirst
{
    /// <summary>A blob this many times wider than tall is writing across (or a row of heads).</summary>
    private const double RowShape = 1.6;

    /// <summary>The widest gap, in characters, between two pieces of one column.</summary>
    private const double PieceGap = 1.0;

    /// <summary>Blobs smaller than this, in map pixels, are specks, as in <see cref="DetectorProbability"/>.</summary>
    private const int MinimumBlob = 12;

    /// <summary>What a bridged gap is given: over the library's threshold, under the ink either side of it.</summary>
    private const float Bridge = 0.5f;

    /// <summary>Columns kept clear between what a column took of a row and what is left of it.</summary>
    private const int Gutter = 3;

    /// <summary>
    /// Rewrites <paramref name="map"/> in place, column first; whether anything was changed.
    /// </summary>
    internal static bool Carve(float[] map, int width, int height, float threshold)
    {
        var labels = new int[map.Length];
        var blobs = Blobs(map, width, height, threshold, labels);
        var rows = blobs.Where(b => b.Width >= RowShape * b.Height).ToList();
        if (rows.Count == 0)
            return false;

        var pieces = blobs.Where(b => b.Width < RowShape * b.Height)
            .Select(b => new Piece(b.Left, b.Top, b.Right, b.Bottom, 0, b.Id)).ToList();
        if (pieces.Count == 0)
            return false;

        // The parts of rows standing over or under a piece, until no row gives another.
        for (var round = 0; round < 4; round++)
        {
            var added = false;
            foreach (var row in rows)
            {
                for (var i = 0; i < pieces.Count; i++)
                {
                    var piece = pieces[i];
                    if (piece.Row == row.Id || piece.Width < 0.7 * row.Height ||
                        Across(piece, row.Left, row.Right) < 0.7 * piece.Width)
                        continue;
                    var glyph = piece.Glyph;
                    if (row.Height < 0.6 * glyph || row.Height > 1.5 * glyph)
                        continue;
                    var gap = piece.Top > row.Bottom ? piece.Top - row.Bottom - 1 : row.Top - piece.Bottom - 1;
                    if (gap < 0 || gap > PieceGap * glyph)
                        continue;
                    if (pieces.Any(p => p.Row == row.Id && Across(p, piece.Left, piece.Right) > 0.5 * glyph))
                        continue;

                    // As tall as the row's ink over the piece is, not the whole row: that is where
                    // the gap to the piece is bridged from.
                    if (Ink(labels, width, row, Math.Max(piece.Left, row.Left), Math.Min(piece.Right, row.Right))
                        is not var (top, bottom))
                        continue;
                    pieces.Add(new Piece(
                        Math.Max(piece.Left, row.Left), top, Math.Min(piece.Right, row.Right), bottom, row.Id, row.Id));
                    added = true;
                }
            }
            if (!added) break;
        }
        if (pieces.All(p => p.Row == 0))
            return false;

        var parent = Enumerable.Range(0, pieces.Count).ToArray();
        for (var a = 0; a < pieces.Count; a++)
            for (var b = a + 1; b < pieces.Count; b++)
            {
                var (p, q) = (pieces[a], pieces[b]);
                if (p.Row != 0 && p.Row == q.Row) continue;
                if (Across(p, q.Left, q.Right) < 0.5 * Math.Min(p.Width, q.Width)) continue;
                var gap = p.Top <= q.Top ? q.Top - p.Bottom - 1 : p.Top - q.Bottom - 1;
                if (gap > PieceGap * Math.Max(p.Glyph, q.Glyph)) continue;
                parent[Find(parent, a)] = Find(parent, b);
            }

        var columns = Enumerable.Range(0, pieces.Count).GroupBy(i => Find(parent, i))
            .ToDictionary(g => g.Key, g => g.Select(i => pieces[i]).ToList());
        bool IsColumn(List<Piece> members)
        {
            var own = members.Where(m => m.Row == 0).Select(m => m.Width).OrderBy(w => w).ToList();
            if (own.Count == 0) return false;
            var length = members.Max(m => m.Bottom) - members.Min(m => m.Top) + 1;
            return length >= 1.5 * own[own.Count / 2];
        }

        // The ink the columns stand on, which no gutter may take from them.
        var standing = columns.Values.Where(IsColumn)
            .SelectMany(m => m).Where(m => m.Row == 0).Select(m => m.Blob).ToHashSet();

        var changed = false;
        var took = new HashSet<int>();
        foreach (var row in rows)
        {
            var given = Enumerable.Range(0, pieces.Count)
                .Where(i => pieces[i].Row == row.Id && IsColumn(columns[Find(parent, i)]))
                .ToList();
            if (given.Count == 0) continue;

            took.UnionWith(given.Select(i => Find(parent, i)));
            var glyph = given.Select(i => pieces[i].Width).OrderBy(w => w).ElementAt(given.Count / 2);
            var claimed = new bool[row.Width];
            foreach (var i in given)
                for (var x = pieces[i].Left; x <= pieces[i].Right; x++)
                    claimed[x - row.Left] = true;

            // What the columns did not take is a reading beside a head, or a head whose column was
            // not found: the map breaks a column up, or joins it to its neighbour, and it then gives
            // no piece. MEASURED on vertical-manga-web/mit-c.png at the realtime second size: に and
            // を, the last characters of two columns, are one row, and only に's column is whole
            // enough to be found. So a remainder a character wide, with writing a character's gap
            // under or over it, is given to that writing; anything else goes — a row this close to
            // a column is dropped after reading anyway (VerticalColumnGrouping), and left on the map
            // it is framed into the head beside it.
            for (var start = 0; start < claimed.Length;)
            {
                if (claimed[start]) { start++; continue; }
                var end = start;
                while (end + 1 < claimed.Length && !claimed[end + 1]) end++;
                var (from, to) = (row.Left + start, row.Left + end);
                if (to - from + 1 < glyph || !GiveToWriting(map, labels, width, blobs, row, from, to, glyph))
                    Clear(map, labels, width, row, from, to);
                start = end + 1;
            }

            // A gutter on either side of each head, cleared of whatever lies there but the columns
            // themselves: the next head in the row, a reading beside the head that is a blob of its
            // own, and specks too small to measure that join it to the head once the library
            // dilates. MEASURED on 19 08 59: ひと, three pixels of specks and
            // 人 came back as one box 81 wide, and the column beside it lost its sentence.
            foreach (var i in given)
            {
                var head = pieces[i];
                for (var y = Math.Max(0, row.Top - 1); y <= Math.Min(height - 1, row.Bottom + 1); y++)
                    for (var d = 1; d <= Gutter; d++)
                        foreach (var x in (ReadOnlySpan<int>)[head.Left - d, head.Right + d])
                            if (x >= 0 && x < width && !standing.Contains(labels[y * width + x]))
                                map[y * width + x] = 0;
            }
            changed = true;
        }

        // Each column that took a head is made one blob, down its middle.
        foreach (var root in took)
        {
            var members = columns[root].OrderBy(m => m.Top).ToList();
            for (var k = 1; k < members.Count; k++)
            {
                var (above, below) = (members[k - 1], members[k]);
                if (below.Top <= above.Bottom + 1) continue;
                var x0 = Math.Max(above.Left, below.Left);
                var x1 = Math.Min(above.Right, below.Right);
                if (x1 < x0) continue;
                var quarter = (x1 - x0 + 1) / 4;
                for (var y = above.Bottom + 1; y < below.Top; y++)
                    for (var x = x0 + quarter; x <= x1 - quarter; x++)
                        map[y * width + x] = Math.Max(map[y * width + x], Bridge);
            }
        }
        return changed;
    }

    private static int Across(Piece piece, int left, int right) =>
        Math.Min(piece.Right, right) - Math.Max(piece.Left, left) + 1;

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }
        return i;
    }

    // Bridges what a row holds between two positions to the writing standing a character's gap
    // under or over it, if there is any: a blob that is not itself a row, over most of that width.
    private static bool GiveToWriting(
        float[] map, int[] labels, int width, List<Blob> blobs, Blob row, int from, int to, double glyph)
    {
        if (Ink(labels, width, row, from, to) is not var (top, bottom))
            return false;
        foreach (var other in blobs)
        {
            if (other.Id == row.Id || other.Width >= RowShape * other.Height) continue;
            var x0 = Math.Max(from, other.Left);
            var x1 = Math.Min(to, other.Right);
            if (x1 - x0 + 1 < 0.5 * (to - from + 1)) continue;
            int y0, y1;
            if (other.Top > bottom) (y0, y1) = (bottom + 1, other.Top - 1);
            else if (other.Bottom < top) (y0, y1) = (other.Bottom + 1, top - 1);
            else continue;
            if (y1 - y0 + 1 > glyph) continue;
            var quarter = (x1 - x0 + 1) / 4;
            for (var y = y0; y <= y1; y++)
                for (var x = x0 + quarter; x <= x1 - quarter; x++)
                    map[y * width + x] = Math.Max(map[y * width + x], Bridge);
            return true;
        }
        return false;
    }

    // The first and last line of the row's own pixels between two positions across it.
    private static (int Top, int Bottom)? Ink(int[] labels, int width, Blob row, int from, int to)
    {
        int? top = null, bottom = null;
        for (var y = row.Top; y <= row.Bottom; y++)
            for (var x = from; x <= to; x++)
                if (labels[y * width + x] == row.Id)
                {
                    top ??= y;
                    bottom = y;
                    break;
                }
        return top is { } t && bottom is { } b ? (t, b) : null;
    }

    // Only the row's own pixels: another blob inside its rectangle is not the row's to lose.
    private static void Clear(float[] map, int[] labels, int width, Blob row, int from, int to)
    {
        for (var y = row.Top; y <= row.Bottom; y++)
            for (var x = Math.Max(from, row.Left); x <= Math.Min(to, row.Right); x++)
                if (labels[y * width + x] == row.Id)
                    map[y * width + x] = 0;
    }

    private static List<Blob> Blobs(float[] map, int width, int height, float threshold, int[] labels)
    {
        var blobs = new List<Blob>();
        var stack = new Stack<int>();
        var next = 0;
        for (var start = 0; start < map.Length; start++)
        {
            if (labels[start] != 0 || map[start] <= threshold) continue;

            var id = ++next;
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue, count = 0;
            labels[start] = id;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % width, y = i / width;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                count++;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        var n = ny * width + nx;
                        if (labels[n] != 0 || map[n] <= threshold) continue;
                        labels[n] = id;
                        stack.Push(n);
                    }
            }
            if (count >= MinimumBlob)
                blobs.Add(new Blob(id, left, top, right, bottom));
        }
        return blobs;
    }

    private readonly record struct Blob(int Id, int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
    }

    /// <summary>
    /// A piece of a column: blob <see cref="Blob"/> of its own, or the part of row <see cref="Row"/> over one.
    /// </summary>
    private readonly record struct Piece(int Left, int Top, int Right, int Bottom, int Row, int Blob)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;

        /// <summary>A character's size: the width, or the height of a piece a little taller than wide.</summary>
        public double Glyph => Math.Max(Width, Math.Min(Height, 1.2 * Width));
    }
}
