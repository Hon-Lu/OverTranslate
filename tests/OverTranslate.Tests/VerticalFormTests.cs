using OverTranslate.Layout;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What vertical writing draws for sentence punctuation, by target: Japanese in its vertical form,
/// Chinese centred, everything else as it is.
/// </summary>
public class VerticalFormTests
{
    private const string Punctuation = "。｡、､，";

    [Theory]
    [InlineData('。', '︒')]
    [InlineData('｡', '︒')]
    [InlineData('、', '︑')]
    [InlineData('､', '︑')]
    [InlineData('，', '︐')]
    public void Japanese_DrawsTheVerticalForm(char glyph, char vertical)
    {
        foreach (var target in new[] { "JA", "ja" })
        {
            var drawn = VerticalTextGrid.VerticalGlyphFor(glyph, target);
            Assert.Equal(vertical, drawn.Glyph);
            Assert.Null(drawn.Font);
        }
    }

    /// <remarks>JhengHei already draws these centred, which is where they belong.</remarks>
    [Fact]
    public void TraditionalChinese_DrawsPunctuationAsItIs()
    {
        foreach (var glyph in Punctuation)
        foreach (var target in new[] { "ZH-HANT", "zh-hant" })
            Assert.Equal(new VerticalTextGrid.VerticalGlyph(glyph, null), VerticalTextGrid.VerticalGlyphFor(glyph, target));
    }

    /// <remarks>Same character, but in JhengHei, which centres it; YaHei puts it bottom left.</remarks>
    [Fact]
    public void SimplifiedChinese_DrawsPunctuationInJhengHei()
    {
        foreach (var glyph in Punctuation)
        foreach (var target in new[] { "ZH", "ZH-HANS", "zh-hans" })
        {
            var drawn = VerticalTextGrid.VerticalGlyphFor(glyph, target);
            Assert.Equal(glyph, drawn.Glyph);
            Assert.NotNull(drawn.Font);
            Assert.Equal(TranslatedTextFont.FamilyList("ZH-HANT"), drawn.Font!.Source);
        }
    }

    [Theory]
    [InlineData('漢')]
    [InlineData('あ')]
    [InlineData('っ')]
    [InlineData('！')]
    [InlineData('？')]
    [InlineData('．')]
    [InlineData('A')]
    public void SimplifiedChinese_LeavesEverythingElseInYaHei(char glyph)
    {
        foreach (var target in new[] { "ZH", "ZH-HANS" })
            Assert.Equal(new VerticalTextGrid.VerticalGlyph(glyph, null), VerticalTextGrid.VerticalGlyphFor(glyph, target));
    }

    [Theory]
    [InlineData("KO")]
    [InlineData("EN")]
    [InlineData("EN-US")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherTargets_AreDrawnAsTheyAre(string? target)
    {
        foreach (var glyph in Punctuation)
            Assert.Equal(new VerticalTextGrid.VerticalGlyph(glyph, null), VerticalTextGrid.VerticalGlyphFor(glyph, target));
    }

    /// <remarks>Small kana included: Unicode has no vertical forms for them, so they stay centred.</remarks>
    [Theory]
    [InlineData('漢')]
    [InlineData('あ')]
    [InlineData('ア')]
    [InlineData('っ')]
    [InlineData('ョ')]
    [InlineData('！')]
    [InlineData('？')]
    [InlineData('．')]
    [InlineData('A')]
    [InlineData(',')]
    [InlineData('.')]
    public void Japanese_LeavesOtherGlyphsAlone(char glyph) =>
        Assert.Equal(new VerticalTextGrid.VerticalGlyph(glyph, null), VerticalTextGrid.VerticalGlyphFor(glyph, "JA"));

    /// <remarks>
    /// The vertical form is already upright for a column; turning it as well would lay it on its side.
    /// Both sides of the swap are checked, since the cell decides whether to turn from the original.
    /// </remarks>
    [Theory]
    [InlineData('。')]
    [InlineData('｡')]
    [InlineData('、')]
    [InlineData('､')]
    [InlineData('，')]
    public void NeitherSideOfTheSwap_IsTurned(char glyph)
    {
        Assert.False(VerticalTextGrid.RotatesGlyph(glyph));
        Assert.False(VerticalTextGrid.RotatesGlyph(VerticalTextGrid.VerticalGlyphFor(glyph, "JA").Glyph));
    }
}
