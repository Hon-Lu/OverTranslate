using System.Globalization;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The two decisions LocalizationService makes on its own: which language a first run starts in,
/// and which stored values it is willing to hand out afterwards.
/// </summary>
public class LocalizationServiceTests
{
    /// <remarks>
    /// Chinese is split by script, and the script has to be read off the region because Windows
    /// reports zh-TW and zh-CN far more often than it reports zh-Hant. Everything unlisted is
    /// Simplified, which is what the regions nobody thought of overwhelmingly are.
    /// </remarks>
    [Theory]
    [InlineData("zh-TW", LocalizationService.TraditionalChinese)]
    [InlineData("zh-HK", LocalizationService.TraditionalChinese)]
    [InlineData("zh-MO", LocalizationService.TraditionalChinese)]
    [InlineData("zh-Hant", LocalizationService.TraditionalChinese)]
    [InlineData("zh-Hant-TW", LocalizationService.TraditionalChinese)]
    [InlineData("zh-CN", LocalizationService.SimplifiedChinese)]
    [InlineData("zh-SG", LocalizationService.SimplifiedChinese)]
    [InlineData("zh-Hans", LocalizationService.SimplifiedChinese)]
    [InlineData("zh", LocalizationService.SimplifiedChinese)]
    [InlineData("ja-JP", LocalizationService.Japanese)]
    [InlineData("ko-KR", LocalizationService.Korean)]
    [InlineData("en-US", LocalizationService.English)]
    [InlineData("de-DE", LocalizationService.English)]
    [InlineData("", LocalizationService.English)]
    public void ResolveSystemDefault_FollowsTheDisplayLanguage(string culture, string expected)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, LocalizationService.ResolveSystemDefault());
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Theory]
    [InlineData(LocalizationService.TraditionalChinese)]
    [InlineData(LocalizationService.SimplifiedChinese)]
    [InlineData(LocalizationService.English)]
    [InlineData(LocalizationService.Japanese)]
    [InlineData(LocalizationService.Korean)]
    public void IsSupported_AcceptsEveryLanguageOnOffer(string language) =>
        Assert.True(LocalizationService.IsSupported(language));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kl")]
    [InlineData("zh-Hans-CN")]
    public void IsSupported_RejectsAnythingElse(string? language) =>
        Assert.False(LocalizationService.IsSupported(language));

    /// <summary>
    /// A language offered in settings that has no dictionary behind it lands the user on Traditional
    /// Chinese with no explanation — Apply falls back rather than throwing, which is right for a
    /// hand-edited settings file and useless as a way of noticing this.
    /// </summary>
    [Fact]
    public void EveryLanguageOnOfferHasADictionaryAndIsSupported()
    {
        foreach (var option in LocalizationService.Options)
        {
            Assert.True(
                LocalizationService.IsSupported(option.Code),
                $"{option.Code} is offered in settings but has no dictionary");

            var file = Path.Combine(
                StringsParityTests.ProjectDirectory(), "Resources", $"Strings.{option.Code}.xaml");

            Assert.True(File.Exists(file), $"{option.Code} is offered in settings but {file} is missing");
        }
    }

    /// <summary>
    /// The name in the picker is the one string never translated — someone who has landed in a
    /// language they cannot read finds their way out by recognising their own language's name.
    /// </summary>
    [Fact]
    public void EveryLanguageIsNamedInItself()
    {
        Assert.Equal(
            new[] { "繁體中文", "简体中文", "English", "日本語", "한국어" },
            LocalizationService.Options.Select(o => o.Display));
    }
}
