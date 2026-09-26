using OverTranslate.Services;

namespace OverTranslate.Models;

/// <param name="NameKey">
/// Resource key for the language's name in the interface's own language, which is what a message
/// says it back with. A key rather than the text itself: these lists are built once at type
/// initialisation and the interface language is not, so a record holding the name would keep
/// whichever language the app started in. <see cref="Name"/> resolves it per read, the way
/// <see cref="ProviderItem.Display"/> does.
/// </param>
/// <param name="English">
/// Its English name, carried separately rather than baked into one label: the pickers show both so
/// a user scanning a long list can find theirs either way, while somewhere with one line to spare
/// wants only the first. Composing here means neither has to take a string apart to get at one.
/// </param>
/// <param name="StandsAlone">
/// True when <paramref name="Name"/> and <paramref name="English"/> are each a complete label on
/// their own, so the Chinese interface shows the name alone instead of the usual pair. For the OCR
/// picker's automatic entry, whose name already carries its own parenthetical.
/// </param>
public record LangItem(string Code, string NameKey, string English, bool StandsAlone = false)
    : ISearchableItem
{
    /// <inheritdoc cref="NameKey"/>
    public string Name => LocalizationService.Get(NameKey);

    /// <summary>
    /// Everything this language can be searched by in the pickers: its code, <see cref="Name"/> and
    /// <see cref="English"/>.
    /// </summary>
    /// <remarks>
    /// All of them regardless of which one is on screen — see <see cref="ISearchableItem"/>. The
    /// code is in there because it is the shortest way to reach a language for anyone who knows its
    /// code ("ja", "zh-hant"), and it is never shown in the list at all. Whichever language
    /// <see cref="Name"/> comes to be written in as more interface languages arrive, it is a name
    /// this search can find the language by, and no entry here has to change for that.
    /// </remarks>
    public string SearchText => $"{Code} {Name} {English}";

    /// <summary>
    /// The label for the pickers, in whichever language the interface is currently in.
    /// </summary>
    /// <remarks>
    /// The Chinese interface shows both names, which is what the pair was carried separately for.
    /// The English one shows only <see cref="English"/>: "英文 English" beside an English menu is
    /// noise to a reader who cannot read the first half, and the doubled-up label is what pushed
    /// these combo boxes wide in the first place.
    /// </remarks>
    public string Display
    {
        get
        {
            if (LocalizationService.Current == LocalizationService.English)
                return string.IsNullOrEmpty(English) ? Name : English;

            return StandsAlone || string.IsNullOrEmpty(English) ? Name : $"{Name} {English}";
        }
    }

    /// <summary>
    /// The name alone, in the interface's language, for somewhere with one line to spare.
    /// </summary>
    /// <remarks>
    /// The realtime control bar names the pair it is translating between and has room for neither
    /// the doubled-up label <see cref="Display"/> produces in Chinese nor a language the reader
    /// cannot read.
    /// </remarks>
    public string ShortName =>
        LocalizationService.Current == LocalizationService.English && !string.IsNullOrEmpty(English)
            ? English
            : Name;
}
/// <param name="DisplayKey">Resource key for the name shown in the pickers.</param>
/// <param name="HintKey">Resource key for the line under the picker, or null for no hint.</param>
/// <remarks>
/// Keys rather than text: the list is static, the interface language is not, and a record built
/// once at type-initialisation would otherwise keep whichever language the app started in.
/// Resolving in the properties means callers still just read <see cref="Display"/>.
/// </remarks>
public record ProviderItem(
    TranslationProvider Provider, string DisplayKey, bool RequiresApiKey, string? HintKey = null)
{
    public string Display => LocalizationService.Get(DisplayKey);
    public string? Hint => HintKey is null ? null : LocalizationService.Get(HintKey);

    /// <summary>The same name, because a provider only has the one.</summary>
    /// <remarks>
    /// Carried so the pickers can ask every item they hold for a short name without having to know
    /// which kind of item it is — <see cref="LangItem.ShortName"/> is the one that differs. Without
    /// it the capture toolbar's provider box binds to a property that is not there and shows
    /// nothing at all.
    /// </remarks>
    public string ShortName => Display;
}

public static class LanguageData
{
    public const string AutomaticSourceLanguage = "AUTO";
    public const string DefaultSourceLanguage = AutomaticSourceLanguage;
    public const string DefaultOcrSourceLanguage = AutomaticSourceLanguage;
    public const string DefaultTargetLanguage = "ZH-HANT";

    /// <summary>
    /// What 即時翻譯 reads from until the user says otherwise — see
    /// <see cref="GetValidRealtimeSourceCode"/> for why it cannot be 自動 like the screenshot flow's.
    /// </summary>
    public const string DefaultRealtimeSourceLanguage = "EN";

    public static readonly List<LangItem> SourceLanguages =
    [
        new(AutomaticSourceLanguage, "S.Lang.Automatic",          "Automatic"),
        new("EN",      "S.Lang.English",            "English"),
        new("JA",      "S.Lang.Japanese",           "Japanese"),
        new("KO",      "S.Lang.Korean",             "Korean"),
        new("ZH-HANT", "S.Lang.TraditionalChinese", "Traditional Chinese"),
        new("ZH",      "S.Lang.SimplifiedChinese",  "Simplified Chinese"),
        new("BG",      "S.Lang.Bulgarian",          "Bulgarian"),
        new("CS",      "S.Lang.Czech",              "Czech"),
        new("DA",      "S.Lang.Danish",             "Danish"),
        new("DE",      "S.Lang.German",             "German"),
        new("EL",      "S.Lang.Greek",              "Greek"),
        new("ES",      "S.Lang.Spanish",            "Spanish"),
        new("ET",      "S.Lang.Estonian",           "Estonian"),
        new("FI",      "S.Lang.Finnish",            "Finnish"),
        new("FR",      "S.Lang.French",             "French"),
        new("HU",      "S.Lang.Hungarian",          "Hungarian"),
        new("ID",      "S.Lang.Indonesian",         "Indonesian"),
        new("IT",      "S.Lang.Italian",            "Italian"),
        new("LT",      "S.Lang.Lithuanian",         "Lithuanian"),
        new("LV",      "S.Lang.Latvian",            "Latvian"),
        new("NB",      "S.Lang.Norwegian",          "Norwegian"),
        new("NL",      "S.Lang.Dutch",              "Dutch"),
        new("PL",      "S.Lang.Polish",             "Polish"),
        new("PT",      "S.Lang.Portuguese",         "Portuguese"),
        new("RO",      "S.Lang.Romanian",           "Romanian"),
        new("RU",      "S.Lang.Russian",            "Russian"),
        new("SK",      "S.Lang.Slovak",             "Slovak"),
        new("SL",      "S.Lang.Slovenian",          "Slovenian"),
        new("SV",      "S.Lang.Swedish",            "Swedish"),
        new("TR",      "S.Lang.Turkish",            "Turkish"),
        new("UK",      "S.Lang.Ukrainian",          "Ukrainian"),
    ];

    public static readonly List<LangItem> OcrSourceLanguages =
    [
        new(AutomaticSourceLanguage, "S.Lang.AutomaticOcr",       "Automatic (ZH/EN/JA)", StandsAlone: true),
        new("EN",      "S.Lang.English",            "English"),
        new("JA",      "S.Lang.Japanese",           "Japanese"),
        new("KO",      "S.Lang.Korean",             "Korean"),
        new("ZH-HANT", "S.Lang.TraditionalChinese", "Traditional Chinese"),
        new("ZH",      "S.Lang.SimplifiedChinese",  "Simplified Chinese"),
    ];

    public static readonly List<ProviderItem> Providers =
    [
        new(TranslationProvider.Google,    "S.Provider.Google",    false, "S.Provider.GoogleHint"),
        new(TranslationProvider.Google2,   "S.Provider.Google2",   false, "S.Provider.Google2Hint"),
        new(TranslationProvider.GoogleChrome, "S.Provider.GoogleChrome", false, "S.Provider.GoogleChromeHint"),
        new(TranslationProvider.Bing,      "S.Provider.Bing",      false),
        new(TranslationProvider.Microsoft, "S.Provider.Microsoft", false),
        new(TranslationProvider.DeepL,     "S.Provider.DeepL",     true,  "S.Provider.DeepLHint"),
        new(TranslationProvider.OpenAI,    "S.Provider.OpenAI",    false, "S.Provider.OpenAIHint"),
    ];

    /// <summary>
    /// Display name of a provider as shown in the selectors, for use in user-facing messages.
    /// Falls back to the enum name so a newly added provider never shows an empty label.
    /// </summary>
    public static string GetProviderDisplay(TranslationProvider provider) =>
        Providers.FirstOrDefault(p => p.Provider == provider)?.Display ?? provider.ToString();

    public static readonly List<LangItem> TargetLanguages =
    [
        new("EN-US",   "S.Lang.English",            "English"),
        new("JA",      "S.Lang.Japanese",           "Japanese"),
        new("KO",      "S.Lang.Korean",             "Korean"),
        new("ZH-HANT", "S.Lang.TraditionalChinese", "Traditional Chinese"),
        new("ZH-HANS", "S.Lang.SimplifiedChinese",  "Simplified Chinese"),
        new("BG",      "S.Lang.Bulgarian",          "Bulgarian"),
        new("CS",      "S.Lang.Czech",              "Czech"),
        new("DA",      "S.Lang.Danish",             "Danish"),
        new("DE",      "S.Lang.German",             "German"),
        new("EL",      "S.Lang.Greek",              "Greek"),
        new("ES",      "S.Lang.Spanish",            "Spanish"),
        new("ET",      "S.Lang.Estonian",           "Estonian"),
        new("FI",      "S.Lang.Finnish",            "Finnish"),
        new("FR",      "S.Lang.French",             "French"),
        new("HU",      "S.Lang.Hungarian",          "Hungarian"),
        new("ID",      "S.Lang.Indonesian",         "Indonesian"),
        new("IT",      "S.Lang.Italian",            "Italian"),
        new("LT",      "S.Lang.Lithuanian",         "Lithuanian"),
        new("LV",      "S.Lang.Latvian",            "Latvian"),
        new("NB",      "S.Lang.Norwegian",          "Norwegian"),
        new("NL",      "S.Lang.Dutch",              "Dutch"),
        new("PL",      "S.Lang.Polish",             "Polish"),
        new("PT-BR",   "S.Lang.Portuguese",         "Portuguese"),
        new("RO",      "S.Lang.Romanian",           "Romanian"),
        new("RU",      "S.Lang.Russian",            "Russian"),
        new("SK",      "S.Lang.Slovak",             "Slovak"),
        new("SL",      "S.Lang.Slovenian",          "Slovenian"),
        new("SV",      "S.Lang.Swedish",            "Swedish"),
        new("TR",      "S.Lang.Turkish",            "Turkish"),
        new("UK",      "S.Lang.Ukrainian",          "Ukrainian"),
    ];

    public static string? MapTargetToSourceCode(string targetCode)
    {
        if (string.IsNullOrWhiteSpace(targetCode)) return null;

        var mapped = targetCode.ToUpperInvariant() switch
        {
            "ZH-HANT" => "ZH-HANT",
            "ZH-HANS" => "ZH",
            _ => targetCode.Split('-')[0]
        };

        return SourceLanguages.Any(l => l.Code.Equals(mapped, StringComparison.OrdinalIgnoreCase))
            ? SourceLanguages.First(l => l.Code.Equals(mapped, StringComparison.OrdinalIgnoreCase)).Code
            : null;
    }

    public static string? MapSourceToTargetCode(string sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode)) return null;

        var mapped = sourceCode.ToUpperInvariant() switch
        {
            "ZH-HANT" => "ZH-HANT",
            "ZH" => "ZH-HANS",
            "EN" => "EN-US",
            _ => sourceCode
        };

        var exact = TargetLanguages.FirstOrDefault(l =>
            l.Code.Equals(mapped, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Code;

        var prefix = TargetLanguages.FirstOrDefault(l =>
            l.Code.StartsWith(mapped + "-", StringComparison.OrdinalIgnoreCase));
        return prefix?.Code;
    }

    public static bool IsAutomaticSource(string? sourceCode) =>
        AutomaticSourceLanguage.Equals(sourceCode, StringComparison.OrdinalIgnoreCase);

    public static string GetValidSourceCode(string? sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            return DefaultSourceLanguage;

        var match = SourceLanguages.FirstOrDefault(l =>
            l.Code.Equals(sourceCode, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? DefaultSourceLanguage;
    }

    public static string GetValidOcrSourceCode(string? sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            return DefaultOcrSourceLanguage;

        var match = OcrSourceLanguages.FirstOrDefault(l =>
            l.Code.Equals(sourceCode, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? DefaultOcrSourceLanguage;
    }

    /// <summary>
    /// What 即時翻譯 reads from, for a setting that may be blank, invalid, or 自動.
    /// </summary>
    /// <remarks>
    /// Its own resolver rather than <see cref="GetValidOcrSourceCode"/> because the two fall back to
    /// different places: 自動 is a fine answer for a screenshot, which can be read again, and not one
    /// realtime can use — its picker does not offer it, recognition there gets one look at a frame,
    /// and a mode that guesses per frame makes a subtitle track flicker between languages.
    ///
    /// So everything that is not a real language lands on <see cref="DefaultRealtimeSourceLanguage"/>:
    /// a blank from before this had a default, 自動 from a hand-edited file, and a code retired since.
    /// One branch covers all three, and the result is always something the session can actually read.
    /// </remarks>
    public static string GetValidRealtimeSourceCode(string? sourceCode)
    {
        var resolved = GetValidOcrSourceCode(sourceCode);
        return IsAutomaticSource(resolved) ? DefaultRealtimeSourceLanguage : resolved;
    }

    /// <summary>
    /// Codes whose model-facing spelling is not just this application's own code in lower case.
    /// </summary>
    /// <remarks>
    /// The pickers carry codes chosen for the translation APIs — DeepL's <c>EN-US</c>, <c>PT-BR</c>,
    /// <c>ZH-HANT</c> — and a local translation model expects BCP 47 instead. Only the entries that
    /// actually differ are listed; everything else is a plain two-letter code that lower-casing
    /// already gets right, and listing those too would be a table to keep in step for no gain.
    ///
    /// <c>ZH</c> maps to Simplified rather than bare <c>zh</c> because that is what it means in this
    /// application's own OCR picker (簡體中文), and a model given <c>zh</c> has to guess between the
    /// two scripts.
    /// </remarks>
    private static readonly Dictionary<string, string> ModelLanguageTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EN-US"] = "en",
        ["ZH"] = "zh-Hans",
        ["ZH-HANS"] = "zh-Hans",
        ["ZH-HANT"] = "zh-Hant",
        ["PT-BR"] = "pt-BR",
    };

    /// <summary>
    /// The language tag to name a language by when instructing a translation model, e.g. <c>ja</c>.
    /// </summary>
    /// <remarks>
    /// Some translation-only models are trained with the language named as "Japanese (ja)" — the tag
    /// is not decoration beside the name there, it is the part the model was trained to key on. The
    /// built-in prompts no longer ask for one, but <c>{source_code}</c> and <c>{target_code}</c> are
    /// still offered to templates users write — see
    /// <see cref="Services.Providers.OpenAiCompatibleProvider.SourceCodePlaceholder"/>.
    ///
    /// Returns empty for 自動, which has no language to name: a template that names the source while
    /// the source is 自動 gets "any language" there, and a tag would be inventing one.
    /// </remarks>
    public static string GetModelLanguageTag(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || IsAutomaticSource(code))
            return "";

        return ModelLanguageTags.TryGetValue(code, out var mapped)
            ? mapped
            : code.ToLowerInvariant();
    }

    public static string GetValidTargetCode(string? targetCode)
    {
        if (string.IsNullOrWhiteSpace(targetCode))
            return DefaultTargetLanguage;

        var match = TargetLanguages.FirstOrDefault(l =>
            l.Code.Equals(targetCode, StringComparison.OrdinalIgnoreCase));
        return match?.Code ?? DefaultTargetLanguage;
    }

    /// <summary>
    /// A source language's name for saying back to the user what is in effect: the interface's own
    /// name for it, without the English one the pickers also carry, because a line with one
    /// language's worth of room has a reader who already knows which language they are reading in.
    /// Falls back to the code, which is still readable, rather than to an empty string.
    /// </summary>
    /// <param name="inEnglish">
    /// Names it in English regardless of the interface language, for a caller whose sentence is in
    /// English while the interface is not — the built-in prompt templates, which ship in three
    /// wordings for five interface languages and must not end up naming a language in a sixth.
    /// </param>
    public static string GetSourceDisplayName(string? code, bool inEnglish = false) =>
        NameIn(OcrSourceLanguages, code, inEnglish);

    /// <inheritdoc cref="GetSourceDisplayName"/>
    public static string GetTargetDisplayName(string? code, bool inEnglish = false) =>
        NameIn(TargetLanguages, code, inEnglish);

    private static string NameIn(List<LangItem> languages, string? code, bool inEnglish)
    {
        var match = languages.FirstOrDefault(l =>
            l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (match is null) return code ?? "";

        return inEnglish && !string.IsNullOrEmpty(match.English) ? match.English : match.ShortName;
    }
}
