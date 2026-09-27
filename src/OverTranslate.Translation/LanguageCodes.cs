namespace OverTranslate.Translation;

/// <summary>
/// Translates between the codes this library speaks (Google's) and the ones each engine wants.
/// </summary>
/// <remarks>
/// Only the codes that differ are listed. Everything else is the same two letters everywhere,
/// which is most of them — and a code that is missing here reaches the engine unchanged, where a
/// wrong one fails loudly rather than translating into the wrong language.
/// </remarks>
internal static class LanguageCodes
{
    /// <summary>A Google-style code as Microsoft and Bing write it.</summary>
    /// <remarks>The same table GTranslate's <c>MicrosoftHotPatch</c> and <c>BingHotPatch</c> carry.</remarks>
    public static string ToMicrosoft(string code) => code switch
    {
        "zh-CN" => "zh-Hans",
        "zh-TW" => "zh-Hant",
        "no"    => "nb",
        "lg"    => "lug",
        "ny"    => "nya",
        "rn"    => "run",
        "sr"    => "sr-Cyrl",
        "mn"    => "mn-Cyrl",
        "tlh"   => "tlh-Latn",
        _       => code,
    };

    /// <summary>A language Microsoft or Bing detected, as Google would have written it.</summary>
    public static string FromMicrosoft(string code) => code switch
    {
        "zh-Hans" => "zh-CN",
        "zh-Hant" => "zh-TW",
        "nb"      => "no",
        "lug"     => "lg",
        "nya"     => "ny",
        "run"     => "rn",
        "sr-Cyrl" or "sr-Latn" => "sr",
        "mn-Cyrl" => "mn",
        _         => code,
    };

    /// <summary>
    /// A language Google detected, in the codes the rest of the world uses where Google kept an
    /// older one.
    /// </summary>
    public static string FromGoogle(string code) => code switch
    {
        "iw" => "he",
        "jw" => "jv",
        _    => code,
    };

    /// <summary>A Google-style code as DeepL's <c>target_lang</c>.</summary>
    /// <remarks>
    /// English and Portuguese need a variant as a target — the bare codes are only kept for old
    /// clients — and the application offers American English and Brazilian Portuguese, which is
    /// also what Google's <c>en</c> and <c>pt</c> are.
    /// </remarks>
    public static string ToDeepLTarget(string code) => code switch
    {
        "en"    => "EN-US",
        "pt"    => "PT-BR",
        "zh-CN" => "ZH-HANS",
        "zh-TW" => "ZH-HANT",
        "no"    => "NB",
        _       => code.ToUpperInvariant(),
    };

    /// <summary>A Google-style code as DeepL's <c>source_lang</c>, which takes no variants.</summary>
    /// <remarks>
    /// Chinese is one source language to DeepL whichever script it is written in: its source list
    /// has <c>ZH</c> and nothing else, where the target list has <c>ZH-HANS</c> and <c>ZH-HANT</c>.
    /// </remarks>
    public static string ToDeepLSource(string code) => code switch
    {
        "no" => "NB",
        _    => code.Split('-')[0].ToUpperInvariant(),
    };

    /// <summary>A language DeepL detected, as Google would have written it.</summary>
    public static string FromDeepL(string code) => code.ToUpperInvariant() switch
    {
        "ZH" => "zh-CN",
        "NB" => "no",
        var other => other.ToLowerInvariant(),
    };
}
