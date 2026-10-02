namespace OverTranslate.Services.Ocr;

/// <summary>
/// Corrects the glyph height reported for a line the pitch clamp does not reach — one to three
/// characters, or a box not more than twice as wide as it is tall — which is the number both
/// overlays size their font from.
/// </summary>
/// <remarks>
/// The height a Latin block reports starts at 0.82 of its detection box — far too generous, because
/// the box is roughly half again as tall as the glyphs in it — and is pulled back to the truth by a
/// clamp against the average glyph pitch. That clamp needs several letters to average over, so it
/// is only applied from four glyphs up, and only on a box more than twice as wide as tall; wherever
/// it is not applied the 0.82 stands unchallenged. See <see cref="PitchClampApplies"/>.
///
/// The second half was missed at first, and lines of four or five glyphs in a narrow box — "TOO.",
/// "Yay!", a web page's "Copy" — kept the 0.82. Measured over the English test images, their
/// estimate came out 1.8–1.9 times their ink height, against 1.0–1.4 for long lines and 0.8–1.0
/// for lines under four glyphs, while their ink filled 0.39–0.46 of the box, the same as either.
///
/// Measured by drawing known text and reading back what the recogniser returned, at a 64px and a
/// 40px em:
///
/// <code>
///   text                 glyphs   box/em (64px, 40px)   reported   true
///   YA                        2   1.48   1.45               78      46
///   Wow                       3   1.38   1.60               72      46
///   Hello there friend       16   1.34   1.50             45.5      46
/// </code>
///
/// The long line, corrected by pitch, lands within a pixel. The two-letter line is 1.7x too large,
/// which is what turned a subtitle reading "YA" into an enormous 耶 across the picture.
///
/// Pitch cannot simply be extended downwards: on two letters it is whatever those two letters
/// happen to be — "YA" measured 1.05 of an em where a full line measures 0.55 — and the clamp it
/// yields (88) is looser than the estimate it was meant to tighten (78). The box is used instead,
/// because the ratio of true glyph height to box height held between 0.48 and 0.54 across every
/// sample, at every length.
///
/// Latin only, matching where the reported height comes from. A CJK source is normalised a
/// different way and has not been measured; nothing here touches it.
/// </remarks>
internal static class ShortTextGlyphHeight
{
    /// <summary>
    /// Glyph count from which the pitch clamp can apply. See <see cref="PitchClampApplies"/>.
    /// </summary>
    public const int PitchCorrectedFromGlyphs = 4;

    /// <summary>
    /// Whether the glyph height estimate clamps a line against its glyph pitch. This correction
    /// applies exactly where that does not, so the two are one condition rather than two copies of it
    /// that can drift apart.
    /// </summary>
    public static bool PitchClampApplies(double boxWidth, double boxHeight, int glyphCount)
        => glyphCount >= PitchCorrectedFromGlyphs && boxWidth > boxHeight * 2;

    /// <summary>
    /// Fraction of a detection box its glyphs actually occupy. The low end of the measured
    /// 0.48–0.54 band: overshooting puts an oversized translation over the picture, undershooting
    /// only makes it slightly small.
    /// </summary>
    public const double GlyphsToBoxHeight = 0.5;

    /// <param name="estimated">Glyph height as computed so far, in the same units as the box.</param>
    /// <param name="boxWidth">Width of the box the estimate was made from.</param>
    /// <param name="boxHeight">Height of the detection box the text was found in.</param>
    /// <param name="glyphCount">Non-whitespace characters in the recognised line.</param>
    public static double For(double estimated, double boxWidth, double boxHeight, int glyphCount)
        => For(estimated, boxWidth, boxHeight, glyphCount, out _);

    /// <param name="correction">
    /// Whether this was in a position to change the estimate, and whether it did. Reported from
    /// here rather than worked out by the caller: the guard below is the definition of "applied",
    /// and a second copy of it elsewhere is one that can come to disagree.
    /// </param>
    /// <inheritdoc cref="For(double, double, double, int)"/>
    public static double For(
        double estimated, double boxWidth, double boxHeight, int glyphCount, out Correction correction)
    {
        if (PitchClampApplies(boxWidth, boxHeight, glyphCount) || boxHeight <= 0)
        {
            correction = new Correction(false, null, false);
            return estimated;
        }

        var candidate = boxHeight * GlyphsToBoxHeight;
        var corrected = Math.Min(estimated, candidate);
        correction = new Correction(true, candidate, corrected < estimated);

        return corrected;
    }

    /// <summary>What this correction did to one line, for the diagnostics to print.</summary>
    /// <param name="Applied">Whether the pitch clamp left the line to this correction.</param>
    /// <param name="Candidate">The height the box alone would give, or null where it was not consulted.</param>
    /// <param name="Selected">Whether that came out lower and replaced the estimate.</param>
    public readonly record struct Correction(bool Applied, double? Candidate, bool Selected);

    /// <summary>Characters that occupy width, which is what "how short is this line" means here.</summary>
    public static int GlyphsIn(string text) => text.Count(c => !char.IsWhiteSpace(c));
}
