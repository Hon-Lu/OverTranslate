using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Rejecting a real word here means it is never translated, so every case below is taken from the
/// 2533 blocks the rule was measured against rather than invented.
/// </summary>
public class BoxShapeNoiseTests
{
    // As the engine hands it over: the rule reads LayoutBounds, the box the detector drew, so a
    // fixture that only sets Bounds is not a block this rule has ever been asked about.
    private static OcrTextBlock Block(string text, double width, double height) =>
        new OcrTextBlock(text, new Rect(100, 100, width, height)).AsDetected();

    [Theory]
    // One character in a box six times wider than tall, which is what a detector returns for a
    // strip of interface it could not read. 126 of these in the sample.
    [InlineData("□", 300, 40)]
    [InlineData("□2", 264, 38)]
    [InlineData("[", 120, 20)]
    [InlineData("[0]2|", 500, 30)]
    public void ABoxTooWideForItsCharactersIsRejected(string text, double width, double height)
    {
        Assert.True(BoxShapeNoise.IsTooWideForItsText(Block(text, width, height)));
    }

    [Theory]
    // Real short words, which is what the rule must not touch. CJK glyphs are square, so a
    // three-character word is about three times as wide as it is tall — well under the bar.
    [InlineData("知らせ", 120, 40)]
    [InlineData("お知らせ", 160, 40)]
    [InlineData("仲町あられ", 200, 40)]
    [InlineData("이벤트", 130, 42)]
    [InlineData("YA", 135, 95)]
    [InlineData("Yay!", 90, 60)]
    [InlineData("Marina-san, are you okay?", 487, 59)]
    public void RealTextIsKept(string text, double width, double height)
    {
        Assert.False(BoxShapeNoise.IsTooWideForItsText(Block(text, width, height)));
    }

    [Fact]
    public void ABoxWithNoHeightIsNotJudged()
    {
        // Nothing to measure against; leaving it alone is the safe answer.
        Assert.False(BoxShapeNoise.IsTooWideForItsText(Block("□", 300, 0)));
    }

    [Fact]
    public void WhitespaceDoesNotCountTowardsTheCharacters()
    {
        // Otherwise padding a lone glyph with spaces would talk the rule out of rejecting it.
        Assert.True(BoxShapeNoise.IsTooWideForItsText(Block("  □  ", 300, 40)));
    }
}
