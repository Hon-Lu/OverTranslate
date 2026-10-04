using OverTranslate.Layout;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeBandPlacementTests
{
    [Fact]
    public void ShorterTranslation_StaysCentredOnItsSource()
    {
        // A 400px subtitle whose translation only needs 200px: the band must lose 100px from each
        // side, not 200px from the right, which is what pinning it to the source's left edge did.
        var left = RealtimeBandPlacement.Left(sourceLeft: 100, sourceWidth: 400, bandWidth: 200, blockWidth: 1000);

        Assert.Equal(200, left);
        Assert.Equal(300, left + 200 / 2.0); // same centre as the source (100 + 400/2)
    }

    [Fact]
    public void LongerTranslation_GrowsEquallyToBothSides()
    {
        var left = RealtimeBandPlacement.Left(sourceLeft: 400, sourceWidth: 200, bandWidth: 400, blockWidth: 1000);

        Assert.Equal(300, left);
        Assert.Equal(500, left + 400 / 2.0); // same centre as the source (400 + 200/2)
    }

    [Fact]
    public void SameWidthTranslation_SitsExactlyOverItsSource()
    {
        Assert.Equal(120, RealtimeBandPlacement.Left(120, 300, 300, 1000));
    }

    [Theory]
    // Centring would put the band past the left edge of the block -> pushed back in.
    [InlineData(10, 100, 300, 1000, 0)]
    // ...and past the right edge -> pushed back in.
    [InlineData(900, 80, 300, 1000, 700)]
    public void BandNearAnEdge_IsPushedInsideTheBlock(
        double sourceLeft, double sourceWidth, double bandWidth, double blockWidth, double expected)
    {
        Assert.Equal(expected, RealtimeBandPlacement.Left(sourceLeft, sourceWidth, bandWidth, blockWidth));
    }

    [Fact]
    public void BandWiderThanTheBlock_StartsAtTheBlockEdge()
    {
        // Nothing can contain it, so the start of the sentence is what is kept visible.
        Assert.Equal(0, RealtimeBandPlacement.Left(50, 200, 1200, 1000));
    }
}
