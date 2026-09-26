namespace OverTranslate.Engines;

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
}
