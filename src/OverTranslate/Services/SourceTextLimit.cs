namespace OverTranslate.Services;

/// <summary>
/// The most source text 文字翻譯 and 取詞翻譯 take in, whichever translation service is chosen.
/// </summary>
/// <remarks>
/// The engines split a long text into requests on their own, so this is not about what they can
/// carry. It is about a Ctrl+A that got away on a long page: 取詞翻譯 translates as soon as it
/// opens, and a whole page sent through a 1,000-characters-per-request service is a hundred
/// requests to a free endpoint, or a fifth of a DeepL Free month. Twenty thousand is well past any
/// passage someone selects to read.
/// </remarks>
internal static class SourceTextLimit
{
    public const int MaxLength = 20000;
}
