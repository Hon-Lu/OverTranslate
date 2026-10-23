using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The documentation the interface links out to is actually in the repository.
/// </summary>
/// <remarks>
/// A link to a renamed or deleted document does not fail here — it opens a browser on GitHub's
/// 404 page, in front of a user who was already stuck enough to be reading a setup guide. The
/// repository is the one place that can answer whether the path is real, so the test reads it
/// rather than the running app.
/// </remarks>
public class DocumentationLinkTests
{
    [Theory]
    [MemberData(nameof(LocalizationServiceTests.InterfaceLanguages), MemberType = typeof(LocalizationServiceTests))]
    public void EveryLanguagesOllamaGuideExists(string language)
    {
        var path = Path.Combine(RepositoryRoot(), DocumentationLinks.OllamaGuidePath(language));

        Assert.True(File.Exists(path), $"{language}: {path} is missing");
    }

    /// <summary>
    /// The guide is kept to a few languages; any other interface is sent to the English one rather
    /// than to the Traditional Chinese the strings are authored in.
    /// </summary>
    [Fact]
    public void ALanguageWithoutAGuideGetsTheEnglishOne() =>
        Assert.Equal(
            DocumentationLinks.OllamaGuidePath(LocalizationService.English),
            DocumentationLinks.OllamaGuidePath("xx"));

    /// <summary>
    /// The guide the settings page links to is the one for the language on screen.
    /// </summary>
    [Fact]
    public void TheLinkFollowsTheInterfaceLanguage()
    {
        var settings = SettingsService.Instance.Current;
        var original = settings.UiLanguage;
        try
        {
            settings.UiLanguage = LocalizationService.Japanese;
            Assert.EndsWith("docs/guides/OLLAMA_GUIDE.ja.md", DocumentationLinks.OllamaGuide);

            settings.UiLanguage = LocalizationService.TraditionalChinese;
            Assert.EndsWith("docs/guides/OLLAMA_GUIDE.md", DocumentationLinks.OllamaGuide);
        }
        finally
        {
            settings.UiLanguage = original;
        }
    }

    /// <summary>The repository root, which is the directory holding src/OverTranslate.</summary>
    private static string RepositoryRoot() =>
        Directory.GetParent(Directory.GetParent(StringsParityTests.ProjectDirectory())!.FullName)!.FullName;
}
