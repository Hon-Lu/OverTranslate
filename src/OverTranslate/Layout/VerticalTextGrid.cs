using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OverTranslate.Layout;

/// <summary>
/// How a translation is set into vertical writing: square cells filled downwards, columns running
/// right to left, and the handful of glyphs that have to be turned on their side to look right in
/// them.
/// </summary>
/// <remarks>
/// Shared by both overlays, and that is the point of it being here. The screenshot overlay has laid
/// vertical text out since issue #132 and the live one does now too, and the two disagreeing about
/// which way the columns run — or about whether a bracket is turned — would be the same feature
/// behaving differently in two places for no reason the reader could see. What they do NOT share is
/// what gives once the cell has shrunk as far as it may and the text still does not fit: a still
/// capture grows its bubble down the page, and a live block — which is exactly the rectangle the
/// user drew — opens another column instead and finally loses the tail. That is the difference
/// between <see cref="Fit"/> and <see cref="FitWithin"/>, and it is the only difference.
/// </remarks>
internal static class VerticalTextGrid
{
    private static readonly SearchValues<char> RotatedGlyphs = SearchValues.Create(
        "「」『』（）〔〕［］｛｝〈〉《》【】〖〗〘〙〚〛⦅⦆｟｠()[]{}<>" +
        "—–―─━‐‑‒-－〜～ーｰ＿_＝=" +
        "…⋯‥");

    /// <summary>Whether this glyph is drawn turned 90° when the text runs down the page.</summary>
    internal static bool RotatesGlyph(char glyph) => RotatedGlyphs.Contains(glyph);

    // Sentence punctuation, full-width and the half-width forms OCR and some engines hand back.
    // Only these move: the rest of the punctuation either turns (RotatedGlyphs) or already sits in
    // the middle of its cell, which is where vertical writing wants it.
    private static readonly SearchValues<char> ShiftedPunctuation = SearchValues.Create("。、，．｡､");

    // Japanese small kana, including the Ainu katakana extensions at U+31F0–U+31FF.
    private static readonly SearchValues<char> SmallKana = SearchValues.Create(
        "ぁぃぅぇぉっゃゅょゎゕゖァィゥェォッャュョヮヵヶ" +
        "ㇰㇱㇲㇳㇴㇵㇶㇷ" +
        "ㇸㇹㇺㇻㇼㇽㇾㇿ");

    // Where sentence punctuation's ink is centred once it has moved, as a fraction of the cell: the
    // upper right quadrant, which is where Japanese and mainland Chinese vertical setting put 。 and
    // 、. The ink is still pulled back inside the cell by InkShift, so a wide comma ends up hard
    // against the right edge rather than past it.
    private const double PunctuationInkX = 0.75;
    private const double PunctuationInkY = 0.25;

    // How far a small kana moves towards the top right of a full-size kana's ink, as a fraction of
    // each gap. Across, all the way: its right edge lines up with the column's, as it does in print,
    // and the gap is only about a tenth of the cell. Down, half: all the way pins it to the top of
    // the cell, where it reads as belonging to the character above; half is about an eighth of the
    // cell, which is roughly where vertical fonts put their own small kana.
    private const double SmallKanaPullRight = 1.0;
    private const double SmallKanaPullUp = 0.5;

    // Cell-relative ink boxes, measured once per family, weight, glyph and size. The sizes in the key
    // are rounded to a quarter DIP so a grid that shrinks in half-pixel steps does not fill this with
    // near-duplicates, and it is cleared outright once it grows past what any one screen could use:
    // a long live session walks through a lot of sizes, and none of them is worth keeping in
    // particular.
    private static readonly ConcurrentDictionary<(string Family, int Weight, char Glyph, double Size, double Cell), Rect>
        InkCache = new();
    private const int InkCacheLimit = 4096;

    internal enum GlyphShift
    {
        None,
        Punctuation,
        SmallKana,
    }

    /// <summary>
    /// How a glyph is moved inside its cell when a translation into <paramref name="targetLanguage"/>
    /// runs down the page.
    /// </summary>
    /// <remarks>
    /// The fonts draw 。、， where horizontal text wants them, and one TextBlock per glyph has no way
    /// to ask a font for its vertical forms. So the glyph is drawn as it is and then moved — only for
    /// the targets whose fonts put that punctuation in the bottom left, Japanese and Simplified
    /// Chinese. Traditional Chinese sets it centred in vertical writing too, and JhengHei already
    /// draws it centred. Small kana moving is a Japanese convention; every other target is left alone.
    /// </remarks>
    internal static GlyphShift ShiftFor(char glyph, string? targetLanguage)
    {
        switch (targetLanguage?.Trim().ToUpperInvariant())
        {
            case "JA":
                if (ShiftedPunctuation.Contains(glyph))
                    return GlyphShift.Punctuation;
                return SmallKana.Contains(glyph) ? GlyphShift.SmallKana : GlyphShift.None;
            case "ZH":
            case "ZH-HANS":
                return ShiftedPunctuation.Contains(glyph) ? GlyphShift.Punctuation : GlyphShift.None;
            default:
                return GlyphShift.None;
        }
    }

    /// <summary>How far to move a glyph whose ink sits at <paramref name="ink"/> in a square cell.</summary>
    /// <param name="ink">The glyph's ink box, relative to its cell.</param>
    /// <param name="referenceInk">
    /// A full-size kana's ink box in the same cell, which a small kana is pulled towards. Unused for
    /// punctuation.
    /// </param>
    /// <remarks>
    /// Never left or down, whatever the font: a face that already draws the glyph where vertical
    /// writing wants it gets nothing rather than a correction in the wrong direction. And never out
    /// of the cell — a wide comma aimed at the three-quarter mark would otherwise poke into the
    /// column beside it.
    /// </remarks>
    internal static Vector InkShift(GlyphShift shift, Rect ink, Rect referenceInk, double cellSize)
    {
        if (shift == GlyphShift.None || ink.IsEmpty)
            return default;

        double dx;
        double dy;
        if (shift == GlyphShift.Punctuation)
        {
            dx = cellSize * PunctuationInkX - (ink.Left + ink.Width / 2);
            dy = cellSize * PunctuationInkY - (ink.Top + ink.Height / 2);
        }
        else
        {
            if (referenceInk.IsEmpty)
                return default;
            dx = (referenceInk.Right - ink.Right) * SmallKanaPullRight;
            dy = (referenceInk.Top - ink.Top) * SmallKanaPullUp;
        }

        dx = Math.Min(dx, cellSize - ink.Right);
        dy = Math.Max(dy, -ink.Top);
        return new Vector(Math.Max(0, dx), Math.Min(0, dy));
    }

    /// <summary>
    /// Moves <paramref name="cell"/>'s glyph to where vertical writing puts it, if it is one that
    /// moves. Call it with the TextBlock's font already set: the glyph is measured as it will be drawn.
    /// </summary>
    /// <remarks>
    /// A RenderTransform rather than a different cell: the grid decides how many glyphs a column
    /// holds, and a period takes a whole cell in vertical writing wherever in it the ink sits.
    /// </remarks>
    internal static void ShiftGlyph(TextBlock cell, char glyph, string? targetLanguage, double cellSize)
    {
        var shift = ShiftFor(glyph, targetLanguage);
        if (shift == GlyphShift.None)
            return;

        var ink = MeasureInk(cell, glyph, cellSize);
        var reference = shift == GlyphShift.SmallKana
            ? MeasureInk(cell, glyph <= 'ゖ' ? 'あ' : 'ア', cellSize)
            : Rect.Empty;
        var offset = InkShift(shift, ink, reference, cellSize);
        if (offset.X != 0 || offset.Y != 0)
            cell.RenderTransform = new TranslateTransform(offset.X, offset.Y);
    }

    /// <summary>
    /// Where <paramref name="glyph"/>'s ink lands in a cell laid out by <see cref="PositionGlyph"/>,
    /// in <paramref name="cell"/>'s font.
    /// </summary>
    /// <remarks>
    /// FormattedText with the cell as its line height, started where a centred TextBlock starts the
    /// glyph, lands within a pixel of what the TextBlock itself renders — checked by rendering both
    /// in Yu Gothic UI, YaHei and JhengHei in an 80 DIP cell. That is cheaper than laying out a
    /// throwaway TextBlock, and it does not need the element to be in a visual tree yet.
    /// </remarks>
    internal static Rect MeasureInk(TextBlock cell, char glyph, double cellSize)
    {
        double size = Math.Round(cell.FontSize * 4) / 4;
        double cellKey = Math.Round(cellSize * 4) / 4;
        var key = (cell.FontFamily.Source, cell.FontWeight.ToOpenTypeWeight(), glyph, size, cellKey);
        if (InkCache.TryGetValue(key, out var cached))
            return cached;

        var text = new FormattedText(
            glyph.ToString(),
            cell.Language.GetSpecificCulture() ?? CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(cell.FontFamily, cell.FontStyle, cell.FontWeight, cell.FontStretch),
            size,
            System.Windows.Media.Brushes.Black,
            1.0)
        {
            LineHeight = cellKey,
        };
        double left = (cellKey - text.WidthIncludingTrailingWhitespace) / 2;
        var ink = text.BuildGeometry(new System.Windows.Point(left, 0)).Bounds;

        if (InkCache.Count >= InkCacheLimit)
            InkCache.Clear();
        InkCache[key] = ink;
        return ink;
    }

    /// <summary>Returns cells in vertical reading order: downwards, then one column left.</summary>
    /// <remarks>
    /// Against the top of the box, centred across it — the horizontal overlay's rule given a quarter
    /// turn, where a line starts hard against the left edge and the lines together sit centred down
    /// the box. Along the writing there is a corner the reader is already looking at: vertical text
    /// begins at the top of the rightmost column, so a translation shorter than its source has to
    /// start where the source started and simply run out early. Across the writing there is no such
    /// corner, and a translation that needs fewer columns than the box holds leaves slack that
    /// belongs on both sides: hanging all of it off one edge slides the whole block away from the
    /// writing it replaces, by a different amount for every block on the page.
    /// </remarks>
    internal static IEnumerable<(char Glyph, Rect Cell)> Cells(
        string text,
        Rect bounds,
        double cellSize)
    {
        // Both tolerances are there for the same reason: a box cut to an exact number of cells is
        // the ordinary case on the live path, where the caller sizes it from this very grid, and
        // `columns * cellSize / cellSize` is not reliably `columns` in binary. Without the slack a
        // three-column grid measures itself at two and the last column of the sentence is dropped
        // — silently, because every glyph in it simply never gets a cell.
        int columns = Math.Max(1, (int)Math.Floor((bounds.Width + 0.01) / cellSize));
        int rows = Math.Max(1, (int)Math.Floor((bounds.Height + 0.01) / cellSize));

        // A column is filled to the bottom of the room it has before the next one starts, so the
        // columns actually occupied — the ones that get centred — are that many of them.
        int used = Math.Min(columns, Math.Max(1, (int)Math.Ceiling((double)text.Length / rows)));
        double right = bounds.Left + (bounds.Width + used * cellSize) / 2;

        for (int i = 0; i < text.Length; i++)
        {
            int column = i / rows;
            int row = i % rows;
            if (column >= used)
                yield break;

            yield return (text[i], new Rect(
                right - (column + 1) * cellSize,
                bounds.Top + row * cellSize,
                cellSize,
                cellSize));
        }
    }

    /// <summary>Puts one glyph in its cell, filling it rather than sitting on a baseline in it.</summary>
    internal static void PositionGlyph(TextBlock glyph, Rect bounds)
    {
        glyph.Width = bounds.Width;
        glyph.Height = bounds.Height;
        glyph.LineHeight = bounds.Height;
        glyph.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        Canvas.SetLeft(glyph, bounds.X);
        Canvas.SetTop(glyph, bounds.Y);
    }

    /// <summary>
    /// The screenshot overlay's fit: shrink the cell until the text fits the width it has, and grow
    /// the bubble downwards if it still does not.
    /// </summary>
    internal static (double CellSize, double Height) Fit(
        double width,
        double height,
        double preferredCellSize,
        double minCellSize,
        double emergencyCellSize,
        int characterCount)
    {
        int needed = Math.Max(1, characterCount);
        double cellSize = Math.Max(minCellSize, preferredCellSize);
        while (cellSize > emergencyCellSize && Capacity(width, height, cellSize) < needed)
        {
            cellSize = Math.Max(emergencyCellSize, cellSize - 0.5);
        }

        int columns = Math.Max(1, (int)Math.Floor(width / cellSize));
        int rows = Math.Max(1, (int)Math.Ceiling((double)needed / columns));
        return (cellSize, Math.Max(height, rows * cellSize));
    }

    /// <summary>
    /// The live overlay's fit: the source's own footprint if the type can be made to sit in it, and
    /// never wider than the block.
    /// </summary>
    /// <remarks>
    /// <para>The order of the two concessions is the whole of this method, and getting it backwards
    /// is what <see cref="Fit"/> already had right. A translation too long for the column it
    /// replaces can either be set a little smaller in that column or be spread over more columns
    /// than the source had, and only the first of those stays inside the balloon. The second walks
    /// out of it: the columns are laid right to left, so the extra ones land on whatever is drawn
    /// beside the source — the next balloon, the next panel, the picture. So the cell shrinks first,
    /// down to the caller's readability floor, and columns are added only once that has run out.
    /// The screenshot overlay reaches the same answer by shrinking before it grows the bubble.</para>
    ///
    /// <para><paramref name="columnLength"/> is how long one column may run, and it is the source's
    /// own length rather than the block's. Over a comic page the block IS the page, so a column
    /// given the block's height fits every sentence into one and runs it from the top of the
    /// picture to the bottom, straight through the panels either side of the balloon it came
    /// from.</para>
    ///
    /// <para>Returned as a size rather than a count because the caller is placing a rectangle over
    /// the source and has to know how far it reaches to do that.</para>
    /// </remarks>
    /// <param name="columnRoom">How wide the source group was: the grid stays inside it if it can.</param>
    /// <param name="columnLength">How far down one column may run.</param>
    /// <param name="maxWidth">The block. The grid is never wider, whatever is left unplaced.</param>
    internal static (double CellSize, double Width, double Height) FitWithin(
        double columnRoom,
        double columnLength,
        double maxWidth,
        double preferredCellSize,
        double minCellSize,
        int characterCount)
    {
        int needed = Math.Max(1, characterCount);
        double cellSize = Math.Max(minCellSize, preferredCellSize);

        while (cellSize > minCellSize && Capacity(columnRoom, columnLength, cellSize) < needed)
        {
            cellSize = Math.Max(minCellSize, cellSize - 0.5);
        }

        int rows = Math.Max(1, (int)Math.Floor(columnLength / cellSize));
        int columns = Math.Max(1, (int)Math.Ceiling((double)needed / rows));

        // At the floor the text may still not fit; keep the grid inside the block anyway and let
        // the tail be the thing that is lost, rather than the block's own edge.
        columns = Math.Max(1, Math.Min(columns, (int)Math.Floor(maxWidth / cellSize)));

        // Every column but the last is full, so a grid over one column is the only one shorter than
        // the room it was given.
        int usedRows = columns > 1 ? rows : Math.Min(rows, needed);
        return (cellSize, columns * cellSize, usedRows * cellSize);
    }

    private static int Capacity(double width, double height, double cellSize) =>
        (int)Math.Max(0, Math.Floor(width / cellSize)) *
        (int)Math.Max(0, Math.Floor(height / cellSize));
}
