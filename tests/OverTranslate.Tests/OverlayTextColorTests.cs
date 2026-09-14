using OverTranslate.Services;
using Xunit;
using MediaColor = System.Windows.Media.Color;

namespace OverTranslate.Tests;

public class OverlayTextColorTests
{
    [Fact]
    public void EnsureContrast_LeavesAReadablePairUntouched()
    {
        var text = MediaColor.FromRgb(255, 255, 255);
        var background = MediaColor.FromRgb(0x33, 0x39, 0x46);

        Assert.Equal(text, OverlayTextColor.EnsureContrast(text, background, OverlayTextColor.MinimumContrast));
    }

    [Fact]
    public void EnsureContrast_RescuesWhitePosterTextTunedIntoItsPaleCard()
    {
        // A white caption on a light grey card: Tune treats the card as light and pulls the text down
        // to #BDBDBD, which is the card's own colour.
        var background = MediaColor.FromRgb(0xC8, 0xC8, 0xC8);
        var tuned = OverlayTextColor.Tune(MediaColor.FromRgb(255, 255, 255), background);
        Assert.True(OverlayTextColor.ContrastRatio(tuned, background) < 1.5);

        var ensured = OverlayTextColor.EnsureContrast(tuned, background, OverlayTextColor.MinimumContrast);

        Assert.True(OverlayTextColor.ContrastRatio(ensured, background) >= OverlayTextColor.MinimumContrast);
    }

    [Fact]
    public void EnsureContrast_KeepsTheHueOfSaturatedTextOnADarkBackgroundOfTheSameHue()
    {
        var background = MediaColor.FromRgb(90, 20, 20);
        var tuned = OverlayTextColor.Tune(MediaColor.FromRgb(200, 30, 30), background);

        var ensured = OverlayTextColor.EnsureContrast(tuned, background, OverlayTextColor.MinimumContrast);

        Assert.True(OverlayTextColor.ContrastRatio(ensured, background) >= OverlayTextColor.MinimumContrast);
        Assert.True(ensured.R > ensured.G && ensured.R > ensured.B);
    }
}
