using OverTranslate.Services;
using Rect = System.Windows.Rect;

namespace OverTranslate.Layout;

/// <summary>
/// Keeps 自動's type size steady: lines that are the same size on screen are drawn at the same
/// size, from one line to the next and from one reading to the next.
/// </summary>
/// <remarks>
/// <para>自動 sizes a line from the height recognition reports for it, and that height is a
/// measurement with a good deal of noise in it. One reported visual novel log (a 950x196 block,
/// one dialogue typeface throughout, 952 lines over four sessions) held heights from 27.1 to
/// 36.8 for the same 30px type — 28.7 to 36.0 between the 10th and 90th percentile — and three
/// lines of one sentence, on screen together, came back 31.2, 28.7 and 35.6. Sized line by line, three out of
/// four lines were drawn at a visibly different size from the one before (718 of 951), and a line
/// read twice changed size by more than 5% one time in five. Shifting a frame by one to three
/// pixels and reading it again moves a line's height by 3–7% at the median and 11% or more at the
/// 90th percentile on each of the Japanese subtitle sets kept, so it is not that one game: it is
/// what the detector's boxes are like.</para>
///
/// <para>So the size is not taken from the one measurement. Every line's height is filed under a
/// size this block has seen — the nearest one within <see cref="SameSizeReach"/> — and drawn at
/// that size's own value, which is the median of the lines filed under it. A median of many
/// readings is as steady as one reading is not: the longest of that report's sessions (329
/// frames, 544 lines of dialogue) settles on 32.4 within ten frames, moves once, to 33.8, and
/// the share of its lines drawn at a different size from the one before falls from 75.0% to
/// 1.5%. What is left is the first few lines of a session, while the median is still being
/// found, and lines that really are another size.</para>
///
/// <para><b>This remembers what the block has shown, and the last thing here that did was thrown
/// out for it</b> — see <see cref="Services.Realtime.CollapsedDetection"/>: a learned line height
/// used to reject boxes, a chat log taught it that text is 13px tall, and every subtitle after
/// that was discarded for good. Three things are different, and each is what that one lacked.
/// Nothing is rejected: the worst this can do to a line is draw it at a neighbouring size. There
/// is no single yardstick: a block holding 13px chat and 55px subtitles holds two sizes, and each
/// line goes to its own. And it cannot be poisoned: a line outside every size's reach starts a
/// size of its own rather than being judged against the others.</para>
///
/// <para><b>Short lines.</b> A CJK line of five characters or fewer is measured about a fifth too
/// tall — in the same session, 40.2 at the median against 32.8 for everything longer, up to 48 —
/// because its box is all there is to measure: nothing clamps it against the character pitch the
/// way a long line is (<see cref="Services.Ocr.ShortTextGlyphHeight"/> does that for a Latin
/// source, and nothing does for this). Left alone, every 「え」 was drawn a size up from the
/// sentence before it; filed like any other line, they founded a size of their own at 41 that
/// then pulled in real sentences measured at 36. So a short line never founds a size a long line
/// can join, does not vote on a long line's size, and reaches further upwards for one to join
/// (<see cref="ShortLineReach"/>). A block of nothing but short lines — a menu — still gets its
/// sizes: short lines file under each other when there is nothing longer to go by.</para>
///
/// <para>The cost is the other side of the same coin: two sizes that really differ by less than
/// the reach are drawn as one. That is the trade being made, and less of one than it sounds: the
/// measurement could not tell those two apart to begin with. Before this, each of them was drawn
/// anywhere within a fifth of itself, changing line by line.</para>
///
/// <para>One of these belongs to one block window, for as long as it lives. Only the type size
/// reads it: what is erased and what is covered still come from the boxes as recognition drew
/// them. Not used for 固定, where the size is the reader's, nor for vertical columns, which are
/// fitted to their balloon rather than sized from a height.</para>
/// </remarks>
internal sealed class RealtimeTextSizes
{
    /// <summary>
    /// How far apart two heights may be, as a ratio, and still be the same size read twice.
    /// </summary>
    /// <remarks>
    /// Measured, not chosen: the session above put 27.1 to 36.8 round a median of 32.8, which is
    /// 1.21 below and 1.12 above, with a few lines out to 41.8 (1.27), and the subtitle sets agree
    /// (49.7–62.3 round 55.1, 41.9–56.6 round 49.2). What matters is the lines left outside,
    /// because each of them founds a size of its own next to the real one, and that size then
    /// takes the real one's lines that happen to read nearer to it. At 1.15 a tenth of the lines
    /// were outside and a third of all lines still changed size; at 1.25 the lines read at 27.1
    /// were, by a hair, and collected a size at 27 that drew in sentences read at 28.7 and 30.3.
    /// At 1.30 neither happens, and going on to 1.33 changed nothing measurable while merging
    /// more of what really differs.
    /// </remarks>
    public const double SameSizeReach = 1.3;

    /// <summary>
    /// How far above a size a short line's height may be and still be that size.
    /// </summary>
    /// <remarks>
    /// The short lines of that session ran 1.22 times the long ones at the median, 1.42 at the
    /// 90th percentile and 1.57 at the most. The application itself, over a dialogue box set in
    /// 30px Yu Gothic, read 「え？」 and 「うん。」 at 44.3 and 50 where the sentences round them
    /// settled on 27.7 — 1.6 and 1.8 — and at a reach of 1.45 those two were the only lines of
    /// the run drawn at another size. Upwards only: nothing measured reads a short line small.
    /// Only where the height is a row's box — a source with a glyph height of its own has had its
    /// short lines corrected already, and measured within 6% of the long ones on the interface set.
    ///
    /// What this gives up is a short line that really is that much larger than the text round
    /// it — a two-character heading over a paragraph. It is drawn at the paragraph's size. Before
    /// this it was drawn at one and a half times its own, for the same reason 「え？」 was.
    /// </remarks>
    public const double ShortLineReach = 1.85;

    /// <summary>Characters from which a line's height is believed as much as any other's.</summary>
    /// <remarks>
    /// Where the measurement itself changes: in the same session lines of two to five characters
    /// had medians of 39.4 to 43.5, and from six up every length sat between 32.0 and 33.9.
    /// </remarks>
    public const int ReliableFromGlyphs = 6;

    /// <summary>
    /// How far a size's median has to drift from the value being drawn before the value follows.
    /// </summary>
    /// <remarks>
    /// Below this the eye cannot tell — a 32px line against a 33px one — and without it a median
    /// resting between two readings would hand every new line the other of the two.
    /// </remarks>
    public const double Deadband = 1.04;

    /// <summary>Distinct lines a size keeps the heights of.</summary>
    /// <remarks>
    /// The median of nine wandered past the deadband all session long, and a fifth of the lines
    /// changed size with it. Of forty it moved six times across the four sessions of that report;
    /// of eighty, twice. Longer still buys the last two and costs how soon a block that really
    /// has changed its type is followed, which is about half of this many lines. Counted in
    /// distinct lines, so a sentence that stays on screen for a hundred readings is still one vote.
    /// </remarks>
    public const int Memory = 80;

    /// <summary>Lines a size needs before it is preferred to one that has fewer.</summary>
    /// <remarks>
    /// A size founded by one stray reading sits right beside the size that reading belonged to,
    /// and is the nearer of the two for anything else that strays the same way. One line is a
    /// reading; three that agree are a size.
    /// </remarks>
    public const int EstablishedFromLines = 3;

    /// <summary>Lines from which a size's value holds still inside the deadband.</summary>
    /// <remarks>
    /// Until then the value is simply the median, however little it has moved. The first line of
    /// a session is one reading, and the size it founds is that reading: held to the deadband
    /// from the start, the value sat on it until the median had drifted 4% away and then jumped —
    /// 25.9, 27.0, 28.2 over the first six sentences of a run with the application, two steps a
    /// reader can see. Following the median while it is still being found takes the same road in
    /// steps that are mostly too small to see, and the deadband takes over once there is a median
    /// worth holding to.
    /// </remarks>
    public const int SettledFromLines = 8;

    /// <summary>Sizes remembered at once; the one unused longest goes first.</summary>
    public const int MaxSizes = 8;

    /// <summary>One line's height, as recognition reported it.</summary>
    /// <param name="Key">What tells this line from another: its source text.</param>
    /// <param name="Height">The height its type would be sized from.</param>
    /// <param name="Glyphs">Characters in it that take up width.</param>
    /// <param name="FromRowBox">
    /// The height is that of the line's box rather than a glyph height estimated for it.
    /// </param>
    public readonly record struct Reading(string Key, double Height, int Glyphs, bool FromRowBox);

    private sealed class Size(double value, bool reliable)
    {
        public double Value = value;
        public readonly bool Reliable = reliable;
        public long Seen;

        // Oldest first. A line read again moves to the end with its newest height.
        public readonly List<(string Key, double Height)> Lines = [];

        public void Vote(string key, double height)
        {
            int at = Lines.FindIndex(line => line.Key == key);
            if (at >= 0) Lines.RemoveAt(at);
            Lines.Add((key, height));
            if (Lines.Count > Memory) Lines.RemoveRange(0, Lines.Count - Memory);
        }

        public bool Established => Lines.Count >= EstablishedFromLines;

        /// <summary>
        /// Moves the value to the median: always while the size is still being found, and after
        /// that once the median has plainly left it.
        /// </summary>
        public void Follow()
        {
            double median = Median();
            if (Lines.Count < SettledFromLines || Math.Max(median / Value, Value / median) > Deadband)
                Value = median;
        }

        private double Median()
        {
            var heights = Lines.Select(line => line.Height).Order().ToArray();
            int middle = heights.Length / 2;
            return heights.Length % 2 == 1 ? heights[middle] : (heights[middle - 1] + heights[middle]) / 2;
        }
    }

    private readonly List<Size> _sizes = [];
    private long _pass;

    /// <summary>
    /// The height to size each line from, in the order given. A reading that is not a height at
    /// all — zero, negative, not a number — comes back as it went in.
    /// </summary>
    public double[] Settle(IReadOnlyList<Reading> readings)
    {
        _pass++;
        var filed = new Size?[readings.Count];
        var voted = new HashSet<Size>();

        // The long lines first, so a short line can join a size a long one founded in this same
        // pass — the first frame of a session is as likely as any to hold both.
        var order = Enumerable.Range(0, readings.Count)
            .OrderBy(index => readings[index].Glyphs >= ReliableFromGlyphs ? 0 : 1);
        foreach (int index in order)
        {
            var reading = readings[index];
            if (!double.IsFinite(reading.Height) || reading.Height <= 0) continue;

            bool reliable = reading.Glyphs >= ReliableFromGlyphs;
            Size? size;
            bool votes = true;
            if (reliable)
            {
                size = Nearest(reading.Height, reliable: true, SameSizeReach);
            }
            else
            {
                size = Nearest(reading.Height, reliable: true, reading.FromRowBox ? ShortLineReach : SameSizeReach);
                // Drawn at the long lines' size, with no say in what that size is.
                if (size is not null) votes = false;
                else size = Nearest(reading.Height, reliable: false, SameSizeReach);
            }

            if (size is null)
            {
                size = new Size(reading.Height, reliable);
                _sizes.Add(size);
            }

            if (votes)
            {
                size.Vote(reading.Key, reading.Height);
                voted.Add(size);
            }
            size.Seen = _pass;
            filed[index] = size;
        }

        foreach (var size in voted) size.Follow();

        Merge(filed);
        Forget();

        return [.. readings.Select((reading, index) => filed[index]?.Value ?? reading.Height)];
    }

    /// <summary>
    /// The size of that kind closest to <paramref name="height"/>, no further below it than
    /// <see cref="SameSizeReach"/> and no further above than <paramref name="reachAbove"/> — an
    /// established one if any is in reach, whatever else is nearer.
    /// </summary>
    private Size? Nearest(double height, bool reliable, double reachAbove)
    {
        Size? nearest = null;
        double distance = double.PositiveInfinity;
        foreach (var size in _sizes)
        {
            if (size.Reliable != reliable) continue;
            double ratio = height / size.Value;
            if (ratio > reachAbove || ratio < 1 / SameSizeReach) continue;
            double away = Math.Abs(Math.Log(ratio));
            bool better = nearest is null
                || (size.Established && !nearest.Established)
                || (size.Established == nearest.Established && away < distance);
            if (!better) continue;
            nearest = size;
            distance = away;
        }
        return nearest;
    }

    /// <summary>
    /// Two sizes whose values have come within reach of each other are one size that was founded
    /// twice — by the low end of its readings and by the high end, before either had a median to
    /// speak of. The one with fewer lines is folded into the other.
    /// </summary>
    private void Merge(Size?[] filed)
    {
        while (true)
        {
            var pair = (
                from a in _sizes
                from b in _sizes
                where !ReferenceEquals(a, b) && a.Reliable == b.Reliable
                    && a.Value <= b.Value && b.Value / a.Value <= SameSizeReach
                select (a, b)).FirstOrDefault();
            if (pair.a is null) return;

            var (kept, folded) = pair.a.Lines.Count >= pair.b.Lines.Count ? (pair.a, pair.b) : (pair.b, pair.a);
            // Older than anything the kept size holds only if it is not there already: the same
            // line under both keeps the height the kept size has for it.
            foreach (var (key, height) in folded.Lines)
                if (!kept.Lines.Exists(line => line.Key == key)) kept.Lines.Insert(0, (key, height));
            if (kept.Lines.Count > Memory) kept.Lines.RemoveRange(0, kept.Lines.Count - Memory);
            kept.Follow();
            kept.Seen = Math.Max(kept.Seen, folded.Seen);

            _sizes.Remove(folded);
            for (int index = 0; index < filed.Length; index++)
                if (ReferenceEquals(filed[index], folded)) filed[index] = kept;
        }
    }

    private void Forget()
    {
        while (_sizes.Count > MaxSizes)
            _sizes.Remove(_sizes.MinBy(size => size.Seen)!);
    }

    /// <summary>
    /// The lines as the type is to be sized from them: each with its height replaced by its size's.
    /// </summary>
    /// <remarks>
    /// <para>A line carrying a glyph height of its own gets the settled one in
    /// <see cref="TranslatedBlock.FontGlyphHeight"/>, which is what the font reads first, and its
    /// rows scaled by as much as that moved. A line without one is sized from its rows, so every
    /// row is given the settled height, about its own centre. Either way the rows of one block come
    /// out the same height: they are one paragraph in one typeface, and a panel sets the whole
    /// paragraph by its shortest row.</para>
    ///
    /// <para>A row that is nowhere near the block's median is left alone — whatever it is, it is
    /// not the same size read badly. A block of more than one row keeps its
    /// <see cref="TranslatedBlock.Bounds"/>, which is where the paragraph is and not how large its
    /// type is. <see cref="TranslatedBlock.RenderGlyphHeight"/> is not touched either: it is what a
    /// line across is covered by. A line off a tilted card keeps all its geometry and takes only
    /// the glyph height.</para>
    ///
    /// <para>A line that does not move comes back as the object it was, and so do the lines this
    /// has no business with — a vertical column, one with nothing to draw.</para>
    /// </remarks>
    /// <param name="vertical">
    /// The block is vertical writing: only what the reading stage marked as running across is sized.
    /// </param>
    public IReadOnlyList<TranslatedBlock> Settle(IReadOnlyList<TranslatedBlock> lines, bool vertical)
    {
        var readings = new List<Reading>(lines.Count);
        var measured = new List<(int Index, double Height, double RowMedian, bool FromRowBox)>(lines.Count);
        for (int index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line.TranslatedText)) continue;
            if (vertical && !line.RunsAcross) continue;

            double rowMedian = Median((line.SourceLineBounds is { Count: > 0 } rows ? rows : [line.Bounds])
                .Select(row => row.Height));
            double? glyph = line.FontGlyphHeight is > 0 ? line.FontGlyphHeight
                : line.RenderGlyphHeight is > 0 ? line.RenderGlyphHeight : null;
            // A tilted line's rows are upright boxes round slanted text: not a height to size from.
            if (glyph is null && line.Tilt is not null) continue;

            double height = glyph ?? rowMedian;
            if (!double.IsFinite(height) || height <= 0) continue;

            readings.Add(new Reading(
                line.OriginalText, height, line.OriginalText.Count(c => !char.IsWhiteSpace(c)), glyph is null));
            measured.Add((index, height, rowMedian, glyph is null));
        }

        if (readings.Count == 0) return lines;

        var settled = Settle(readings);
        TranslatedBlock[]? result = null;
        for (int at = 0; at < measured.Count; at++)
        {
            var (index, height, rowMedian, fromRowBox) = measured[at];
            var resized = Resized(lines[index], height, settled[at], rowMedian, fromRowBox);
            if (ReferenceEquals(resized, lines[index])) continue;

            result ??= [.. lines];
            result[index] = resized;
        }

        return result ?? lines;
    }

    /// <summary>
    /// <paramref name="line"/> with <paramref name="settled"/> in place of the
    /// <paramref name="height"/> it was read at, or the line itself where that changes nothing.
    /// </summary>
    private static TranslatedBlock Resized(
        TranslatedBlock line, double height, double settled, double rowMedian, bool fromRowBox)
    {
        var resized = fromRowBox || settled == height ? line : line with { FontGlyphHeight = settled };
        if (line.Tilt is not null) return resized;

        // Asked for even when the height itself has not moved: a paragraph whose median happens
        // to be the settled size still has rows either side of it, and its shortest row is what a
        // panel sets the whole of it by.
        double rowHeight = rowMedian * settled / height;
        if (!double.IsFinite(rowHeight) || rowHeight <= 0) return resized;

        bool moved = false;
        Rect Row(Rect row)
        {
            if (row.Height <= 0 || row.Height == rowHeight
                || Math.Max(row.Height / rowMedian, rowMedian / row.Height) > SameSizeReach)
                return row;
            moved = true;
            return new Rect(row.X, row.Y + (row.Height - rowHeight) / 2, row.Width, rowHeight);
        }

        if (line.SourceLineBounds is { Count: > 1 } rows)
        {
            IReadOnlyList<Rect> evened = [.. rows.Select(Row)];
            return moved ? resized with { SourceLineBounds = evened } : resized;
        }

        var bounds = Row(line.Bounds);
        IReadOnlyList<Rect>? single = line.SourceLineBounds is null ? null : [.. line.SourceLineBounds.Select(Row)];
        return moved ? resized with { Bounds = bounds, SourceLineBounds = single } : resized;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
