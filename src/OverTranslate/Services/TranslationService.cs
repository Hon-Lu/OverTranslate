using System.Net.Http;
using NLog;
using OverTranslate.Translation;
using OverTranslate.Translation.Bing;
using OverTranslate.Translation.Google;
using OverTranslate.Translation.Lookup;
using OverTranslate.Translation.Microsoft;
using OverTranslate.Translation.Tencent;
using OverTranslate.Translation.Youdao;
using OverTranslate.Layout;
using OverTranslate.Models;
using OverTranslate.Services.Providers;

namespace OverTranslate.Services;

public record TranslatedBlock(
    string OriginalText,
    string TranslatedText,
    System.Windows.Rect Bounds,
    IReadOnlyList<System.Windows.Rect>? SourceLineBounds = null,
    double? RenderGlyphHeight = null,
    System.Windows.Media.Color BackgroundColor = default,
    System.Windows.Media.Color TextColor = default,

    // Set by placement, read by the overlay. Default until something decides otherwise, so the
    // realtime path and the translation providers carry it without knowing it is there.
    OverlayLayoutIntent LayoutIntent = OverlayLayoutIntent.Default)
{
    /// <summary>
    /// Carried over from the block this was read from — see <see cref="OcrTextBlock.RunsAcross"/>.
    /// </summary>
    /// <remarks>
    /// A property rather than a constructor parameter because every provider builds this record
    /// from the same five fields and none of them has any business deciding this one. They copy it
    /// across unread, which is all a translator can honestly do with it.
    /// </remarks>
    public bool RunsAcross { get; init; }

    /// <summary>
    /// Carried over from the block this was read from — see <see cref="OcrTextBlock.FontGlyphHeight"/>.
    /// </summary>
    public double? FontGlyphHeight { get; init; }

    /// <summary>
    /// Carried over from the block this was read from — see <see cref="OcrTextBlock.Tilt"/>.
    /// </summary>
    internal Ocr.TiltedText? Tilt { get; init; }

    /// <summary>
    /// True when no engine produced a translation and <see cref="TranslatedText"/> is only the
    /// original text standing in for one.
    /// </summary>
    /// <remarks>
    /// Said outright rather than left to be inferred from the two texts being equal: a number, a
    /// name or "OK" translates to itself, and a caller that retried those would retry forever.
    /// </remarks>
    public bool Untranslated { get; init; }
}

public class TranslationService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // One client for every free engine, so a hung endpoint fails fast instead of stalling the batch.
    // Built by the engines library because how it speaks matters: see EngineHttp for why HTTP/2.
    private static readonly HttpClient Http = EngineHttp.CreateClient(TranslationTiming.Request);

    private readonly GoogleWebTranslator    _google       = new(Http);
    private readonly GoogleRpcTranslator    _google2      = new(Http);
    private readonly GoogleChromeTranslator _googleChrome = new(Http,
        loadKey: () => SettingsService.Instance.Current.GoogleChromeApiKey,
        saveKey: key =>
        {
            SettingsService.Instance.Current.GoogleChromeApiKey = key;
            SettingsService.Instance.Save();
        });
    private readonly BingTranslator         _bing         = new(Http);
    private readonly MicrosoftTranslator    _microsoft    = new(Http);
    private readonly YoudaoTranslator       _youdao       = new(Http);
    private readonly TranSmartTranslator    _tranSmart    = new(Http);
    // DeepL's own, because it is an official API spoken to with the user's key and has no reason to
    // introduce itself as a browser. Its clock is everyone's: see TranslationTiming.
    private static readonly HttpClient DeepLHttp = new() { Timeout = TranslationTiming.Request };

    private readonly DeepLProvider      _deepL     = new(DeepLHttp);
    private readonly OpenAiCompatibleProvider _openAi = new();

    // Only when asked for, one word at a time; the names are what the dictionary card credits.
    private readonly DictionaryLookupProvider _googleDictionary    = new(new GoogleDictionary(Http), "Google Web");
    private readonly DictionaryLookupProvider _bingDictionary      = new(new BingDictionary(Http), "Bing");
    private readonly DictionaryLookupProvider _microsoftDictionary = new(new MicrosoftDictionary(Http), "Microsoft");
    private readonly DictionaryLookupProvider _youdaoDictionary    = new(new YoudaoDictionary(Http), "Youdao");

    // Per-option resilient wrappers: the user's engine is the primary and is asked twice before
    // anything else is (see ResilientProvider); the backups are there for when it cannot answer.
    private readonly ResilientProvider _googleR;
    private readonly ResilientProvider _googleChromeR;
    private readonly ResilientProvider _bingR;
    private readonly ResilientProvider _microsoftR;
    private readonly ResilientProvider _youdaoR;
    private readonly ResilientProvider _tranSmartR;

    // The same engines on their own, for callers that asked for no fallback.
    private readonly EngineProvider _googleS;
    private readonly EngineProvider _googleChromeS;
    private readonly EngineProvider _bingS;
    private readonly EngineProvider _microsoftS;
    private readonly EngineProvider _youdaoS;
    private readonly EngineProvider _tranSmartS;

    // Which option each engine is part of, so the backup badge names what the dropdown names.
    private readonly Dictionary<string, TranslationProvider> _optionOf;

    public TranslationService()
    {
        _optionOf = new()
        {
            [_google.Name]       = TranslationProvider.Google,
            [_google2.Name]      = TranslationProvider.Google,
            [_googleChrome.Name] = TranslationProvider.GoogleChrome,
            [_bing.Name]         = TranslationProvider.Bing,
            [_microsoft.Name]    = TranslationProvider.Microsoft,
            [_youdao.Name]       = TranslationProvider.Youdao,
            [_tranSmart.Name]    = TranslationProvider.TranSmart,
        };

        // Each backup list leads with the engine that writes most like the primary, because a
        // backup that answers is a screen in two voices and the closer the voices the less it shows.
        //
        // 「Google 翻譯 (標準)」 is two endpoints that write almost identically — thirteen of fourteen
        // test sentences came back word for word the same — so the second is part of the option
        // rather than a backup to it: Web first, being faster and never having failed a request in
        // testing, RPC behind it, whose calls each fail now and then with an internal error. 「(Beta)」
        // is a different model with nothing that writes like it, so it falls back to 標準. Nothing
        // writes like Bing's language model or like Microsoft, so those two get the fast batch
        // engines. Bing is never a backup: it takes one text per request and is the slowest of all.
        //
        // 有道 and 騰訊 are there for users Google cannot reach, so neither falls back to
        // Google: each backs the other up — the two reachable engines nearest in kind — and Microsoft,
        // reachable from the same places, is behind both.
        _googleR       = Chain([_google, _google2, _microsoft]);
        _googleChromeR = Chain([_googleChrome, _google, _google2]);
        _bingR         = Chain([_bing, _google, _microsoft]);
        _microsoftR    = Chain([_microsoft, _google, _google2]);
        _youdaoR       = Chain([_youdao, _tranSmart, _microsoft]);
        _tranSmartR    = Chain([_tranSmart, _youdao, _microsoft]);

        _googleS       = new EngineProvider(_google);
        _googleChromeS = new EngineProvider(_googleChrome);
        _bingS         = new EngineProvider(_bing);
        _microsoftS    = new EngineProvider(_microsoft);
        _youdaoS       = new EngineProvider(_youdao);
        _tranSmartS    = new EngineProvider(_tranSmart);
    }

    private ResilientProvider Chain(IReadOnlyList<ITextTranslator> engines) =>
        new(engines, optionName: engine => _optionOf.TryGetValue(engine, out var option)
            ? LanguageData.GetProviderDisplay(option)
            : engine);

    /// <summary>
    /// The engine a caller that has not said otherwise gets: whatever the user last chose in the
    /// places that share one preference — 設定, 文字翻譯 and the capture toolbar.
    /// </summary>
    private static TranslationProvider Saved => SettingsService.Instance.Current.Provider;

    // Resilient (hedged + fallback) provider for a given choice.
    private ITranslationProvider Resilient(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google    => _googleR,
        TranslationProvider.GoogleChrome => _googleChromeR,
        TranslationProvider.Bing      => _bingR,
        TranslationProvider.Microsoft => _microsoftR,
        TranslationProvider.Youdao    => _youdaoR,
        TranslationProvider.TranSmart => _tranSmartR,
        TranslationProvider.DeepL     => _deepL,
        TranslationProvider.OpenAI    => _openAi,
        _                             => _googleR,
    };

    // Single chosen engine, no hedging/fallback — a timeout/failure surfaces directly to the caller.
    private ITranslationProvider Single(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google       => _googleS,
        TranslationProvider.GoogleChrome => _googleChromeS,
        TranslationProvider.Bing         => _bingS,
        TranslationProvider.Microsoft    => _microsoftS,
        TranslationProvider.Youdao       => _youdaoS,
        TranslationProvider.TranSmart    => _tranSmartS,
        TranslationProvider.DeepL        => _deepL,
        TranslationProvider.OpenAI       => _openAi,
        _                                => _googleS,
    };

    private DictionaryLookupProvider? DictionaryProvider(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google    => _googleDictionary,
        TranslationProvider.Bing      => _bingDictionary,
        TranslationProvider.Microsoft => _microsoftDictionary,
        TranslationProvider.Youdao    => _youdaoDictionary,
        _                             => null,
    };

    public bool RequiresApiKey => Resilient(Saved).RequiresApiKey;

    /// <summary>Whether a specific engine needs an API key, for a caller that chose its own.</summary>
    public bool ProviderRequiresApiKey(TranslationProvider provider) => Resilient(provider).RequiresApiKey;

    /// <summary>
    /// Which engine(s) actually served the most recent translation. Null for providers that have
    /// no fallback concept (e.g. DeepL), so the UI can keep the engine badge hidden.
    /// </summary>
    public EngineUsage? LastEngineUsage { get; private set; }

    /// <param name="resilient">
    /// true (default) uses the hedged/fallback provider; false sends to the single chosen engine only,
    /// so a timeout/failure throws straight to the caller (used by the manual translation window).
    /// </param>
    /// <param name="engine">
    /// Which engine to send to, or null to use the shared preference. 即時翻譯 passes its own: that
    /// page keeps its settings to itself, so the engine it is running with is not necessarily the
    /// one saved, and reading the saved one here would quietly translate with something the user
    /// did not pick.
    /// </param>
    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey, bool resilient = true,
        CancellationToken cancellationToken = default, TranslationProvider? engine = null)
    {
        var chosen   = engine ?? Saved;
        var provider = resilient ? Resilient(chosen) : Single(chosen);
        var result   = await provider.TranslateAsync(blocks, sourceLang, targetLang, apiKey, cancellationToken);
        LastEngineUsage = (provider as ResilientProvider)?.LastUsage;
        return result;
    }

    /// <summary>
    /// Looks up rich dictionary data only when the caller explicitly asks for it. Normal translation,
    /// screenshot translation and realtime translation keep their existing request count and latency.
    /// </summary>
    public Task<DictionaryLookupData?> LookupDictionaryAsync(
        string text, string sourceLang, string targetLang,
        CancellationToken cancellationToken = default, TranslationProvider? engine = null)
    {
        if (!DictionaryLookupEligibility.IsEligible(text))
            return Task.FromResult<DictionaryLookupData?>(null);

        var lookupText = text.Trim();
        var attempts = DictionaryLookupPlan.Build(engine ?? Saved, sourceLang, targetLang)
            .Select<DictionaryLookupStep, Func<CancellationToken, Task<DictionaryLookupData?>>>(step =>
                async token =>
                {
                    var provider = DictionaryProvider(step.Provider);
                    if (provider is null) return null;

                    var requestText = step.ConvertSourceToSimplified
                        ? DictionarySimplifiedChineseConverter.Convert(lookupText)
                        : lookupText;

                    // Every step, not only the last: DictionaryLookupFallback keeps just the last
                    // failure, so without these a card that came from the second engine never says
                    // why the first one did not answer. Debug, because some steps fail on every
                    // lookup by design — Microsoft and Bing refuse a pair without English with a 400.
                    DictionaryLookupData? result;
                    try
                    {
                        result = await provider.LookupDictionaryAsync(
                            requestText, step.SourceLanguage, step.TargetLanguage, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        Log.Debug("字典 {Provider} {Source}→{Target} 失敗：{Error}",
                            step.Provider, step.SourceLanguage, step.TargetLanguage, ex.Message);
                        throw;
                    }

                    if (result is null)
                    {
                        Log.Debug("字典 {Provider} {Source}→{Target} 沒有內容",
                            step.Provider, step.SourceLanguage, step.TargetLanguage);
                        return null;
                    }

                    return PrepareDictionaryResult(result, lookupText, step.ConvertToTraditional);
                })
            .ToList();

        return DictionaryLookupFallback.TryAsync(attempts, cancellationToken);
    }

    internal static DictionaryLookupData PrepareDictionaryResult(
        DictionaryLookupData result, string originalText, bool convertToTraditional)
    {
        var prepared = convertToTraditional
            ? DictionaryTraditionalChineseConverter.Convert(result)
            : result;
        return prepared with { Headword = originalText };
    }
}
