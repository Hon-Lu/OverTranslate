using OverTranslate.Layout;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What vertical writing draws for punctuation, by target: Japanese in its vertical form, Chinese
/// centred with curly quotes as corner brackets, everything else as it is.
/// </summary>
public class VerticalFormTests
{
    private const string SentencePunctuation = "。｡、､，";
    private const string SimplifiedCentred = "。｡、､，！？：；";

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
            Assert.False(drawn.Rotates);
        }
    }

    /// <remarks>JhengHei already draws these centred, which is where they belong.</remarks>
    [Fact]
    public void TraditionalChinese_DrawsPunctuationAsItIs()
    {
        foreach (var glyph in SimplifiedCentred)
        foreach (var target in new[] { "ZH-HANT", "zh-hant" })
            Assert.Equal(new VerticalTextGrid.VerticalGlyph(glyph, null), VerticalTextGrid.VerticalGlyphFor(glyph, target));
    }

    /// <remarks>Same character, but in JhengHei, which centres it; YaHei puts it to one side.</remarks>
    [Fact]
    public void SimplifiedChinese_DrawsCentredPunctuationInJhengHei()
    {
        foreach (var glyph in SimplifiedCentred)
        foreach (var target in new[] { "ZH", "ZH-HANS", "zh-hans" })
        {
            var drawn = VerticalTextGrid.VerticalGlyphFor(glyph, target);
            Assert.Equal(glyph, drawn.Glyph);
            Assert.NotNull(drawn.Font);
            Assert.Equal(TranslatedTextFont.FamilyList("ZH-HANT"), drawn.Font!.Source);
            Assert.False(drawn.Rotates);
        }
    }

    [Theory]
    [InlineData('漢')]
    [InlineData('あ')]
    [InlineData('っ')]
    [InlineData('．')]
    [InlineData('!')]
    [InlineData('?')]
    [InlineData('A')]
    [InlineData('·')]
    public void SimplifiedChinese_LeavesEverythingElseInYaHei(char glyph)
    {
        foreach (var target in new[] { "ZH", "ZH-HANS" })
            Assert.Equal(new VerticalTextGrid.VerticalGlyph(glyph, null), VerticalTextGrid.VerticalGlyphFor(glyph, target));
    }

    /// <remarks>
    /// Drawn as the corner brackets Chinese vertical writing quotes with, in the column's own font,
    /// and turned like the corner brackets the translation already had.
    /// </remarks>
    [Theory]
    [InlineData('“', '「')]
    [InlineData('”', '」')]
    [InlineData('‘', '『')]
    [InlineData('’', '』')]
    public void Chinese_DrawsCurlyQuotesAsTurnedCornerBrackets(char quote, char bracket)
    {
        foreach (var target in new[] { "ZH", "ZH-HANS", "ZH-HANT", "zh-hant" })
        {
            var drawn = VerticalTextGrid.VerticalGlyphFor(quote, target);
            Assert.Equal(bracket, drawn.Glyph);
            Assert.Null(drawn.Font);
            Assert.True(drawn.Rotates);
            // The bracket the translation already had is drawn exactly the same way.
            Assert.Equal(drawn, VerticalTextGrid.VerticalGlyphFor(bracket, target));
        }
    }

    [Theory]
    [InlineData("JA")]
    [InlineData("KO")]
    [InlineData("EN")]
    [InlineData(null)]
    public void OtherTargets_LeaveCurlyQuotesAlone(string? target)
    {
        foreach (var quote in "“”‘’")
        {
            var drawn = VerticalTextGrid.VerticalGlyphFor(quote, target);
            Assert.Equal(new VerticalTextGrid.VerticalGlyph(quote, null), drawn);
            Assert.False(drawn.Rotates);
        }
    }

    /// <remarks>
    /// The baseline … turned hugs the left of the cell; the midline ⋯ turned is centred. Still
    /// turned, still in the column's own font, and a ⋯ already in the translation is left as it is.
    /// </remarks>
    [Fact]
    public void Chinese_DrawsTheEllipsisAsTheMidlineOne()
    {
        foreach (var target in new[] { "ZH", "ZH-HANS", "ZH-HANT", "zh-hant" })
        {
            var drawn = VerticalTextGrid.VerticalGlyphFor('…', target);
            Assert.Equal(new VerticalTextGrid.VerticalGlyph('⋯', null), drawn);
            Assert.True(drawn.Rotates);
            Assert.Equal(drawn, VerticalTextGrid.VerticalGlyphFor('⋯', target));
        }
    }

    [Theory]
    [InlineData("JA")]
    [InlineData("KO")]
    [InlineData("EN")]
    [InlineData(null)]
    public void OtherTargets_KeepTheEllipsis(string? target)
    {
        var drawn = VerticalTextGrid.VerticalGlyphFor('…', target);
        Assert.Equal(new VerticalTextGrid.VerticalGlyph('…', null), drawn);
        Assert.True(drawn.Rotates);
    }

    [Theory]
    [InlineData("KO")]
    [InlineData("EN")]
    [InlineData("EN-US")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherTargets_AreDrawnAsTheyAre(string? target)
    {
        foreach (var glyph in SimplifiedCentred)
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
    /// Turning is decided by what is drawn. A vertical form is already upright for a column, and a
    /// borrowed centred glyph is upright too; turning either would lay it on its side.
    /// </remarks>
    [Fact]
    public void SwappedPunctuation_IsNotTurned()
    {
        foreach (var glyph in SentencePunctuation)
            Assert.False(VerticalTextGrid.VerticalGlyphFor(glyph, "JA").Rotates);
        foreach (var glyph in SimplifiedCentred)
        foreach (var target in new[] { "ZH", "ZH-HANS", "ZH-HANT" })
            Assert.False(VerticalTextGrid.VerticalGlyphFor(glyph, target).Rotates);
    }

    /// <remarks>What was turned before is still turned, for every target.</remarks>
    [Fact]
    public void AlreadyTurnedGlyphs_StayTurned()
    {
        foreach (var glyph in "「」『』（）【】《》〈〉—～")
        foreach (var target in new[] { "JA", "ZH", "ZH-HANS", "ZH-HANT", "KO", "EN" })
            Assert.True(VerticalTextGrid.VerticalGlyphFor(glyph, target).Rotates);
    }
}
