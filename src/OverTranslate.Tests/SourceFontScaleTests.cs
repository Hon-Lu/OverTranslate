using OverTranslate.Layout;
using Xunit;

namespace OverTranslate.Tests;

public class SourceFontScaleTests
{
    // The defect this class exists to prevent: OCR box heights for ordinary UI text land at 12-16,
    // and any change to how the detector is fed moves them by a fraction of a pixel. The font must
    // move by a comparable fraction, not jump.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeighbouringSourceHeights_ProduceNeighbouringFontSizes(bool latinToCjk)
    {
        for (var height = 8.0; height <= 30.0; height += 0.1)
        {
            var here = SourceFontScale.Calculate(height, latinToCjk);
            var justAbove = SourceFontScale.Calculate(height + 0.1, latinToCjk);

            // A 0.1px step in the source may not move the font by more than 0.25px. The old
            // `height <= 14` switch moved it by ~2.8px at the boundary.
            Assert.True(
                justAbove - here <= 0.25,
                $"font jumped from {here:F2} to {justAbove:F2} between heights {height:F1} and {height + 0.1:F1}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargerSourceText_NeverProducesASmallerFont(bool latinToCjk)
    {
        for (var height = 8.0; height <= 30.0; height += 0.1)
        {
            Assert.True(
                SourceFontScale.Calculate(height + 0.1, latinToCjk)
                    >= SourceFontScale.Calculate(height, latinToCjk) - 1e-9,
                $"font shrank as the source grew past {height:F1}");
        }
    }

    // Outside the blend band the ratios are the ones the overlay has always used, so captures of
    // clearly-small or clearly-large text render exactly as they did before.
    [Theory]
    [InlineData(10.0, 14.5)]  // floor wins: 10 * 1.18 = 11.8
    [InlineData(12.0, 14.5)]  // floor wins: 12 * 1.18 = 14.16
    [InlineData(20.0, 21.2)]  // 20 * 1.06
    [InlineData(28.0, 29.68)] // 28 * 1.06
    public void OutsideTheBlendBand_TheOriginalRatiosStillApply(double sourceHeight, double expected)
    {
        Assert.Equal(expected, SourceFontScale.Calculate(sourceHeight, latinSourceToCjkTarget: false), 2);
    }

    [Fact]
    public void SmallLatinRenderedAsCjk_GetsTheBoost_AndLargeDoesNot()
    {
        // At the small end the boost is the full 1.08 on the raw size; the readability floor is
        // applied after it, so a boosted block is never pulled back down to the floor.
        Assert.Equal(12.0 * 1.18 * 1.08, SourceFontScale.Calculate(12.0, latinSourceToCjkTarget: true), 2);
        Assert.True(
            SourceFontScale.Calculate(12.0, latinSourceToCjkTarget: true)
                > SourceFontScale.Calculate(12.0, latinSourceToCjkTarget: false));

        var large = SourceFontScale.Calculate(20.0, latinSourceToCjkTarget: false);
        Assert.Equal(large, SourceFontScale.Calculate(20.0, latinSourceToCjkTarget: true), 2);
    }

    // The concrete regression: a detector change that shifts a measured height by half a pixel
    // must not visibly resize the text. Across the old threshold this went 14.95 -> 17.7 (+18%).
    [Fact]
    public void HalfAPixelAcrossTheOldThreshold_BarelyChangesTheFont()
    {
        var before = SourceFontScale.Calculate(14.1, latinSourceToCjkTarget: true);
        var after = SourceFontScale.Calculate(13.6, latinSourceToCjkTarget: true);

        Assert.True(
            Math.Abs(before - after) / before < 0.05,
            $"font changed from {before:F2} to {after:F2} for a 0.5px shift in the source height");
    }
}
