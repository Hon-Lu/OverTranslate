namespace OverTranslate.Services.Ocr;

/// <summary>
/// Which of the estimate's values the glyph height it returned actually came from.
/// </summary>
internal enum GlyphHeightSource
{
    /// <summary>No estimate was made: the script has no single glyph body to estimate.</summary>
    None,

    /// <summary>The detection box's own height, scaled. The value every other one is measured against.</summary>
    Box,

    /// <summary>The average glyph pitch, which came out lower than the box and replaced it.</summary>
    Pitch,

    /// <summary>The short-line correction, applied wherever the pitch clamp does not reach.</summary>
    ShortText,

    /// <summary>The floor, reached only by a degenerate box.</summary>
    Floor,
}

/// <summary>
/// Every intermediate value on the way to one glyph height, reported by the estimate itself.
/// </summary>
/// <remarks>
/// <para>Written because the last number is not enough to say which rule produced it. Two lines of
/// one paragraph came back 57% apart in estimated glyph height with detection boxes within two
/// pixels of each other, and reading only the final figure it looked like a box height problem; it
/// was the pitch branch being entered for one of them and not the other. A story reconstructed
/// backwards from the answer is a story, and this branch has now cost two of them.</para>
///
/// <para>Filled where the arithmetic happens rather than recomputed by whoever prints it. A trace
/// that works the branch out a second time is a trace that can disagree with the code it claims to
/// be describing, and every field here would then need re-checking against the real one whenever
/// either moved.</para>
///
/// <para>Diagnostic only. Nothing in the app reads it, and producing it changes no value the app
/// computes — the estimate grid test is what says so.</para>
/// </remarks>
/// <param name="PitchCoefficient">Latin 1.3 or CJK 1.18, the multiplier the pitch is read through.</param>
/// <param name="BoxEstimate">Height × 0.82: what the estimate is before anything challenges it.</param>
/// <param name="PitchCandidate">
/// Width ÷ glyphs × coefficient, computed whenever there are glyphs to divide by and NOT only when
/// it is read. <see cref="PitchBranchEntered"/> says whether the estimate looked at it, and
/// <see cref="PitchSelected"/> whether it won — the three are separate questions and printing the
/// number alone reads as though it were the answer.
/// </param>
/// <param name="WidthMinusTwiceHeight">
/// How far the box sits from the width condition, in pixels, signed. Zero is on the line and
/// refused, because the condition is a strict &gt;.
/// </param>
/// <param name="PitchSelected">Whether the pitch candidate came out below the box estimate and replaced it.</param>
/// <param name="ShortTextApplied">Whether the short-line correction was consulted at all.</param>
/// <param name="ShortTextSelected">Whether it changed the value.</param>
internal readonly record struct GlyphHeightTrace(
    OcrLayoutScript Script,
    double BoxWidth,
    double BoxHeight,
    int GlyphCount,
    double PitchCoefficient,
    double BoxEstimate,
    double? PitchCandidate,
    double WidthMinusTwiceHeight,
    bool HasEnoughGlyphs,
    bool IsWideEnough,
    bool PitchBranchEntered,
    bool PitchSelected,
    bool FloorApplied,
    bool ShortTextApplied,
    double? ShortTextCandidate,
    bool ShortTextSelected,
    GlyphHeightSource Source,
    double? Result)
{
    /// <summary>The trace of a script that gets no estimate, so that the caller still has the inputs.</summary>
    public static GlyphHeightTrace NotEstimated(OcrLayoutScript script, System.Windows.Rect box, int glyphCount) =>
        new(script, box.Width, box.Height, glyphCount, 0, 0, null,
            box.Width - box.Height * 2, glyphCount >= ShortTextGlyphHeight.PitchCorrectedFromGlyphs,
            box.Width > box.Height * 2, false, false, false, false, null, false,
            GlyphHeightSource.None, null);
}
