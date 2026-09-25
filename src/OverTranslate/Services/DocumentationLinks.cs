namespace OverTranslate.Services;

/// <summary>
/// Where the repository's documentation lives, for the links the interface offers.
/// </summary>
/// <remarks>
/// Built here rather than written into XAML so the link can follow the interface language: a
/// Japanese reader sent to the Traditional Chinese guide has been sent nowhere useful.
/// DocumentationLinkTests holds these paths against the files actually in the repository, because
/// a renamed document breaks the link silently — the browser opens and shows GitHub's 404.
/// </remarks>
public static class DocumentationLinks
{
    private const string Blob = "https://github.com/Hon-Lu/OverTranslate/blob/main/";

    /// <summary>
    /// Paths under the repository root, one per interface language.
    /// </summary>
    private static readonly Dictionary<string, string> OllamaGuidePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        [LocalizationService.TraditionalChinese] = "docs/guides/OLLAMA_GUIDE.md",
        [LocalizationService.SimplifiedChinese]  = "docs/guides/OLLAMA_GUIDE.zh-Hans.md",
        [LocalizationService.English]            = "docs/guides/OLLAMA_GUIDE.en.md",
        [LocalizationService.Japanese]           = "docs/guides/OLLAMA_GUIDE.ja.md",
        [LocalizationService.Korean]             = "docs/guides/OLLAMA_GUIDE.ko.md",
    };

    /// <summary>The Ollama guide in the language the interface is currently in.</summary>
    public static string OllamaGuide => Blob + OllamaGuidePath(LocalizationService.Current);

    /// <inheritdoc cref="OllamaGuide"/>
    internal static string OllamaGuidePath(string language) =>
        OllamaGuidePaths.TryGetValue(language, out var path)
            ? path
            : OllamaGuidePaths[LocalizationService.TraditionalChinese];
}
