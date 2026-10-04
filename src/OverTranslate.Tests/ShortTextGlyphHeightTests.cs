using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Both overlays size their font from the glyph height this corrects, so a mistake here is text
/// drawn at the wrong size over the user's screen — in the screenshot flow as much as the live one.
/// </summary>
public class ShortTextGlyphHeightTests
{
    [Fact]
    public void ATwoLetterLineIsSizedFromItsBoxRatherThanTheOverblownEstimate()
    {
        // The measured case: "YA" drawn at a 64px em came back in a 95px box, reported as 78
        // against a true glyph height of about 46.
        Assert.Equal(47.5, ShortTextGlyphHeight.For(estimated: 78, boxWidth: 40, boxHeight: 95, glyphCount: 2));
    }

    [Fact]
    public void ALongLineIsLeftAloneBecauseThePitchClampAlreadyCorrectedIt()
    {
        // "Hello there friend" at the same em: pitch had already brought 70.5 down to 45.5, which
        // is right to within a pixel, and halving its box would take it to 43 for no reason.
        Assert.Equal(45.5, ShortTextGlyphHeight.For(estimated: 45.5, boxWidth: 300, boxHeight: 86, glyphCount: 16));
    }

    [Fact]
    public void TheCorrectionOnlyEverMakesTextSmaller()
    {
        // A tight box on short text needs no correction, and this must never invent height.
        Assert.Equal(20, ShortTextGlyphHeight.For(estimated: 20, boxWidth: 60, boxHeight: 60, glyphCount: 2));
    }

    [Fact]
    public void AMissingBoxLeavesTheEstimateUntouched()
    {
        Assert.Equal(30, ShortTextGlyphHeight.For(estimated: 30, boxWidth: 40, boxHeight: 0, glyphCount: 1));
    }

    [Fact]
    public void TheBoundaryMatchesTheClampItStandsInFor()
    {
        // Three glyphs is the last length the pitch clamp does not cover, so it is the last one
        // corrected here. Off by one in either direction and a length is either corrected twice or
        // not at all.
        Assert.Equal(30, ShortTextGlyphHeight.For(estimated: 60, boxWidth: 500, boxHeight: 60, glyphCount: 3));
        Assert.Equal(60, ShortTextGlyphHeight.For(estimated: 60, boxWidth: 500, boxHeight: 60, glyphCount: 4));
    }

    [Fact]
    public void ANarrowLineOfFourGlyphsIsCorrectedLikeAShortOne()
    {
        // The measured case: "TOO." on region-comic-en-3 (2), four glyphs in a 55x32 box. Not wide
        // enough for the pitch clamp, so it kept 0.82 of the box and its translation came out
        // larger than every other line on the card.
        Assert.Equal(16, ShortTextGlyphHeight.For(estimated: 26.24, boxWidth: 55, boxHeight: 32, glyphCount: 4));
    }

    [Fact]
    public void TheWidthBoundaryIsTheClampsOwn()
    {
        // Exactly twice as wide as tall is the last width the clamp refuses, so it is the last one
        // corrected here; a hair past it the clamp has run and this must stay out.
        Assert.False(ShortTextGlyphHeight.PitchClampApplies(boxWidth: 64, boxHeight: 32, glyphCount: 4));
        Assert.Equal(16, ShortTextGlyphHeight.For(estimated: 26.24, boxWidth: 64, boxHeight: 32, glyphCount: 4));

        Assert.True(ShortTextGlyphHeight.PitchClampApplies(boxWidth: 64.0001, boxHeight: 32, glyphCount: 4));
        Assert.Equal(26.24, ShortTextGlyphHeight.For(estimated: 26.24, boxWidth: 64.0001, boxHeight: 32, glyphCount: 4));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    public void TheCorrectionIsConsultedExactlyWhereThePitchClampIsNot(int glyphCount)
    {
        // The two are one condition: a line either side of it gets one of the two, never both and
        // never neither.
        foreach (var width in new[] { 10, 40, 63.9, 64, 64.0001, 65, 200, 1000 })
        {
            ShortTextGlyphHeight.For(estimated: 26.24, boxWidth: width, boxHeight: 32, glyphCount, out var correction);

            Assert.NotEqual(ShortTextGlyphHeight.PitchClampApplies(width, 32, glyphCount), correction.Applied);
        }
    }
}
