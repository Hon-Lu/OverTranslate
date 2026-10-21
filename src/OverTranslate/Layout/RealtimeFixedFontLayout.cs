// UseWindowsForms brings System.Drawing into the implicit usings, which has a Size of its own.
using Size = System.Windows.Size;

namespace OverTranslate.Layout;

/// <summary>
/// How a realtime line is set when the user has fixed the type size — 顯示外觀 → 文字大小 → 固定.
/// </summary>
/// <param name="FontSize">The size drawn at: the user's own, unless the last resort below took over.</param>
/// <param name="Wrapped">Whether the TextBlock wraps.</param>
/// <param name="TextWidth">The width the TextBlock is given.</param>
/// <param name="TextHeight">How tall the text comes out at that width and size.</param>
internal readonly record struct RealtimeFixedFontFit(
    double FontSize, bool Wrapped, double TextWidth, double TextHeight);

/// <summary>
/// The sizing ladder for 固定, kept apart from the window so the decisions can be checked without
/// one: the size stays where the user put it, and a sentence that will not fit wraps instead.
/// </summary>
/// <remarks>
/// <para>What 固定 exists to undo is 自動's shrink. 自動 sizes a line from its source and makes a
/// long sentence smaller to keep it on one line, so a short line and a long one from the same
/// speaker come out at visibly different sizes (#316). Here the size is the user's, and width is
/// bought back by wrapping, never by shrinking.</para>
///
/// <para>Shrinking is still the last rung, and only past the point where wrapping has run out:
/// when the wrapped text is taller than the room it is allowed. That room is the block the user
/// drew, which is this window, so the part past it is not drawn at all — and a sentence that ends
/// early with nothing to say so is the failure every overlay here is built to avoid. A smaller
/// sentence the reader can finish beats a fixed-size one they cannot.</para>
///
/// <para>The measurement is handed in as a delegate — the size, and the width to wrap at or null
/// for one line — because it is a TextBlock's in the window and has to be the same one there
/// (see <see cref="WrappedTextMeasure"/>), while these rules have nothing to do with WPF.</para>
/// </remarks>
internal static class RealtimeFixedFontLayout
{
    /// <summary>The step the last resort searches downwards by, as the window's own fit does.</summary>
    private const double ShrinkStep = 0.5;

    /// <summary>
    /// One source line: drawn on one line at the fixed size if that fits the block's width, and
    /// otherwise wrapped across it at the same size.
    /// </summary>
    /// <remarks>
    /// Neither of 自動's two caps applies. The line is not held to the height of the line it
    /// replaces — a reader who set 32 over 14px interface text asked for larger text, and the
    /// band grows round its source to cover both — and it is not shrunk to the block's width,
    /// which is the very thing 固定 is for.
    /// </remarks>
    public static RealtimeFixedFontFit Line(
        double fixedSize, double maxWidth, double maxHeight, double minFontSize,
        Func<double, double?, Size> measure)
    {
        var oneLine = measure(fixedSize, null);
        if (oneLine.Width <= maxWidth)
            return new(fixedSize, false, Math.Min(maxWidth, oneLine.Width), oneLine.Height);

        double size = LargestThatFits(fixedSize, maxWidth, maxHeight, minFontSize, measure);
        return Narrowed(size, maxWidth, measure);
    }

    /// <summary>
    /// Several source lines under one translation: wrapped at the group's own width, and across
    /// the whole block only when that comes out taller than the block.
    /// </summary>
    /// <remarks>
    /// The group's width first, because that is where the source paragraph was and widening it
    /// covers picture the reader is watching. 自動 shrinks to keep a group inside its source's
    /// height; here the size is fixed, so the band is let grow past that height instead, round the
    /// group's centre, and the width is only spent once height has run out too.
    /// </remarks>
    public static RealtimeFixedFontFit Group(
        double fixedSize, double groupWidth, double maxWidth, double maxHeight, double minFontSize,
        Func<double, double?, Size> measure)
    {
        groupWidth = Math.Min(groupWidth, maxWidth);
        var atGroupWidth = measure(fixedSize, groupWidth);
        if (atGroupWidth.Height <= maxHeight)
            return new(fixedSize, true, groupWidth, atGroupWidth.Height);

        double size = LargestThatFits(fixedSize, maxWidth, maxHeight, minFontSize, measure);
        var widened = Narrowed(size, maxWidth, measure);

        // Never narrower than the group: the band has to cover the source, and a narrower one would
        // only have its text wrap where a wider one was already paid for.
        if (widened.TextWidth >= groupWidth) return widened;
        return new(size, true, groupWidth, measure(size, groupWidth).Height);
    }

    /// <summary>
    /// The fixed size if the wrapped text fits <paramref name="maxHeight"/>, otherwise the largest
    /// size below it that does — the last resort. The floor is returned when nothing fits, for the
    /// reason the window's own fit gives: under it text stops being readable at all.
    /// </summary>
    private static double LargestThatFits(
        double fixedSize, double width, double maxHeight, double minFontSize,
        Func<double, double?, Size> measure)
    {
        for (double size = fixedSize; size >= minFontSize; size -= ShrinkStep)
            if (measure(size, width).Height <= maxHeight)
                return size;

        return minFontSize;
    }

    /// <summary>
    /// Wrapped across <paramref name="maxWidth"/>, with the box then narrowed to the widest line
    /// the wrap produced — the band hugs the text, as a single line's does.
    /// </summary>
    /// <remarks>
    /// Measured twice for the reason the window's wrapped fallback is: narrowing the box can move a
    /// line break, and a height taken at the wider width would then be a line short and clipped.
    /// One pixel of slack so rounding cannot leave the TextBlock a hair narrower than the measure.
    /// </remarks>
    private static RealtimeFixedFontFit Narrowed(
        double size, double maxWidth, Func<double, double?, Size> measure)
    {
        double width = Math.Min(maxWidth, measure(size, maxWidth).Width + 1);
        return new(size, true, width, measure(size, width).Height);
    }
}
