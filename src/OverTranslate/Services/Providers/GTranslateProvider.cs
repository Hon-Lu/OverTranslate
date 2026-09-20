using GTranslate.Results;
using GTranslate.Translators;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services.Providers;

public class GTranslateProvider : ITranslationProvider
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly ITranslator _translator;
    private readonly Dictionary<string, string> _targetOverrides;
    private readonly int _safeInputLimit;

    /// <param name="safeInputLimit">
    /// The most this engine is handed in one request, or null for the shared default. Each engine
    /// splits for itself at its own transport boundary, so one that turns out to want a smaller
    /// budget can be given one without every other engine inheriting it — which is what would
    /// happen if <see cref="ResilientProvider"/> split once for whichever of them answers.
    /// </param>
    public GTranslateProvider(
        ITranslator translator,
        Dictionary<string, string>? targetOverrides = null,
        int? safeInputLimit = null)
    {
        _translator     = translator;
        _targetOverrides = targetOverrides ?? [];
        _safeInputLimit  = safeInputLimit ?? TranslationRequestChunks.SafeMaxCharacters;
    }

    public bool RequiresApiKey => false;

    // Friendly engine name (e.g. "GoogleTranslator2", "BingTranslator") for diagnostics/logging.
    public string Name => _translator.Name;

    public bool SupportsDictionary => _translator is IDictionaryTranslator;

    // Maps DeepL-style codes to BCP-47 codes used by GTranslate
    private static readonly Dictionary<string, string> ToGTranslate = new(StringComparer.OrdinalIgnoreCase)
    {
        { "EN",      "en"    }, { "EN-US",   "en"    }, { "EN-GB",   "en"    },
        { "ZH",      "zh-CN" }, { "ZH-HANS", "zh-CN" }, { "ZH-HANT", "zh-TW" },
        { "PT",      "pt"    }, { "PT-BR",   "pt"    }, { "PT-PT",   "pt"    },
        { "DE",      "de"    }, { "FR",      "fr"    }, { "ES",      "es"    },
        { "IT",      "it"    }, { "NL",      "nl"    }, { "PL",      "pl"    },
        { "RU",      "ru"    }, { "JA",      "ja"    }, { "KO",      "ko"    },
        { "CS",      "cs"    }, { "DA",      "da"    }, { "EL",      "el"    },
        { "ET",      "et"    }, { "FI",      "fi"    }, { "HU",      "hu"    },
        { "ID",      "id"    }, { "LT",      "lt"    }, { "LV",      "lv"    },
        { "NB",      "no"    }, { "RO",      "ro"    }, { "SK",      "sk"    },
        { "SL",      "sl"    }, { "SV",      "sv"    }, { "TR",      "tr"    },
        { "UK",      "uk"    }, { "BG",      "bg"    },
    };

    private static string MapToGTranslate(string deepLCode) =>
        ToGTranslate.TryGetValue(deepLCode, out var code) ? code : deepLCode.ToLowerInvariant().Split('-')[0];

    internal static string? MapSourceToGTranslate(string sourceLang) =>
        LanguageData.IsAutomaticSource(sourceLang) ? null : MapToGTranslate(sourceLang);

    private static string MapDetectedToDeepL(string iso6391) => iso6391.ToUpperInvariant() switch
    {
        "NO" => "NB",
        _    => iso6391.ToUpperInvariant()
    };

    private static string DictionaryServiceDisplay(string service) => service switch
    {
        "GoogleTranslator"    => "Google Web",
        "BingTranslator"      => "Bing",
        "MicrosoftTranslator" => "Microsoft",
        _                     => service,
    };

    public async Task<(List<TranslatedBlock> Blocks, string DetectedLang)> TranslateAsync(
        List<OcrTextBlock> blocks, string sourceLang, string targetLang, string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (blocks.Count == 0) return ([], "");
        cancellationToken.ThrowIfCancellationRequested();

        var tasks   = blocks.Select(b => TranslateOneAsync(b.Text, sourceLang, targetLang, cancellationToken));
        var results = await Task.WhenAll(tasks);

        var langVotes  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var translated = new List<TranslatedBlock>();
        for (int i = 0; i < blocks.Count; i++)
        {
            var (translation, detLang) = results[i];
            if (!string.IsNullOrEmpty(detLang))
                langVotes[detLang] = langVotes.GetValueOrDefault(detLang) + 1;
            translated.Add(new TranslatedBlock(blocks[i].Text, translation, blocks[i].Bounds, blocks[i].Lines, blocks[i].RenderGlyphHeight)
                { RunsAcross = blocks[i].RunsAcross });
        }

        string detectedLang = langVotes.Count > 0 ? langVotes.MaxBy(kv => kv.Value).Key : "";
        return (translated, detectedLang);
    }

    // Translates a single text fragment. Detected language is returned as a DeepL-style code.
    // Throws if the underlying free endpoint fails — the caller (ResilientProvider) decides on fallback.
    //
    // GTranslate's ITranslator.TranslateAsync takes no CancellationToken, so a request already in
    // flight cannot be aborted. The token is honoured at the only point where it still helps: before
    // the call is made. That is what keeps an abandoned batch from issuing the requests it has not
    // started yet, including every hedged backup ResilientProvider would have launched.
    public async Task<(string Translation, string DetectedLang)> TranslateOneAsync(
        string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_targetOverrides.TryGetValue(targetLang, out var overrideLang))
            targetLang = overrideLang;

        var toCode   = MapToGTranslate(targetLang);
        var fromCode = MapSourceToGTranslate(sourceLang);

        // A text past this engine's budget is sent as consecutive sentences rather than as one
        // request. Two of the three engines refuse an over-long text outright and the third answers
        // with a repetition loop, which nothing above here could tell from a translation — see
        // TranslationRequestChunks.
        var chunks = TranslationRequestChunks.Split(text, _safeInputLimit);
        if (chunks.Count == 1)
        {
            var single  = await _translator.TranslateAsync(text, toCode, fromCode);
            var oneLang = single.SourceLanguage?.ISO6391 ?? "";
            return (single.Translation, string.IsNullOrEmpty(oneLang) ? "" : MapDetectedToDeepL(oneLang));
        }

        // Where a long text was cut and why, because a translation that reads oddly at a seam is
        // otherwise indistinguishable from one the engine simply got wrong.
        Log.Debug(
            "{Engine}：{Length} 字超過 {Limit} 字上限，分成 {Count} 段（{Boundaries}）",
            Name, text.Length, _safeInputLimit, chunks.Count,
            string.Join(", ", chunks.Select(chunk => $"{chunk.Text.Length}/{chunk.BoundaryAfter}")));

        var translations = new List<string>(chunks.Count);
        var detected = "";

        // One at a time, never in parallel: these are keyless endpoints, and a burst of requests
        // from one machine is what throttling is for.
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var part = await _translator.TranslateAsync(chunk.Text, toCode, fromCode);
            translations.Add(part.Translation);
            // The first chunk that names a language speaks for the block; the rest are the same
            // text and a later disagreement is a shorter piece being read with less to go on.
            if (detected.Length == 0) detected = part.SourceLanguage?.ISO6391 ?? "";
        }

        var mapped = string.IsNullOrEmpty(detected) ? "" : MapDetectedToDeepL(detected);
        return (TranslationRequestChunks.Join(chunks, translations), mapped);
    }

    public async Task<DictionaryLookupData?> LookupDictionaryAsync(
        string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        if (_translator is not IDictionaryTranslator dictionaryTranslator)
            return null;

        if (_targetOverrides.TryGetValue(targetLang, out var overrideLang))
            targetLang = overrideLang;

        var fromCode = MapSourceToGTranslate(sourceLang);
        if (string.IsNullOrWhiteSpace(fromCode)) return null;

        var result = await dictionaryTranslator.LookupDictionaryAsync(
            text, MapToGTranslate(targetLang), fromCode, cancellationToken);

        var mapped = new DictionaryLookupData(
            result.Source,
            DictionaryServiceDisplay(result.Service),
            result.Headword,
            result.Pronunciation,
            result.Groups.Select(group => new DictionaryLookupGroupData(
                group.PartOfSpeech,
                group.Entries.Select(entry => new DictionaryEntryData(
                    entry.Text,
                    entry.Transliteration,
                    entry.Confidence,
                    entry.Frequency,
                    entry.BackTranslations,
                    entry.Examples.Select(example => new DictionaryExampleData(
                        example.Source, example.Translation)).ToList())).ToList(),
                group.Definitions,
                group.Synonyms)).ToList(),
            result.Examples.Select(example => new DictionaryExampleData(
                example.Source, example.Translation)).ToList());

        return mapped.HasContent ? mapped : null;
    }
}
