namespace OverTranslate.Services.Providers;

public interface ITranslationProvider
{
    bool RequiresApiKey { get; }

    Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default, TranslationContext? context = null);
}
