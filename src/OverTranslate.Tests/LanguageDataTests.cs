using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using Xunit;

namespace OverTranslate.Tests;

public class LanguageDataTests
{
    // The pickers carry codes chosen for the translation APIs; a local translation model wants BCP 47
    // instead, and the tag is the half it was trained to key on rather than decoration beside a name.
    [Theory]
    [InlineData("JA", "ja")]
    [InlineData("KO", "ko")]
    [InlineData("EN", "en")]
    [InlineData("EN-US", "en")]          // DeepL's regional code; the model wants the plain one
    [InlineData("ZH", "zh-Hans")]        // 簡體中文 in this application's own OCR picker
    [InlineData("ZH-HANS", "zh-Hans")]
    [InlineData("ZH-HANT", "zh-Hant")]
    [InlineData("PT-BR", "pt-BR")]       // keeps its region, unlike EN-US
    [InlineData("BG", "bg")]             // no entry in the table; lower-casing is already right
    public void ModelLanguageTag_IsTheFormATranslationModelExpects(string code, string expected) =>
        Assert.Equal(expected, LanguageData.GetModelLanguageTag(code));

    [Theory]
    [InlineData("AUTO")]
    [InlineData("")]
    [InlineData(null)]
    public void ModelLanguageTag_IsEmptyWhenThereIsNoLanguageToName(string? code) =>
        // 自動 is not a language, so naming one would be inventing it — the prompt says "any
        // language" in that position and must not gain a tag.
        Assert.Equal("", LanguageData.GetModelLanguageTag(code));

    // The two keyless engines for users Google cannot reach go before the two that need setting up
    // first: DeepL a key, OpenAI a model.
    [Theory]
    [InlineData(TranslationProvider.Youdao, "S.Provider.Youdao")]
    [InlineData(TranslationProvider.TranSmart, "S.Provider.TranSmart")]
    public void KeylessChineseEngines_AreListedBeforeDeepL(TranslationProvider engine, string displayKey)
    {
        var order = LanguageData.Providers.Select(item => item.Provider).ToList();
        var item = LanguageData.Providers.Single(item => item.Provider == engine);

        Assert.True(order.IndexOf(engine) < order.IndexOf(TranslationProvider.DeepL));
        Assert.True(order.IndexOf(TranslationProvider.Youdao) < order.IndexOf(TranslationProvider.TranSmart));
        Assert.Equal(displayKey, item.DisplayKey);
        Assert.False(item.RequiresApiKey);
    }

    [Fact]
    public void OpenAiCompatibleProvider_IsAvailableWithoutRequiringAKey()
    {
        var provider = LanguageData.Providers.Single(item =>
            item.Provider == TranslationProvider.OpenAI);

        // The wording moved to the string dictionaries when the interface gained a second language,
        // so what is asserted here is the wiring; StringsParityTests covers what the text says.
        Assert.Equal("S.Provider.OpenAI", provider.DisplayKey);
        Assert.False(provider.RequiresApiKey);
        Assert.Equal("S.Provider.OpenAIHint", provider.HintKey);
    }

    /// <summary>
    /// The languages worth reaching first, in the same order in all three lists — a picker that
    /// reorders them between screens makes the user read a list they already know.
    /// </summary>
    /// <remarks>
    /// 繁體中文 leads the Chinese pair on purpose. It is the interface's own language and the default
    /// target; the list opens on what most of its users are looking for.
    /// </remarks>
    [Fact]
    public void PrimaryLanguages_UseConsistentOrder()
    {
        Assert.Equal(
            ["AUTO", "EN", "JA", "KO", "ZH-HANT", "ZH"],
            LanguageData.SourceLanguages.Take(6).Select(language => language.Code));
        Assert.Equal(
            ["AUTO", "EN", "JA", "KO", "ZH-HANT", "ZH"],
            LanguageData.OcrSourceLanguages.Take(6).Select(language => language.Code));
        Assert.Equal(
            ["EN-US", "JA", "KO", "ZH-HANT", "ZH-HANS"],
            LanguageData.TargetLanguages.Take(5).Select(language => language.Code));
    }

    [Fact]
    public void AutomaticSource_IsAvailableWithSupportedLanguagesInLabel()
    {
        var ocr = LanguageData.OcrSourceLanguages.Single(language =>
            language.Code == LanguageData.AutomaticSourceLanguage);

        Assert.Equal("自動（中英日）", ocr.Name);
        Assert.Contains(LanguageData.SourceLanguages, language =>
            language.Code == LanguageData.AutomaticSourceLanguage);
        Assert.Equal("AUTO", LanguageData.GetValidOcrSourceCode("auto"));
        Assert.Equal("AUTO", LanguageData.GetValidSourceCode("auto"));
    }

    /// <summary>
    /// Language names follow the interface language everywhere they are shown — the realtime
    /// control bar's pair, and the names filled into the OpenAI prompt.
    /// </summary>
    /// <remarks>
    /// The prompt used to keep its own always-Chinese accessor, on the reasoning that its reader is
    /// a model rather than a person. That stopped holding once the prompt became something the user
    /// reads and edits on the settings page: prose in a language they cannot read is no use as the
    /// starting point for their own, and the sentence names the language to translate into either
    /// way, so nothing about the model's instructions is lost by following the interface.
    /// </remarks>
    [Theory]
    [InlineData("zh-Hant", "英文", "繁體中文")]
    [InlineData("en", "English", "Traditional Chinese")]
    public void DisplayNames_FollowTheInterfaceLanguage(
        string uiLanguage, string expectedSource, string expectedTarget)
    {
        var settings = SettingsService.Instance.Current;
        var original = settings.UiLanguage;
        try
        {
            settings.UiLanguage = uiLanguage;

            Assert.Equal(expectedSource, LanguageData.GetSourceDisplayName("EN"));
            Assert.Equal(expectedTarget, LanguageData.GetTargetDisplayName("ZH-HANT"));
        }
        finally
        {
            settings.UiLanguage = original;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsupported")]
    public void TextSourceDefaultsToAutomatic(string? code)
    {
        Assert.Equal(LanguageData.AutomaticSourceLanguage, LanguageData.GetValidSourceCode(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsupported")]
    public void OcrSourceDefaultsToAutomatic(string? code)
    {
        Assert.Equal(LanguageData.AutomaticSourceLanguage, LanguageData.GetValidOcrSourceCode(code));
    }

    [Theory]
    [InlineData("AUTO", true)]
    [InlineData("auto", true)]
    [InlineData("EN", false)]
    [InlineData(null, false)]
    public void AutomaticSourceCheck_IsCaseInsensitive(string? code, bool expected)
    {
        Assert.Equal(expected, LanguageData.IsAutomaticSource(code));
    }

    [Fact]
    public void AutomaticSource_CannotBecomeATargetWhenLanguagesAreSwapped()
    {
        Assert.Null(LanguageData.MapSourceToTargetCode(LanguageData.AutomaticSourceLanguage));
    }

    [Theory]
    [InlineData("AUTO")]
    [InlineData("auto")]
    public void AutomaticSource_IsOmittedFromTranslationProviderRequests(string code)
    {
        Assert.Null(EngineLanguage.SourceToEngine(code));
    }

    [Fact]
    public void ManualSource_IsStillSentToTranslationProviderRequests()
    {
        Assert.Equal("en", EngineLanguage.SourceToEngine("EN"));
    }
}
