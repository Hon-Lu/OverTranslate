using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// The page number in the margin, and everything else a translator would hand straight back.
/// </summary>
public class VerticalWordlessGroupTests
{
    private static OcrTextBlock Group(string text) =>
        new(text, new Rect(0, 0, 30, 30)) { LayoutBounds = new Rect(0, 0, 30, 30) };

    /// <summary>The page numbers off the fifteen comic pages, read perfectly every time.</summary>
    [Theory]
    [InlineData("45")]
    [InlineData("11")]
    [InlineData("444")]
    [InlineData("8")]
    [InlineData("!?")]
    [InlineData("——")]
    public void A_group_with_no_word_in_it_is_dropped(string text)
    {
        Assert.Empty(VerticalColumnGrouping.WithoutWordlessGroups([Group(text)]));
    }

    /// <summary>
    /// Shorter than a page number and still a line of dialogue: length is not what this asks.
    /// </summary>
    [Theory]
    [InlineData("ん？")]
    [InlineData("これが")]
    [InlineData("8年前")]
    [InlineData("100層")]
    [InlineData("Sランク")]
    public void A_group_holding_a_word_is_kept(string text)
    {
        Assert.Single(VerticalColumnGrouping.WithoutWordlessGroups([Group(text)]));
    }
}
