using System.Drawing;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// How many lines a block of writing across the page was set in, read off its ink: the manga
/// detector draws one box round a whole caption, and manga-ocr reads it as one string.
/// </summary>
/// <remarks>
/// <para>A caption of three lines came back as one block with no lines in it, and both overlays
/// took that for one line of source: the translation was squeezed onto a single line across the
/// box, at a third of the size of the type it replaced, with the lower two thirds of the box left
/// empty. Given its lines, it is the group the horizontal pipeline would have made of it, and is
/// drawn the way that one is.</para>
///
/// <para>The lines are the rows of the box that the writing crosses, counted along each row as
/// changes between ink and paper — not as an amount of ink, which a band of tone or a panel's edge
/// caught in the box has more of than any line of text. Furigana is set between the lines in rows
/// of its own, and is told from a line by its height. Anything this cannot be sure of is one line,
/// as it was: cutting one line in two is the failure that matters, missing a second line only
/// leaves the block as it was before.</para>
/// </remarks>
// MEASURED: over the 33 blocks the manga detector set across on the 176 vertical pages, the five
// that hold two or three lines of one size are counted right and none of the 28 single lines is
// cut — nor is any of them when the page is scaled to 0.5, 0.75 or 1.25 or the box is moved 2–3px,
// where the one miss is the two-line chapter-end strip at half size. Asked of the 2265 single
// lines on the 336 horizontal pages, it cuts none; of their 377 groups of two lines or more, it
// never counts more lines than there are.
internal static class MangaTextRows
{
    // Paper is lighter than this and ink darker, as LumaPage takes it; on a dark page the other way.
    private const int InkLevel = 128;

    // A row is writing when it changes between ink and paper at least this share as often as the
    // busiest row does. 0.1 and 0.15 lost the chapter-end strip's single line to the tone above it
    // at some scales; 0.25 began to find lines in a page of hand-lettered columns.
    private const double RowShare = 0.2;

    // Rows of writing this close together are one band: the gap inside a glyph, not between lines.
    private const int SameBand = 2;

    // A band this much of the tallest one's height or more is a line; less is furigana.
    private const double LineShare = 0.6;

    // A line whose rows are more ink than paper is not a line of writing but a ground it stands on —
    // the page beside a dark strip, a band of speed lines.
    private const double MostlyInk = 0.4;

    // Two lines stand at least this share of a line's height apart.
    private const double LineGap = 0.25;

    // The shortest line, in pixels, that is believed.
    private const int ShortestLine = 4;

    /// <summary>
    /// The block's lines from top to bottom, as page rows; empty when it is one line, or when that
    /// cannot be told.
    /// </summary>
    /// <param name="characters">How many characters were read in the block.</param>
    internal static IReadOnlyList<(int Top, int Bottom)> Find(LumaPage luma, RectangleF block, int characters)
    {
        int left = Math.Clamp((int)block.Left, 0, luma.Width), top = Math.Clamp((int)block.Top, 0, luma.Height);
        int right = Math.Clamp((int)block.Right, left, luma.Width), bottom = Math.Clamp((int)block.Bottom, top, luma.Height);
        int width = right - left, height = bottom - top;
        if (width < 2 || height <= 0) return [];

        bool light = luma.Median(Rectangle.FromLTRB(left, top, right, bottom)) >= InkLevel;
        var changes = new int[height];
        var ink = new int[height];
        for (int y = 0; y < height; y++)
        {
            bool before = Ink(left, top + y);
            if (before) ink[y]++;
            for (int x = left + 1; x < right; x++)
            {
                bool now = Ink(x, top + y);
                if (now) ink[y]++;
                if (now != before) changes[y]++;
                before = now;
            }
        }

        int busiest = changes.Max();
        if (busiest <= 0) return [];

        double bar = Math.Max(2, RowShare * busiest);
        var bands = new List<(int Top, int Bottom)>();
        for (int y = 0, start = -1; y <= height; y++)
        {
            bool writing = y < height && changes[y] >= bar;
            if (writing && start < 0) start = y;
            if (writing || start < 0) continue;

            if (bands.Count > 0 && start - bands[^1].Bottom < SameBand) bands[^1] = (bands[^1].Top, y);
            else bands.Add((start, y));
            start = -1;
        }

        if (bands.Count == 0) return [];
        int tallest = bands.Max(band => band.Bottom - band.Top);
        var lines = bands.Where(band => band.Bottom - band.Top >= LineShare * tallest).ToList();

        if (lines.Count < 2) return [];
        if (lines.Any(line => Share(ink, line) > MostlyInk * width)) return [];

        var heights = lines.Select(line => line.Bottom - line.Top).Order().ToList();
        int typical = heights[heights.Count / 2];
        if (heights[0] < ShortestLine) return [];
        // A line against the edge of the box may run on past it: the box was not drawn round it.
        if (lines[0].Top == 0 || lines[^1].Bottom == height) return [];
        for (int i = 1; i < lines.Count; i++)
            if (lines[i].Top - lines[i - 1].Bottom < LineGap * typical) return [];
        // Each line holds two characters at least, and its busiest row crosses one stroke of each.
        if (characters < 2 * lines.Count) return [];
        double perLine = (double)characters / lines.Count;
        if (lines.Any(line => Busiest(changes, line) < perLine)) return [];

        return [.. lines.Select(line => (top + line.Top, top + line.Bottom))];

        bool Ink(int x, int y) => light ? luma[x, y] < InkLevel : luma[x, y] >= InkLevel;
    }

    // The mean of a band's per-row ink counts.
    private static double Share(int[] ink, (int Top, int Bottom) band)
    {
        double sum = 0;
        for (int y = band.Top; y < band.Bottom; y++) sum += ink[y];
        return sum / (band.Bottom - band.Top);
    }

    private static int Busiest(int[] changes, (int Top, int Bottom) band)
    {
        int most = 0;
        for (int y = band.Top; y < band.Bottom; y++) most = Math.Max(most, changes[y]);
        return most;
    }
}
