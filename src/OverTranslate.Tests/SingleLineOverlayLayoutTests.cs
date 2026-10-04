using OverTranslate.Layout;
using Xunit;

namespace OverTranslate.Tests;

public class SingleLineOverlayLayoutTests
{
    [Fact]
    public void KeepsOriginalFontAndExpandsRight_WhenThereIsEnoughRoom()
    {
        var result = Calculate(textWidth: 120, rightExpansionWidth: 130);

        Assert.Equal(16, result.FontSize);
        Assert.Equal(126, result.BorderWidth);
        Assert.True(result.PreferRightExpansion);
    }

    [Fact]
    public void DoesNotGrowFontAboveOriginal_WhenTranslationIsMuchShorter()
    {
        // A short translation of a wide source line must not be scaled up to fill the box.
        var result = Calculate(textWidth: 40, rightExpansionWidth: 80);

        Assert.Equal(16, result.FontSize, 3);
        Assert.Equal(106, result.BorderWidth, 3);
        Assert.False(result.PreferRightExpansion);
    }

    [Fact]
    public void ShrinksInsideOriginalWidth_BeforeExpandingBothSides()
    {
        var result = Calculate(textWidth: 160, rightExpansionWidth: 80);

        Assert.Equal(10, result.FontSize, 3);
        Assert.Equal(106, result.BorderWidth, 3);
        Assert.False(result.PreferRightExpansion);
    }

    [Fact]
    public void ExpandsBothSides_WhenOriginalWidthWouldDropBelowReadableThreshold()
    {
        var result = Calculate(textWidth: 220, rightExpansionWidth: 80);

        Assert.Equal(10, result.FontSize, 3);
        Assert.Equal(143.5, result.BorderWidth, 3);
        Assert.False(result.PreferRightExpansion);
    }

    [Fact]
    public void FallsBackToAbsoluteMinimum_WhenEvenExpandedWidthIsNotEnough()
    {
        var result = Calculate(textWidth: 400, rightExpansionWidth: 80, maxExpandedWidth: 140);

        Assert.Equal(9.5, result.FontSize, 3);
        Assert.Equal(140, result.BorderWidth);
        Assert.False(result.PreferRightExpansion);
    }

    private static SingleLineOverlayResult Calculate(
        double textWidth,
        double rightExpansionWidth,
        double maxExpandedWidth = 300) =>
        SingleLineOverlayLayout.Calculate(new(
            OriginalFontSize: 16,
            OriginalBorderWidth: 106,
            TextWidth: textWidth,
            HorizontalPadding: 6,
            RightExpansionWidth: rightExpansionWidth,
            MaxExpandedWidth: maxExpandedWidth,
            ReadableMinFontSize: 10,
            AbsoluteMinFontSize: 9.5));
}
