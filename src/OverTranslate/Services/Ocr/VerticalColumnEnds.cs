using SkiaSharp;
using TextBox = RapidOcrNet.TextBox;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Gives a column back the character its box stopped short of, before anything crops from it.
/// </summary>
/// <remarks>
/// <para>The most repeated loss in vertical Japanese, and the one the user reported first: the top
/// or bottom character of a column is missing from the reading. It is not a recognition failure —
/// the glyph is never handed to the recogniser at all, because the detector's quad starts below it.
/// MEASURED on <c>.ai/test-images/vertical-image-ja2/2026-09-20 19 14 59 (2).png</c>: the column
/// 私の知らない人… is boxed from y=616 while 私 is drawn from y=603, and it reads back as
/// の知らない人…. The same shape turns up on completely different material — ヘッドの下なら read as
/// ッドの下なら, お来てるな as 来てるな, この辺り一帯 as この辺り帯 — so it is the detector's
/// behaviour rather than one comic's typesetting.</para>
///
/// <para>Widening every quad is the wrong answer and was not done: a column sits about two thirds
/// of a character away from the next one, so a blind margin reaches into the neighbouring column and
/// reads its glyphs into this one. What makes this safe is that it only grows while the row
/// IMMEDIATELY outside the box still has ink. A box that already holds its whole column has a blank
/// row just past its edge and does not move at all; a box that cut a glyph in half has that glyph's
/// remaining strokes there, and the walk stops as soon as they run out. So the question asked is
/// "was something cut here", not "is there anything nearby".</para>
///
/// <para>Behind the same flat-background pre-filter <see cref="VerticalColumnDetection"/> uses, and
/// for the same reason: over artwork there is ink everywhere, every row qualifies, and the walk
/// would spend its whole budget every time.</para>
/// </remarks>
internal static class VerticalColumnEnds
{
    /// <summary>
    /// How far past its own edge a column may reach for the glyph it cut, in box widths.
    /// </summary>
    /// <remarks>
    /// A column's box is about one character wide, so this is a budget in characters. Half of one,
    /// because the walk stops at the first blank row anyway and the budget is only there for the
    /// case where it never finds one.
    ///
    /// MEASURED at 0.4, 0.5, 0.6 and 0.9 over the 15 comic pages and the 12 web ones, and the
    /// honest summary is that it is worth about what it costs on the corpus everything else was
    /// tuned on: character recall there goes 0.975 to 0.976 while whole balloons go 124 to 123,
    /// because a longer reading changes the character pitch grouping measures with. What decided it
    /// is the corpus that was NOT tuned on — the twelve pages off other projects' test packs, where
    /// recall goes 0.923 to 0.931 and whole balloons 40 to 41, and where the recovered characters
    /// are the first one of a line every time: ヘッドの下なら for ッドの下なら, お来てるな for
    /// 来てるな, この辺り一帯 for この辺り帯, 私の知らない人 for の知らない人. Above 0.5 both
    /// corpora fall: the walk starts finding the artwork past the end of the balloon.
    /// </remarks>
    private const double Reach = 0.5;

    /// <summary>Ink a row needs before it counts as a glyph continuing, as a share of the width.</summary>
    private const double RowInk = 0.05;

    /// <summary>The box to CROP from, which is not the box the page is laid out with.</summary>
    /// <remarks>
    /// <para>Only the crop grows, and that separation is what makes this worth having at all.
    /// MEASURED both ways over the 15 comic pages and the 12 web ones: growing the detected box
    /// itself recovers the same characters, and then costs them back in grouping — a column that is
    /// a few pixels longer sits differently against its neighbours, and two balloons that had been
    /// separate merged (ああ次の探索の準備か with それならちょうど今) while one that had been whole
    /// came apart. Grouping reads the detector's own geometry, which has not changed; recognition
    /// reads the pixels, which is where the missing glyph is.</para>
    /// </remarks>
    internal static TextBox Extend(SKBitmap image, TextBox box)
    {
        {
            var p = box.BoxPoints;
            var left = Math.Max(0, p.Min(v => v.X));
            var right = Math.Min(image.Width, p.Max(v => v.X));
            var top = Math.Max(0, p.Min(v => v.Y));
            var bottom = Math.Min(image.Height, p.Max(v => v.Y));
            var width = right - left;
            var height = bottom - top;

            // An upright column, as the splitter defines one. A perspective quad has no "up".
            if (width < 8 || height < width * 1.5 ||
                Math.Abs(p[0].X - p[3].X) > 3 || Math.Abs(p[1].X - p[2].X) > 3 ||
                Math.Abs(p[0].Y - p[1].Y) > 3 || Math.Abs(p[3].Y - p[2].Y) > 3 ||
                !FlatBackground(image, left, right, top, bottom, out var mode))
                return box;

            var budget = (int)(width * Reach);
            var floor = Math.Max(1, width * RowInk);
            var newTop = top;
            while (newTop > top - budget && newTop > 0 && Continues(image, left, right, newTop - 1, mode, floor))
                newTop--;
            var newBottom = bottom;
            while (newBottom < bottom + budget && newBottom < image.Height &&
                   Continues(image, left, right, newBottom, mode, floor))
                newBottom++;

            if (newTop == top && newBottom == bottom) return box;

            return new TextBox
            {
                Score = box.Score,
                BoxPoints =
                [
                    new(left, newTop), new(right, newTop), new(right, newBottom), new(left, newBottom),
                ],
            };
        }
    }

    /// <summary>Whether this row is more of the column's own glyph rather than something crossing it.</summary>
    /// <remarks>
    /// A glyph's ink stops at the column; a rule does not. The border of a narration box, the
    /// outline of a balloon and the edge of a panel all run right through where a column ends, and
    /// each of them offers the walk an unbroken row of ink to spend its whole budget on. MEASURED:
    /// without this the narration box on 2026-09-20 19 14 57 (2).png turned
    /// だって俺はパーティから捨てられる辛さを知っている into
    /// だって俺はなさテかってきてられる, because every column in it grew into the border and the
    /// crops stopped being columns.
    ///
    /// Asked of BOTH sides, because a reading sits along one side of its column for its whole
    /// length — testing either side alone would refuse to extend any column that has furigana.
    /// </remarks>
    private static bool Continues(SKBitmap image, int left, int right, int y, int mode, double floor)
    {
        if (!HasInk(image, left, right, y, mode, floor)) return false;

        var reach = Math.Max(2, (right - left) / 4);
        return !(HasInk(image, Math.Max(0, left - reach), left, y, mode, 1) &&
                 HasInk(image, right, Math.Min(image.Width, right + reach), y, mode, 1));
    }

    private static bool HasInk(SKBitmap image, int left, int right, int y, int mode, double floor)
    {
        var ink = 0;
        for (var x = left; x < right; x++)
            if (Math.Abs(Luminance(image.GetPixel(x, y)) - mode) > 48 && ++ink >= floor)
                return true;
        return false;
    }

    private static bool FlatBackground(
        SKBitmap image, int left, int right, int top, int bottom, out int mode)
    {
        // Strided: this is a share and a mode, both of which a quarter of the pixels answer just
        // as well, and it is the only part of this type that touches the whole box. MEASURED over
        // the 15 comic pages against a pass of 1.58s a frame: every pixel costs 43ms a frame, every
        // second pixel costs 5ms, and the two read the pages identically — 125 balloons whole,
        // 0.976 recall, on both.
        const int step = 2;
        var histogram = new int[256];
        var samples = 0;
        for (var y = top; y < bottom; y += step)
        for (var x = left; x < right; x += step)
        {
            histogram[Luminance(image.GetPixel(x, y)) / 16 * 16]++;
            samples++;
        }

        var peak = histogram.Max();
        mode = Array.IndexOf(histogram, peak) + 8;
        // Lower than the splitter's bar: that one has to be sure enough to CUT a box, this one only
        // walks a few rows and stops at the first blank one.
        return peak >= samples * 0.45;
    }

    private static int Luminance(SKColor c) => (c.Red * 77 + c.Green * 150 + c.Blue * 29) >> 8;
}
