using System.Net.Http;
using OverTranslate.Translation.DeepL;

namespace OverTranslate.Services.Providers;

/// <summary>
/// 「DeepL」 as the application asks for it: the engine in <see cref="OverTranslate.Translation"/>,
/// with the key the caller was given.
/// </summary>
/// <remarks>
/// The key arrives with each call rather than once, so the engine is built per call — it holds
/// nothing but the key and the client. Everything else (codes, packing, what a failure looks like)
/// is <see cref="EngineProvider"/>'s, the same as for the free engines.
/// </remarks>
public sealed class DeepLProvider(HttpClient http) : ITranslationProvider
{
    public bool RequiresApiKey => true;

    public Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default) =>
        new EngineProvider(new DeepLTranslator(http, apiKey))
            .TranslateAsync(blocks, sourceLang, targetLang, apiKey, cancellationToken);
}
