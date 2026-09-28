namespace OverTranslate.Services.Ocr;

/// <summary>
/// The detector's probability map for one detection, kept after the library has turned it into
/// boxes, so a box it merged can be taken apart again without running the model a second time.
/// </summary>
/// <remarks>
/// <para>What it is for: columns written straight onto the artwork of a comic page — narration over
/// a landscape, a line on a screentone floor. MEASURED on
/// <c>.ai/test-images/vertical-image-ja3/ossan-boukensha-no-okureta-eiyuutan-chapter-23/003</c>: the
/// model finds every one of those columns, clearly and separately, and none of them is read. The
/// library's post-processing thresholds the map at 0.2 and then DILATES it by a pixel, and the
/// furigana standing between two columns is enough to join them: <c>誰かが</c>, <c>気付いてくれれば</c>,
/// <c>戦いに備えられる</c> and their readings come back as one 153x282 box, <c>それまで</c> and
/// <c>時間を稼ぐ</c> as one 196x210. A crop two columns wide reads as nothing. Lowering the thresholds
/// does not help — at 0.05 those boxes are still merged — because the map was never the problem.</para>
///
/// <para>A balloon has the same merge and <see cref="VerticalColumnDetection"/> has always cut it
/// apart, by projecting the PIXELS: a flat background and blank gutters between the columns. The
/// artwork is exactly where that fails — no colour covers 60% of the box, and there is no blank
/// gutter on a landscape. The map does not care what is behind the writing: it is high on text and
/// low everywhere else, so the gutter is there whatever the background is.</para>
///
/// <para>The box for each column is rebuilt the way the library builds its own: the kernel's
/// rectangle offset by DB's unclip distance (area × ratio ÷ perimeter) plus the pixel the dilation
/// adds. Checked against the library's boxes on single columns it did not merge — the kernel of
/// <c>目ぼしいものはない</c> is 14x189 in the map and the library's box 34x209, which is that formula.</para>
/// </remarks>
internal sealed class DetectorProbability
{
    /// <summary>
    /// How much ink a strip must carry, against the heaviest in the box, to count as a column of its
    /// own rather than the furigana beside one.
    /// </summary>
    /// <remarks>
    /// <para>A box holding one column and its reading is not a merge and is left as the library drew
    /// it: cutting the reading away makes the column's box tighter than grouping was measured on, and
    /// grouping then breaks the balloon. MEASURED on <c>vertical-image-ja2/2026-09-20 19 14 56.png</c>:
    /// cut, <c>到達していない</c> loses its reading <c>とうたつ</c>, its box narrows from 52 to 33, and
    /// the narration it belongs to comes apart in two.</para>
    ///
    /// <para>Width does not tell the two apart — the map is thin and broken on a small reading, so the
    /// positions that are on for a third of its length number 7 against 11 for its column. Ink per unit
    /// of length does: that reading carries 0.59 of its column, and the merged columns on the pages
    /// this exists for carry 0.74 to 0.82 of each other. Over ja3 and ja2, 0.60 and 0.67 give the same
    /// result in every sentence and 0.75 begins to lose one; this is the upper end of the flat part.</para>
    /// </remarks>
    private const double ColumnInkShare = 0.67;

    /// <summary>
    /// The least a strip may fill of its own rectangle and still be writing.
    /// </summary>
    /// <remarks>
    /// Scribbled artwork lights the map too — thinly, and over an area. MEASURED on
    /// <c>vertical-image-ja3/…-chapter-432/007</c> read at the realtime second size: the foliage of a
    /// tree makes a 62x102 strip that fills 0.22 of itself, is counted as a column, and the part cut
    /// for it reads as <c>82</c> and joins two neighbouring lines into one translation. Columns fill
    /// 0.34 and up, even one carrying a stray piece at its head; 0.25 and 0.30 measure the same.
    /// </remarks>
    private const double ColumnFill = 0.3;

    /// <summary>A position across a strip is part of its column when it is on for this much of its length.</summary>
    private const double CoreLength = 0.3;

    /// <summary>Blobs smaller than this, in map pixels, are specks and are not measured.</summary>
    private const int MinimumBlob = 12;

    private readonly float[] _values;
    private readonly int _width;
    private readonly int _height;
    private readonly float _scaleX;
    private readonly float _scaleY;
    private readonly float _threshold;
    private readonly float _scoreThreshold;
    private readonly float _unclipRatio;

    /// <param name="values">The map, row by row, <paramref name="width"/> × <paramref name="height"/>.</param>
    /// <param name="scaleX">Map pixels per detector-bitmap pixel across, as the library's ScaleParam has it.</param>
    /// <param name="scaleY">Map pixels per detector-bitmap pixel down.</param>
    /// <param name="threshold">The library's binarisation threshold (BoxThresh).</param>
    /// <param name="scoreThreshold">The library's box score floor (BoxScoreThresh).</param>
    /// <param name="unclipRatio">The library's unclip ratio.</param>
    internal DetectorProbability(
        float[] values, int width, int height, float scaleX, float scaleY,
        float threshold, float scoreThreshold, float unclipRatio)
    {
        _values = values;
        _width = width;
        _height = height;
        _scaleX = scaleX;
        _scaleY = scaleY;
        _threshold = threshold;
        _scoreThreshold = scoreThreshold;
        _unclipRatio = unclipRatio;
    }

    /// <summary>
    /// The columns standing side by side inside a box, in the detector bitmap's coordinates, or
    /// nothing when the box does not hold at least two.
    /// </summary>
    /// <remarks>
    /// Strips are formed along x only: a column the map breaks into one blob per glyph is still one
    /// column, because its pieces overlap across. A strip is a column when it is half again as long as
    /// its core is wide, which keeps a horizontal line broken into glyphs from being cut into single
    /// characters. Every strip is returned once two columns are found — furigana included, as
    /// <see cref="VerticalColumnDetection"/> keeps a narrow part rather than dropping it.
    /// </remarks>
    internal IReadOnlyList<Column> ColumnsIn(int left, int top, int right, int bottom)
    {
        var x0 = Math.Max(0, (int)MathF.Floor(left * _scaleX));
        var y0 = Math.Max(0, (int)MathF.Floor(top * _scaleY));
        var x1 = Math.Min(_width, (int)MathF.Ceiling(right * _scaleX));
        var y1 = Math.Min(_height, (int)MathF.Ceiling(bottom * _scaleY));
        if (x1 <= x0 || y1 <= y0)
            return [];

        var strips = new List<Strip>();
        foreach (var blob in Blobs(x0, y0, x1 - x0, y1 - y0))
        {
            // A blob can bridge two strips found before it, so a join is checked again.
            var joined = blob;
            int at;
            while ((at = strips.FindIndex(s => joined.Left <= s.Right && joined.Right >= s.Left)) >= 0)
            {
                joined = joined.Join(strips[at]);
                strips.RemoveAt(at);
            }
            strips.Add(joined);
        }

        var columns = strips
            .Select(s => (Strip: s, Core: CoreWidth(s), Ink: InkPerRow(s)))
            .Where(s => s.Core > 0 && s.Strip.Height >= s.Core * 1.5 && s.Ink / s.Strip.Width >= ColumnFill)
            .ToList();
        if (columns.Count < 2)
            return [];
        var heaviest = columns.Max(s => s.Ink);
        if (columns.Count(s => s.Ink >= heaviest * ColumnInkShare) < 2)
            return [];

        return strips
            .OrderBy(s => s.Left)
            .Select(s =>
            {
                // DB's unclip on the kernel's rectangle, and the dilation's pixel on top of it.
                var offset = (float)s.Width * s.Height * _unclipRatio / (2f * (s.Width + s.Height)) + 1;
                return new Column(
                    Math.Max(left, (int)((s.Left - offset) / _scaleX)),
                    Math.Max(top, (int)((s.Top - offset) / _scaleY)),
                    Math.Min(right, (int)((s.Right + 1 + offset) / _scaleX)),
                    Math.Min(bottom, (int)((s.Bottom + 1 + offset) / _scaleY)),
                    (int)(s.Left / _scaleX),
                    (int)(s.Top / _scaleY),
                    (int)((s.Right + 1) / _scaleX),
                    (int)((s.Bottom + 1) / _scaleY));
            })
            .ToList();
    }

    // Eight-connected, as the library's contour search is, and scored the way it scores a box: the
    // mean probability over the blob. Specks are left out before anything is measured from them.
    private IEnumerable<Strip> Blobs(int x0, int y0, int w, int h)
    {
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (var start = 0; start < seen.Length; start++)
        {
            if (seen[start] || !On(start)) continue;

            var strip = new Strip(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
            var count = 0;
            var sum = 0.0;
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % w, y = i / w;
                strip = strip.Take(x0 + x, y0 + y);
                count++;
                sum += _values[(y0 + y) * _width + x0 + x];
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        var n = ny * w + nx;
                        if (seen[n] || !On(n)) continue;
                        seen[n] = true;
                        stack.Push(n);
                    }
            }

            if (count >= MinimumBlob && sum / count >= _scoreThreshold)
                yield return strip;
        }

        bool On(int i) => _values[(y0 + i / w) * _width + x0 + i % w] > _threshold;
    }

    // How much of the map is on per row of the strip: a column's ink, which a reading beside it
    // carries much less of, and a stray piece at one end hardly changes.
    private double InkPerRow(Strip s)
    {
        var on = 0;
        for (var x = s.Left; x <= s.Right; x++)
            for (var y = s.Top; y <= s.Bottom; y++)
                if (_values[y * _width + x] > _threshold) on++;
        return (double)on / s.Height;
    }

    // How wide the strip is where it is a column: the positions across it that are on for a good
    // part of its length. A reading or a stray piece at one end widens the rectangle, not this.
    private int CoreWidth(Strip s)
    {
        var core = 0;
        for (var x = s.Left; x <= s.Right; x++)
        {
            var on = 0;
            for (var y = s.Top; y <= s.Bottom; y++)
                if (_values[y * _width + x] > _threshold) on++;
            if (on >= s.Height * CoreLength) core++;
        }
        return core;
    }

    /// <summary>
    /// A column's box, built as the library builds one, and the kernel it was built from — the part
    /// of the map that is actually on, which is what says whose ink this is.
    /// </summary>
    internal readonly record struct Column(
        int Left, int Top, int Right, int Bottom,
        int KernelLeft, int KernelTop, int KernelRight, int KernelBottom);

    private readonly record struct Strip(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;

        public Strip Take(int x, int y) =>
            new(Math.Min(Left, x), Math.Min(Top, y), Math.Max(Right, x), Math.Max(Bottom, y));

        public Strip Join(Strip other) =>
            new(Math.Min(Left, other.Left), Math.Min(Top, other.Top),
                Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }
}
