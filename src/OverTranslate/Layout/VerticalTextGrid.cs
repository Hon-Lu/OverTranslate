using System.Buffers;
using System.Windows;
using System.Windows.Controls;

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
/// how big the grid is allowed to get: a still capture may grow its bubble past the source, and a
/// live block may not grow past the rectangle the user drew. That is the difference between
/// <see cref="Fit"/> and <see cref="FitWithin"/>, and it is the only difference.
/// </remarks>
internal static class VerticalTextGrid
{
    private static readonly SearchValues<char> RotatedGlyphs = SearchValues.Create(
        "「」『』（）〔〕［］｛｝〈〉《》【】〖〗〘〙〚〛⦅⦆｟｠()[]{}<>" +
        "—–―─━‐‑‒-－〜～ーｰ＿_＝=" +
        "…⋯‥");

    /// <summary>Whether this glyph is drawn turned 90° when the text runs down the page.</summary>
    internal static bool RotatesGlyph(char glyph) => RotatedGlyphs.Contains(glyph);

    /// <summary>Returns cells in vertical reading order: downwards, then one column left.</summary>
    internal static IEnumerable<(char Glyph, Rect Cell)> Cells(
        string text,
        Rect bounds,
        double cellSize)
    {
        int columns = Math.Max(1, (int)Math.Floor(bounds.Width / cellSize));
        int rows = Math.Max(1, (int)Math.Floor((bounds.Height + 0.01) / cellSize));

        for (int i = 0; i < text.Length; i++)
        {
            int column = i / rows;
            int row = i % rows;
            if (column >= columns)
                yield break;

            yield return (text[i], new Rect(
                bounds.Left + bounds.Width - (column + 1) * cellSize,
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
    /// The live overlay's fit: a grid that fits inside the room it is given and never asks for more.
    /// </summary>
    /// <remarks>
    /// <para>The live layer is exactly the block the user drew, so there is no growing out of it —
    /// anything past the edge is not rendered at all, and a sentence that ends early with nothing to
    /// say so is the failure this whole overlay is written to avoid. So the cell shrinks instead,
    /// down to a floor the caller sets, and the columns are counted from what is left.</para>
    ///
    /// <para>Returned as a size rather than a count because the caller is placing a rectangle: the
    /// grid is anchored to the source's first character, not centred on the block, and it has to
    /// know how far the other two edges reach to do that.</para>
    /// </remarks>
    internal static (double CellSize, double Width, double Height) FitWithin(
        double maxWidth,
        double maxHeight,
        double preferredCellSize,
        double minCellSize,
        int characterCount)
    {
        int needed = Math.Max(1, characterCount);
        double cellSize = Math.Max(minCellSize, preferredCellSize);

        while (true)
        {
            int rows = Math.Max(1, (int)Math.Floor(maxHeight / cellSize));
            int columns = Math.Max(1, (int)Math.Ceiling((double)needed / rows));

            if (columns * cellSize <= maxWidth || cellSize <= minCellSize)
            {
                // At the floor the text may still not fit; keep the grid inside the block anyway and
                // let the tail be the thing that is lost, rather than the block's own edge.
                columns = Math.Max(1, Math.Min(columns, (int)Math.Floor(maxWidth / cellSize)));
                rows = Math.Max(1, Math.Min(rows, (int)Math.Ceiling((double)needed / columns)));
                return (cellSize, columns * cellSize, rows * cellSize);
            }

            cellSize = Math.Max(minCellSize, cellSize - 0.5);
        }
    }

    private static int Capacity(double width, double height, double cellSize) =>
        (int)Math.Max(0, Math.Floor(width / cellSize)) *
        (int)Math.Max(0, Math.Floor(height / cellSize));
}
