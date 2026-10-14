using System.Drawing;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>Which way the writing in a block looks to run, from its ink.</summary>
internal enum MangaWriting
{
    /// <summary>Nothing to go on: the box's shape decides, as it always has.</summary>
    Unsure,
    /// <summary>Three long lines or more across the page, one under the other.</summary>
    Across,
    /// <summary>Three columns or more down the page, side by side.</summary>
    Down,
}

/// <summary>
/// Whether a block's ink looks set the other way from what the shape of its box says — a
/// suspicion, cheap enough to ask of every block, that the column pipeline then settles (see
/// <see cref="MangaPageLayout.Suspect"/>).
/// </summary>
/// <remarks>
/// <para>The shape of the box is what the manga path goes by (<see cref="MangaPageLayout.AcrossRatio"/>),
/// and it is right for nearly every block: a column is tall, a name plate is wide. It was wrong for
/// both blocks of vertical-image-ja5. A caption of nine lines across in a box 495×381 is too tall
/// for a row, so it was taken for columns, read by manga-ocr as one string — sentences that are
/// not on the page — and set down the page. A balloon of sixteen short columns is 464×182; framed
/// a few pixels differently or captured at another scale the detector makes it 2.48–2.88 times as
/// wide as tall, either side of the 2.5 that makes a row, and manga-ocr, squeezing it into
/// 224×224, turned its last sentences into hundreds of dots every time. The translation came out
/// one long line across or, below 2.5, a column grid a third of the source's size.</para>
///
/// <para>Lines of writing leave paper between them. Counted along each row (or column) as changes
/// between ink and paper, as <see cref="MangaTextRows"/> counts them, the lines are bands with
/// clear paper between, and along each band the ink runs on, glyph after glyph, many times the
/// band's thickness. The same counts are not enough to say which way a block runs: columns whose
/// characters line up make rows of paper between the characters too, and a row band through them
/// runs as long as a line would. What they do well is find the few blocks worth a second look, and
/// that is all they are asked here.</para>
/// </remarks>
// MEASURED on the 1767 blocks the manga detector found on the vertical and comic corpora (205
// pages) plus ja5 at 14 framings and scales (0.75–1.5): Across is said of the ja5 caption at every
// one, of four English balloons, and of one block of columns (最早人類の制御下に…, zang), which the
// column pipeline then reads as columns; Down only of the ja5 balloon, at every one. A bar of 4 on
// a line's run instead of 8 also suspects 13 more blocks of columns; 6, one more. Below 0.4 of a
// line between lines or below three lines, dozens of columns: two characters side by side in two
// columns make two short "lines" with a gap.
internal static class MangaWritingDirection
{
    // As LumaPage and MangaTextRows take it: paper lighter than this, ink darker; on a dark block
    // the other way round.
    private const int InkLevel = 128;

    // A row (or column) is writing when it changes between ink and paper at least this share as
    // often as the busiest one — MangaTextRows' bar.
    private const double WritingShare = 0.2;

    // Rows of writing this close are one band: the gap inside a glyph, not between lines.
    private const int SameBand = 2;

    // A band this much of the thickest one's thickness or more is a line; thinner is furigana, a
    // stroke of punctuation, the tail of a kana.
    private const double LineShare = 0.6;

    private const int FewestLines = 3;

    // Between two lines, the median gap is at least this share of a line's thickness.
    private const double LineGap = 0.4;

    // Along the median line the ink runs on for at least this many times the line's thickness, a
    // gap of up to one thickness counting as part of the run. A line across a caption runs the
    // width of the box; a column in a balloon is a short sentence, five or six characters long.
    private const double AcrossRun = 8;
    private const double DownRun = 4;

    // Long columns: two or more, between these shares of a column's thickness apart, each running
    // at least this many characters down. See LongColumns.
    private const int FewestLongColumns = 2;
    private const double LongColumnGap = 0.25;
    private const double LongColumnWidestGap = 1;
    private const double LongColumnRun = 18;

    internal static MangaWriting Suspected(LumaPage luma, RectangleF block)
    {
        if (Inside(luma, block) is not { } box) return MangaWriting.Unsure;

        bool light = luma.Median(box) >= InkLevel;
        bool across = Lines(luma, box, light, alongRows: true, FewestLines, LineGap, double.MaxValue, AcrossRun);
        bool down = Lines(luma, box, light, alongRows: false, FewestLines, LineGap, double.MaxValue, DownRun);
        return across == down ? MangaWriting.Unsure : across ? MangaWriting.Across : MangaWriting.Down;
    }

    /// <summary>
    /// Whether the block is two columns or more of eighteen characters or more each — longer than
    /// manga-ocr reads.
    /// </summary>
    /// <remarks>
    /// <para>The ink is the measure, not the box: a box is drawn round furigana and a margin, and a
    /// column's length in its own thickness is a count of characters whatever the scale.
    /// vertical-image-ja5's narration — two columns of 19–20 characters in a box 6.95 times as
    /// tall as wide, three in one 5.76 — comes in under <see cref="MangaPageLayout.LongBlockRatio"/>,
    /// and manga-ocr read it as sentences that are not on the page (大手前座業者もダミもAAAIルディング、
    /// 不倫関係という教会知られているし) while the column pipeline read it almost word for word.
    /// Lowering the ratio instead was measured before: at 4 it sent six good ja2 balloons away and
    /// lost two sentences.</para>
    ///
    /// <para>Close, too: no more than a column's thickness apart, as typeset narration is.</para>
    /// </remarks>
    // MEASURED over the 182 readable vertical and comic pages, against round one's output: the
    // ja5 narration runs 21.3–21.9 characters at every scale from 0.5 to 1.3, with gaps of 0.29–0.36;
    // the longest columns of any balloon are 16.7 (ja2 助けてくださりありがとうございました!). Without
    // the widest gap two more blocks went to the column pipeline: ossan's title strip (gap 1.21),
    // read correctly there where manga-ocr garbled it, and Aisazu's hand-lettered その3 title
    // (gap 1.54), of whose three columns the column pipeline kept one. With it, ja5 is the only page
    // whose output changes.
    internal static bool LongColumns(LumaPage luma, RectangleF block) =>
        Inside(luma, block) is { } box &&
        Lines(
            luma, box, luma.Median(box) >= InkLevel, alongRows: false,
            FewestLongColumns, LongColumnGap, LongColumnWidestGap, LongColumnRun);

    private static Rectangle? Inside(LumaPage luma, RectangleF block)
    {
        int left = Math.Clamp((int)block.Left, 0, luma.Width), top = Math.Clamp((int)block.Top, 0, luma.Height);
        int right = Math.Clamp((int)block.Right, left, luma.Width), bottom = Math.Clamp((int)block.Bottom, top, luma.Height);
        var box = Rectangle.FromLTRB(left, top, right, bottom);
        return box.Width < 2 || box.Height < 2 ? null : box;
    }

    // Whether the block's rows (alongRows) or columns fall into lines of writing.
    private static bool Lines(
        LumaPage luma, Rectangle box, bool light, bool alongRows, int fewest, double gap, double widestGap,
        double run)
    {
        int count = alongRows ? box.Height : box.Width, length = alongRows ? box.Width : box.Height;
        var changes = new int[count];
        for (int i = 0; i < count; i++)
        {
            bool before = Ink(0, i);
            for (int j = 1; j < length; j++)
            {
                bool now = Ink(j, i);
                if (now != before) changes[i]++;
                before = now;
            }
        }

        int busiest = changes.Max();
        if (busiest <= 0) return false;

        double bar = Math.Max(2, WritingShare * busiest);
        var bands = new List<(int Start, int End)>();
        for (int i = 0, start = -1; i <= count; i++)
        {
            bool writing = i < count && changes[i] >= bar;
            if (writing && start < 0) start = i;
            if (writing || start < 0) continue;

            if (bands.Count > 0 && start - bands[^1].End < SameBand) bands[^1] = (bands[^1].Start, i);
            else bands.Add((start, i));
            start = -1;
        }

        int thickest = bands.Max(band => band.End - band.Start);
        var lines = bands.Where(band => band.End - band.Start >= LineShare * thickest).ToList();
        if (lines.Count < fewest) return false;

        double typical = Median(lines.Select(line => (double)(line.End - line.Start)));
        var gaps = new List<double>();
        for (int i = 1; i < lines.Count; i++) gaps.Add((lines[i].Start - lines[i - 1].End) / typical);
        double typicalGap = Median(gaps);
        if (typicalGap < gap || typicalGap > widestGap) return false;

        return Median(lines.Select(Run)) >= run;

        // The longest stretch along a line that is ink with gaps no wider than the line is thick,
        // in thicknesses.
        double Run((int Start, int End) line)
        {
            int thickness = line.End - line.Start, longest = 0, from = 0, lastInk = int.MinValue / 2;
            for (int j = 0; j < length; j++)
            {
                bool any = false;
                for (int i = line.Start; i < line.End && !any; i++) any = Ink(j, i);
                if (!any) continue;
                if (j - lastInk > thickness) from = j;
                lastInk = j;
                longest = Math.Max(longest, j - from + 1);
            }

            return (double)longest / thickness;
        }

        // Position j along line i.
        bool Ink(int j, int i)
        {
            byte value = alongRows ? luma[box.Left + j, box.Top + i] : luma[box.Left + i, box.Top + j];
            return light ? value < InkLevel : value >= InkLevel;
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
