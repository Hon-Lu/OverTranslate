using System.Drawing;
using System.Drawing.Imaging;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Whether the edge of a panel runs between two boxes the detector found on one row of a live
/// frame — the one thing about a row the boxes alone cannot say.
/// </summary>
/// <remarks>
/// <para>A game's menu screen sets its panels side by side, and a row of one sits level with a row
/// of the next, a narrow frame apart. In a JRPG party menu (FF4's, 960×544) a member's
/// 「Lv 54 HP2015/2277」 and the next panel's 「月齢 上弦の月」 stood 0.6–0.7 of a line height
/// apart, inside the gap the same-row test lets through, and were translated as one string. Which
/// row it happened to moved with the detector's boxes, and so with how the block was framed: of 20
/// framings of that one picture, 11 joined a row across the frame (that row, or the next one with
/// the panel's 「影響」). Nothing in the boxes tells that gap from a wide word space — what does is
/// the frame drawn across it.</para>
///
/// <para>An edge is two columns of the gap, <see cref="Side"/> apart, each one shade from well
/// above the row to well below it, and the two clearly different shades. Glyphs cannot do that:
/// they stop at the row, and a column through one changes shade where the stroke ends. A separator
/// bar between words ("Home | About") is no taller than the row either. What is left is a panel's
/// frame, a table's rule, the side of a badge or a bubble — each a reason the two boxes are two
/// things.</para>
///
/// <para>Measured over 1,265 cached frames (the 591 of the realtime replay corpus — subtitles,
/// comics, chat, web pages, game interfaces — the RPG and comic-caption screens, and 235 pictures
/// of the remaining horizontal test sets read at both detector sizes): of the 590 times it was
/// asked in one replay of that corpus, 261 had a gap and room enough to look and none found an
/// edge, so that corpus groups exactly as before. Elsewhere it parted two pairs, both rightly: the menu
/// above, and a news site's blue name badge from the sentence beside it that repeats the name. It
/// parted the menu in all 20 framings, and at 0.8×, 1.25×, 1.5× and as a JPEG.</para>
///
/// <para>Read from the frame only where a pair is in question, never the whole of it: the test
/// runs on rows the gap test has already joined, about one per frame, and each looks at a strip a
/// few dozen pixels wide — 0.002ms a frame on average, 0.3ms at worst.</para>
/// </remarks>
internal sealed class PanelEdges(Bitmap frame)
{
    /// <summary>How far apart the two columns of an edge are, in pixels.</summary>
    /// <remarks>
    /// Two rather than one so a frame drawn with a soft, scaled or compressed edge still has a full
    /// step across it; the gap needs to be this plus one wide before it is looked at.
    /// </remarks>
    private const int Side = 2;

    /// <summary>
    /// How far above and below the row the edge has to run, in line heights. Past where any glyph
    /// of the row can reach, which is the whole of the evidence.
    /// </summary>
    private const double Reach = 0.5;

    /// <summary>How far a column's pixels may stray from its own median and still be one shade.</summary>
    private const int ShadeTolerance = 16;

    /// <summary>The share of a column's pixels that have to be that shade.</summary>
    private const double SteadyShare = 0.95;

    /// <summary>How different the two columns' shades have to be, out of 255.</summary>
    private const int MinStep = 32;

    /// <summary>
    /// Whether an edge runs between <paramref name="left"/> and <paramref name="right"/>, the
    /// right one starting after the left one ends.
    /// </summary>
    /// <remarks>
    /// False whenever it cannot be told: a gap too narrow, a row too near the top or bottom of the
    /// frame for the edge to be seen past it on either side, a line tilted and levelled for the
    /// grouping rules (whose box is no longer where its pixels are), or a frame that cannot be read.
    /// False keeps the join the rest of the rules decided on.
    /// </remarks>
    internal bool Between(OcrTextBlock left, OcrTextBlock right)
    {
        if (left.UprightLayoutBounds.HasValue || right.UprightLayoutBounds.HasValue)
            return false;

        var a = left.LayoutBounds;
        var b = right.LayoutBounds;
        var x0 = Math.Max(0, (int)Math.Ceiling(a.Right));
        var x1 = Math.Min(frame.Width, (int)Math.Floor(b.Left));
        if (x1 - x0 <= Side)
            return false;

        var top = Math.Min(a.Top, b.Top);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        var reach = (a.Height + b.Height) / 2 * Reach;
        var y0 = Math.Max(0, (int)Math.Floor(top - reach));
        var y1 = Math.Min(frame.Height, (int)Math.Ceiling(bottom + reach));
        // Past the row on at least one side by the whole reach, or nothing has been shown that a
        // glyph could not have done.
        if (y1 - y0 < bottom - top + reach)
            return false;

        int[,] shades;
        try
        {
            shades = Read(new Rectangle(x0, y0, x1 - x0, y1 - y0));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Locked elsewhere or gone: no answer, so no change to the join.
            return false;
        }

        var width = x1 - x0;
        var height = y1 - y0;
        var medians = new int?[width];
        for (var x = 0; x < width; x++)
            medians[x] = SteadyShade(shades, x, height);

        for (var x = 0; x + Side < width; x++)
        {
            if (medians[x] is { } near && medians[x + Side] is { } far && Math.Abs(near - far) >= MinStep)
                return true;
        }

        return false;
    }

    // The column's shade, or null when it is not one shade from top to bottom.
    private static int? SteadyShade(int[,] shades, int x, int height)
    {
        var column = new int[height];
        for (var y = 0; y < height; y++)
            column[y] = shades[x, y];
        Array.Sort(column);
        var median = column[height / 2];

        var steady = 0;
        for (var y = 0; y < height; y++)
        {
            if (Math.Abs(shades[x, y] - median) <= ShadeTolerance)
                steady++;
        }

        return steady >= height * SteadyShare ? median : null;
    }

    private unsafe int[,] Read(Rectangle area)
    {
        var shades = new int[area.Width, area.Height];
        var data = frame.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < area.Height; y++)
            {
                var row = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                for (var x = 0; x < area.Width; x++)
                {
                    var pixel = row[x];
                    shades[x, y] = (int)(((pixel >> 16) & 0xFF) * 77 + ((pixel >> 8) & 0xFF) * 150 + (pixel & 0xFF) * 29) >> 8;
                }
            }
        }
        finally
        {
            frame.UnlockBits(data);
        }

        return shades;
    }
}
