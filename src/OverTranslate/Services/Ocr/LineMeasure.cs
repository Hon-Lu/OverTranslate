using System.Windows;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// The two questions both groupers ask of a pair of lines before anything about spacing: is this
/// line long enough to have run out of room, and are these two the same size.
/// </summary>
/// <remarks>
/// <para>Collected here so that <see cref="OcrTextBlockGrouper"/> and
/// <see cref="Realtime.DialogueTextGrouper"/> ask them the same way and a new rule finds the figure
/// already in use instead of writing a fresh one. The figures were moved, not merged: each is the
/// number its rule was measured with, and several look alike without being the same question.</para>
///
/// <para>TWO ARITHMETIC FORMS ON PURPOSE. Some rules divided (<see cref="Aspect"/>,
/// <see cref="HeightRatio"/>) and some multiplied (<see cref="Spans"/>,
/// <see cref="HeightsWithin"/>). Both are kept as each rule wrote it, because at the boundary the
/// two can disagree by a rounding step and this file was introduced without moving a verdict.
/// Settling on one is a behaviour change, to be measured as one.</para>
///
/// <para>Text size here is the detection box. The screenshot grouper's own size test reads glyph
/// heights where it has them — see <see cref="OcrTextBlockGrouper.TextSizeRatio(OcrTextBlock, OcrTextBlock)"/>
/// — and that stays where it is.</para>
/// </remarks>
internal static class LineMeasure
{
    /// <summary>
    /// Whether a line is long enough that running out of room — and so wrapping — is plausible.
    /// </summary>
    /// <remarks>
    /// <para>Measured in line heights rather than characters so that one number serves every script:
    /// a CJK line runs about one character per line height, a Latin one about two. So this asks for
    /// roughly eight CJK characters, or sixteen Latin ones.</para>
    ///
    /// <para>Across the 329-image corpus the two populations are far apart: every genuinely wrapped
    /// sentence reached 16.6 and every stacked label stopped at 6.8, with nothing in between. The
    /// threshold sits at the low end of that empty band on purpose. Letting a label through costs
    /// two crowded words in one bubble; keeping a real wrapped sentence apart costs the translator
    /// half a sentence, which is the far worse failure — it is the whole of #74.</para>
    /// </remarks>
    internal const double WrappedLineMinAspect = 8.0;

    /// <summary>
    /// A row of dialogue or prose long enough to be given the benefit of the doubt on alignment and
    /// height — what a short name or a stray symbol must not borrow.
    /// </summary>
    internal const double SubstantialLineMinAspect = 6.0;

    /// <summary>A row long enough to be a line of a paragraph rather than of an interface.</summary>
    internal const double ParagraphRowMinAspect = 12.0;

    /// <summary>A body line long enough that it is certainly running text set across a column.</summary>
    internal const double ProseLineMinAspect = 20.0;

    /// <summary>The narrowest and widest a row may be, against another, to count as the same size of text.</summary>
    internal const double SizeBandLow = 0.75;

    /// <inheritdoc cref="SizeBandLow"/>
    internal const double SizeBandHigh = 1.35;

    /// <summary>A box's length in its own line heights; nothing for a degenerate box.</summary>
    internal static double Aspect(Rect box) => box.Height > 0 ? box.Width / box.Height : 0;

    /// <summary>Whether a box runs at least this many of its own line heights across.</summary>
    internal static bool Spans(Rect box, double lineHeights) => box.Width >= box.Height * lineHeights;

    /// <summary>The shorter box's height over the taller one's.</summary>
    internal static double HeightRatio(Rect a, Rect b) =>
        Math.Min(a.Height, b.Height) / Math.Max(a.Height, b.Height);

    /// <summary>Whether the shorter box is at least this fraction of the taller one's height.</summary>
    internal static bool HeightsWithin(Rect a, Rect b, double ratio) =>
        Math.Min(a.Height, b.Height) >= Math.Max(a.Height, b.Height) * ratio;

    /// <summary>Whether a height is within <see cref="SizeBandLow"/>..<see cref="SizeBandHigh"/> of a reference.</summary>
    internal static bool WithinSizeBand(double height, double reference) =>
        height >= reference * SizeBandLow && height <= reference * SizeBandHigh;
}
