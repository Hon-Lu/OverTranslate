using OverTranslate.Models;

namespace OverTranslate.Services;

internal sealed record DictionaryLookupStep(
    TranslationProvider Provider,
    string SourceLanguage,
    string TargetLanguage,
    bool ConvertSourceToSimplified,
    bool ConvertToTraditional);

internal static class DictionaryLookupPlan
{
    internal static IReadOnlyList<DictionaryLookupStep> Build(
        TranslationProvider selectedProvider, string sourceLanguage, string targetLanguage)
    {
        if (targetLanguage.Equals("ZH-HANT", StringComparison.OrdinalIgnoreCase))
            return BuildTraditionalChinese(selectedProvider, sourceLanguage, targetLanguage);

        return selectedProvider switch
        {
            TranslationProvider.Google =>
            [
                Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
                Native(TranslationProvider.Microsoft, sourceLanguage, targetLanguage),
                Native(TranslationProvider.Bing, sourceLanguage, targetLanguage),
            ],
            TranslationProvider.Google2 or TranslationProvider.GoogleChrome =>
            [
                Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
                Native(TranslationProvider.Microsoft, sourceLanguage, targetLanguage),
            ],
            TranslationProvider.Microsoft =>
            [
                Native(TranslationProvider.Microsoft, sourceLanguage, targetLanguage),
                Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
                Native(TranslationProvider.Bing, sourceLanguage, targetLanguage),
            ],
            TranslationProvider.Bing =>
            [
                Native(TranslationProvider.Bing, sourceLanguage, targetLanguage),
                Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
                Native(TranslationProvider.Microsoft, sourceLanguage, targetLanguage),
            ],
            // For users Google cannot reach, so Google is only the last resort. Youdao's dictionary
            // glosses in Chinese only and says itself when it has nothing, which hands the lookup on.
            TranslationProvider.Youdao or TranslationProvider.TranSmart =>
            [
                Native(TranslationProvider.Youdao, sourceLanguage, targetLanguage),
                Native(TranslationProvider.Microsoft, sourceLanguage, targetLanguage),
                Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
            ],
            TranslationProvider.DeepL =>
            [
                Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
                Native(TranslationProvider.Microsoft, sourceLanguage, targetLanguage),
            ],
            _ => [],
        };
    }

    private static IReadOnlyList<DictionaryLookupStep> BuildTraditionalChinese(
        TranslationProvider selectedProvider, string sourceLanguage, string targetLanguage) => selectedProvider switch
    {
        TranslationProvider.Google =>
        [
            Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
            Converted(TranslationProvider.Microsoft, sourceLanguage),
            Converted(TranslationProvider.Bing, sourceLanguage),
        ],
        TranslationProvider.Google2 or TranslationProvider.GoogleChrome =>
        [
            Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
            Converted(TranslationProvider.Microsoft, sourceLanguage),
        ],
        TranslationProvider.Microsoft =>
        [
            Converted(TranslationProvider.Microsoft, sourceLanguage),
            Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
            Converted(TranslationProvider.Bing, sourceLanguage),
        ],
        TranslationProvider.Bing =>
        [
            Converted(TranslationProvider.Bing, sourceLanguage),
            Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
            Converted(TranslationProvider.Microsoft, sourceLanguage),
        ],
        // Youdao's glosses are simplified whatever is asked, and shown as they are: see YoudaoDictionary.
        TranslationProvider.Youdao or TranslationProvider.TranSmart =>
        [
            Native(TranslationProvider.Youdao, sourceLanguage, targetLanguage),
            Converted(TranslationProvider.Microsoft, sourceLanguage),
            Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
        ],
        TranslationProvider.DeepL =>
        [
            Native(TranslationProvider.Google, sourceLanguage, targetLanguage),
            Converted(TranslationProvider.Microsoft, sourceLanguage),
        ],
        _ => [],
    };

    private static DictionaryLookupStep Native(
        TranslationProvider provider, string sourceLanguage, string targetLanguage) =>
        Create(provider, sourceLanguage, targetLanguage, convertTargetToTraditional: false);

    private static DictionaryLookupStep Converted(
        TranslationProvider provider, string sourceLanguage) =>
        Create(provider, sourceLanguage, "ZH-HANS", convertTargetToTraditional: true);

    private static DictionaryLookupStep Create(
        TranslationProvider provider,
        string sourceLanguage,
        string targetLanguage,
        bool convertTargetToTraditional)
    {
        var convertSourceToSimplified =
            sourceLanguage.Equals("ZH-HANT", StringComparison.OrdinalIgnoreCase) &&
            provider is TranslationProvider.Microsoft or TranslationProvider.Bing;

        return new DictionaryLookupStep(
            provider,
            convertSourceToSimplified ? "ZH-HANS" : sourceLanguage,
            targetLanguage,
            convertSourceToSimplified,
            convertTargetToTraditional || convertSourceToSimplified);
    }
}
