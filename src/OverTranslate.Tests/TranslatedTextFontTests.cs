using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Which family the screenshot and realtime overlays set a translation in, by its target language.
/// </summary>
public class TranslatedTextFontTests
{
    /// <remarks>
    /// The CJK targets ignore the interface language entirely: the translation is in the target's
    /// script, and its punctuation and Han shapes have to be that script's whatever the menus are in.
    /// </remarks>
    [Theory]
    [InlineData("JA",      "Yu Gothic UI, Segoe UI, Sans-Serif")]
    [InlineData("KO",      "Malgun Gothic, Segoe UI, Sans-Serif")]
    [InlineData("ZH",      "Microsoft YaHei, Segoe UI, Sans-Serif")]
    [InlineData("ZH-HANS", "Microsoft YaHei, Segoe UI, Sans-Serif")]
    [InlineData("ZH-HANT", "Microsoft JhengHei, Segoe UI, Sans-Serif")]
    public void CjkTarget_IsSetInItsOwnScript(string target, string expected)
    {
        foreach (var ui in new[]
                 {
                     LocalizationService.TraditionalChinese, LocalizationService.SimplifiedChinese,
                     LocalizationService.English, LocalizationService.Japanese, LocalizationService.Korean,
                 })
            Assert.Equal(expected, TranslatedTextFont.FamilyList(target, ui));
    }

    [Theory]
    [InlineData("ja",      "Yu Gothic UI, Segoe UI, Sans-Serif")]
    [InlineData("zh-Hans", "Microsoft YaHei, Segoe UI, Sans-Serif")]
    [InlineData("zh-hant", "Microsoft JhengHei, Segoe UI, Sans-Serif")]
    [InlineData("Ko",      "Malgun Gothic, Segoe UI, Sans-Serif")]
    public void TargetCode_IsCaseInsensitive(string target, string expected) =>
        Assert.Equal(expected, TranslatedTextFont.FamilyList(target, LocalizationService.English));

    /// <remarks>
    /// Segoe UI leads, and the CJK family behind it — there for an untranslated name — follows the
    /// interface. English falls back to JhengHei, as the English interface font does.
    /// </remarks>
    [Theory]
    [InlineData(LocalizationService.TraditionalChinese, "Segoe UI, Microsoft JhengHei, Sans-Serif")]
    [InlineData(LocalizationService.SimplifiedChinese,  "Segoe UI, Microsoft YaHei, Sans-Serif")]
    [InlineData(LocalizationService.Japanese,           "Segoe UI, Yu Gothic UI, Sans-Serif")]
    [InlineData(LocalizationService.Korean,             "Segoe UI, Malgun Gothic, Sans-Serif")]
    [InlineData(LocalizationService.English,            "Segoe UI, Microsoft JhengHei, Sans-Serif")]
    public void NonCjkTarget_FallsBackToTheInterfaceCjkFamily(string ui, string expected)
    {
        foreach (var target in new[] { "EN", "EN-US", "DE", "RU" })
            Assert.Equal(expected, TranslatedTextFont.FamilyList(target, ui));
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("ZH-TW")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownTarget_IsTreatedAsNonCjk(string? target) =>
        Assert.Equal(
            "Segoe UI, Yu Gothic UI, Sans-Serif",
            TranslatedTextFont.FamilyList(target, LocalizationService.Japanese));

    [Fact]
    public void For_BuildsTheSameFamilyList() =>
        Assert.Equal(
            "Yu Gothic UI, Segoe UI, Sans-Serif",
            TranslatedTextFont.For("JA", LocalizationService.English).Source);
}
