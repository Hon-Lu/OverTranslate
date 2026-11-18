namespace OverTranslate.Layout;

/// <summary>
/// Where a realtime translation band sits horizontally over the source line it replaces.
/// </summary>
/// <remarks>
/// Only the live subtitle layer uses this. The screenshot overlay lays its bubbles out differently
/// and deliberately — see <see cref="SingleLineOverlayLayout"/>.
/// </remarks>
internal static class RealtimeBandPlacement
{
    /// <summary>
    /// The band's left edge, centred on the source line and kept inside the block.
    /// </summary>
    /// <remarks>
    /// A translation is very rarely the same length as what it replaces — a long subtitle often
    /// comes back much shorter — so a band pinned to the source's left edge drifts away from the
    /// words it stands in for, and does so by more the bigger the mismatch. Centring spends the
    /// difference evenly on both sides: the band grows and shrinks around the middle of the line,
    /// which is where the eye already is, so a mismatch of length never becomes a mismatch of
    /// position. This is also what the vertical placement already does.
    /// </remarks>
    /// <param name="sourceLeft">Left edge of the recognised source line, in block coordinates.</param>
    /// <param name="sourceWidth">Width of the recognised source line.</param>
    /// <param name="bandWidth">Width of the band being placed, scrim padding included.</param>
    /// <param name="blockWidth">Width of the watched block, which the band may not leave.</param>
    public static double Left(
        double sourceLeft, double sourceWidth, double bandWidth, double blockWidth)
    {
        double centred = sourceLeft + sourceWidth / 2 - bandWidth / 2;

        // A band wider than the block cannot be contained; pinning it to the left edge at least
        // keeps the start of the sentence readable, and the window clips the rest.
        return Math.Clamp(centred, 0, Math.Max(0, blockWidth - bandWidth));
    }

    /// <summary>
    /// The band's left edge set against the edge 譯文對齊 asks for — the text inside it starting at
    /// the source's left edge — and kept inside the block. Centred, or
    /// with no edge asked for, it is <see cref="Left(double, double, double, double)"/>.
    /// </summary>
    /// <remarks>
    /// A source set flush left keeps its left edge whatever length the translation comes back: the
    /// edge is where the reader's eye goes back to at each line, which is the case the centring above
    /// does not cover.
    /// </remarks>
    /// <param name="padding">The band's own padding either side of its text.</param>
    public static double Left(
        double sourceLeft, double sourceWidth, double bandWidth, double blockWidth,
        SourceAlignment? alignment, double padding)
    {
        double at = alignment switch
        {
            SourceAlignment.Left => sourceLeft - padding,
            _ => sourceLeft + sourceWidth / 2 - bandWidth / 2,
        };
        return Math.Clamp(at, 0, Math.Max(0, blockWidth - bandWidth));
    }
}
