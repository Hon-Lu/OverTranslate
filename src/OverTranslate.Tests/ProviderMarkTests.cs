using System.Xml.Linq;
using OverTranslate.Controls;
using OverTranslate.Models;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The one table of which mark belongs to which service — see <see cref="ProviderMark"/>.
/// </summary>
public class ProviderMarkTests
{
    [Fact]
    public void EveryServiceInTheMenus_HasAMarkThatExists()
    {
        var shared = XDocument.Load(Path.Combine(StringsParityTests.ProjectDirectory(), "Themes", "SharedStyles.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = shared.Descendants().Select(e => (string?)e.Attribute(x + "Key")).Where(k => k is not null).ToHashSet();

        foreach (var item in LanguageData.Providers)
            Assert.Contains(ProviderMark.For(item.Provider).Resource, keys);
    }

    // Only the two Googles share a mark: every other service is told apart by its own.
    [Fact]
    public void OnlyTheTwoGoogles_ShareAMark()
    {
        var shared = LanguageData.Providers
            .GroupBy(item => ProviderMark.For(item.Provider).Resource)
            .Where(group => group.Count() > 1)
            .Select(group => group.Select(item => item.Provider).OrderBy(p => p).ToArray())
            .ToList();

        Assert.Equal([[TranslationProvider.Google, TranslationProvider.GoogleChrome]], shared);
    }

    // The retired option draws as the one it became.
    [Fact]
    public void TheRetiredGoogle_DrawsAsStandard() =>
        Assert.Equal(ProviderMark.For(TranslationProvider.Google), ProviderMark.For(TranslationProvider.Google2));

    // The cards' sizes, which were settled by eye: moving them into the table must not move a mark.
    [Theory]
    [InlineData(TranslationProvider.Google, 26.0, 26.0)]
    [InlineData(TranslationProvider.Bing, null, 27.0)]
    [InlineData(TranslationProvider.Microsoft, 23.0, 23.0)]
    [InlineData(TranslationProvider.Youdao, 28.0, 28.0)]
    [InlineData(TranslationProvider.TranSmart, 27.0, 27.0)]
    [InlineData(TranslationProvider.Yandex, 27.0, 27.0)]
    [InlineData(TranslationProvider.DeepL, 22.0, null)]
    [InlineData(TranslationProvider.LocalAI, 28.0, 28.0)]
    [InlineData(TranslationProvider.OpenAI, 25.0, null)]
    public void AtTheCardsSize_EachMarkKeepsItsSettledSize(TranslationProvider provider, double? width, double? height)
    {
        var spec = ProviderMark.For(provider);

        Assert.Equal(width, spec.Width);
        Assert.Equal(height, spec.Height);
    }
}
