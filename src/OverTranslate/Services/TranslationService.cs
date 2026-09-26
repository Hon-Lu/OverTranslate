using System.Net.Http;
using OverTranslate.Engines;
using OverTranslate.Engines.Bing;
using OverTranslate.Engines.Google;
using OverTranslate.Engines.Microsoft;
using OverTranslate.Layout;
using OverTranslate.Models;
using OverTranslate.Services.Providers;
using GT = GTranslate.Translators;

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
    // One client for every free engine, so a hung endpoint fails fast instead of stalling the batch.
    // Built by the engines library because how it speaks matters: see EngineHttp for why HTTP/2.
    private static readonly HttpClient Http = EngineHttp.CreateClient(TimeSpan.FromSeconds(10));

    private readonly GoogleWebTranslator    _google       = new(Http);
    private readonly GoogleRpcTranslator    _google2      = new(Http);
    private readonly GoogleChromeTranslator _googleChrome = new(Http);
    private readonly BingTranslator         _bing         = new(Http);
    private readonly MicrosoftTranslator    _microsoft    = new(Http);
    private readonly DeepLProvider      _deepL     = new();
    private readonly OpenAiCompatibleProvider _openAi = new();

    // Dictionary lookups are still GTranslate's; see GTranslateDictionaryProvider.
    private readonly GTranslateDictionaryProvider _googleDictionary    = new(new GT.GoogleTranslator(Http));
    private readonly GTranslateDictionaryProvider _bingDictionary      = new(new GT.BingTranslator(Http));
    private readonly GTranslateDictionaryProvider _microsoftDictionary = new(new GT.MicrosoftTranslator(Http));

    // Per-engine resilient wrappers: the user's choice is the primary and is asked twice before
    // anything else is (see ResilientProvider); the backups are there for when it cannot answer.
    private readonly ResilientProvider _googleR;
    private readonly ResilientProvider _google2R;
    private readonly ResilientProvider _googleChromeR;
    private readonly ResilientProvider _bingR;
    private readonly ResilientProvider _microsoftR;

    // The same engines on their own, for callers that asked for no fallback.
    private readonly EngineProvider _googleS;
    private readonly EngineProvider _google2S;
    private readonly EngineProvider _googleChromeS;
    private readonly EngineProvider _bingS;
    private readonly EngineProvider _microsoftS;

    public TranslationService()
    {
        // Each backup list leads with the engine that writes most like the primary, because a
        // backup that answers is a screen in two voices and the closer the voices the less it shows.
        // 「Google (Web)」 and 「Google (RPC)」 write almost identically — thirteen of fourteen test
        // sentences came back word for word the same — so they back each other up first. Nothing writes like Bing's language model or
        // like Microsoft, so those two get the fast batch engines. Bing is never a backup: it
        // takes one text per request and is the slowest of the five.
        _googleR       = new ResilientProvider([_google, _google2, _microsoft]);
        _google2R      = new ResilientProvider([_google2, _google, _microsoft]);
        _googleChromeR = new ResilientProvider([_googleChrome, _google2, _microsoft]);
        _bingR         = new ResilientProvider([_bing, _google2, _microsoft]);
        _microsoftR    = new ResilientProvider([_microsoft, _google2, _google]);

        _googleS       = new EngineProvider(_google);
        _google2S      = new EngineProvider(_google2);
        _googleChromeS = new EngineProvider(_googleChrome);
        _bingS         = new EngineProvider(_bing);
        _microsoftS    = new EngineProvider(_microsoft);
    }

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
        TranslationProvider.DeepL     => _deepL,
        TranslationProvider.OpenAI    => _openAi,
        _                             => _google2R,
    };

    // Single chosen engine, no hedging/fallback — a timeout/failure surfaces directly to the caller.
    private ITranslationProvider Single(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google       => _googleS,
        TranslationProvider.GoogleChrome => _googleChromeS,
        TranslationProvider.Bing         => _bingS,
        TranslationProvider.Microsoft    => _microsoftS,
        TranslationProvider.DeepL        => _deepL,
        TranslationProvider.OpenAI       => _openAi,
        _                                => _google2S,
    };

    private GTranslateDictionaryProvider? DictionaryProvider(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google    => _googleDictionary,
        TranslationProvider.Bing      => _bingDictionary,
        TranslationProvider.Microsoft => _microsoftDictionary,
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
                    var result = await provider.LookupDictionaryAsync(
                        requestText, step.SourceLanguage, step.TargetLanguage, token);
                    if (result is null) return null;

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
