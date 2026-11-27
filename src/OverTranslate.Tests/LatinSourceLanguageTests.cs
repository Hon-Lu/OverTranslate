using OverTranslate.Models;
using OverTranslate.Services.Ocr;
using RapidOcrNet;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// #441: French, German, Spanish, Italian and Portuguese as OCR source languages. The built-in model
/// already carries their letters; what they need is not to have them folded away.
/// </summary>
public class LatinSourceLanguageTests
{
    public static TheoryData<string> Languages() => new() { "FR", "DE", "ES", "IT", "PT" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void ReadByTheBuiltInModel_WithNoPackToDownload(string code)
    {
        Assert.True(OcrLanguageRouter.IsSupported(code));
        Assert.Equal("cjk", OnnxOcrEngine.GetModelKeyForLanguage(code));
        Assert.Null(OcrLanguagePacks.ForCode(code));
        Assert.True(OcrLanguagePacks.IsUsable(code));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ASavedChoice_IsKept(string code)
    {
        // The report: 法文 in appsettings came back as 英文.
        Assert.Equal(code, LanguageData.GetValidOcrSourceCode(code));
        Assert.Equal(code, LanguageData.GetValidOcrSourceCode(code.ToLowerInvariant()));
        Assert.Equal(code, LanguageData.GetValidRealtimeSourceCode(code));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void KeepsAccentedLetters(string code)
    {
        var text = Filter(code, "Où est passé mon cœur ? Größe, año, città, coração");

        Assert.Equal("Où est passé mon cœur ? Größe, año, città, coração", text);
    }

    [Theory]
    [InlineData("EN")]
    [InlineData("AUTO")]
    [InlineData("JA")]
    public void EveryOtherLanguage_StillFoldsThem(string code)
    {
        // The misread the fold exists for: Romanian ș in an English line, read at 0.93.
        Assert.Equal("That kind of song", Filter(code, "That kind of șong"));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void SizedLikeEnglish_IntoChineseJapaneseOrKorean(string code) =>
        Assert.True(OcrLanguageRouter.IsLatinScript(code));

    [Theory]
    [InlineData("JA")]
    [InlineData("RU")]
    [InlineData("AUTO")]
    public void OtherScripts_AreNotLatin(string code) =>
        Assert.False(OcrLanguageRouter.IsLatinScript(code));

    [Theory]
    [InlineData("café", "noir", "café noir")]
    [InlineData("Paramètres", "élevée", "Paramètres élevée")]
    [InlineData("Größe", "ändern", "Größe ändern")]
    [InlineData("sœur", "aînée", "sœur aînée")]
    [InlineData("Ñandú", "y", "Ñandú y")]
    [InlineData("città", "45", "città 45")]
    public void JoinInlineText_SpacesAccentedWords(string left, string right, string expected) =>
        Assert.Equal(expected, OcrTextBlockGrouper.JoinInlineText(left, right));

    [Theory]
    [InlineData("3", "×", "3×")]
    [InlineData("10", "÷2", "10÷2")]
    [InlineData("é,", "bar", "é,bar")]
    public void JoinInlineText_LeavesSignsAndPunctuationAsTheyWere(string left, string right, string expected) =>
        Assert.Equal(expected, OcrTextBlockGrouper.JoinInlineText(left, right));

    private static string Filter(string language, string text) =>
        Assert.Single(OnnxOcrEngine.ApplyBlockFilters(
            [Detected(text)], language,
            OcrLanguageRouter.UsesCjkOnnx(language),
            OcrLanguageRouter.UsesAutomaticLayout(language))).Text;

    private static TextBlock Detected(string text) =>
        new()
        {
            BoxPoints = [new SKPointI(40, 40), new SKPointI(640, 40), new SKPointI(640, 70), new SKPointI(40, 70)],
            BoxScore = 0.9f,
            Text = text,
            Chars = [.. text.Select(c => c.ToString())],
            CharScores = [.. text.Select(_ => 0.99f)],
        };
}
