using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>
/// The two languages 雙語互譯 moves between, and every rule about them that is not about reading
/// the text — see <see cref="BilingualDirection"/> for that half.
/// </summary>
/// <remarks>
/// Shared by 取詞翻譯 and 替換翻譯, which each keep a pair of their own (#322). The rules are the
/// same in both, and having them in one place is what keeps the two from drifting apart: a swap,
/// a fallback or a correction that behaved differently in the popup and in the replacement would be
/// one feature with two meanings.
///
/// <see cref="First"/> is also the fallback: text in neither language is translated into it.
/// </remarks>
internal readonly record struct BilingualPair(string First, string Second)
{
    /// <summary>The stored pair, made safe to translate with.</summary>
    /// <remarks>
    /// One language twice is a pair that translates nothing into itself. The pickers cannot produce
    /// it — see <see cref="Pick"/> — so it can only come from a hand-edited settings file, and the
    /// second slot is the one to move: the first is also the fallback, and redirecting that would
    /// change what every translation does rather than just this pair.
    /// </remarks>
    public static BilingualPair Load(string? first, string? second)
    {
        var validFirst  = LanguageData.GetValidTargetCode(first);
        var validSecond = LanguageData.GetValidTargetCode(second);

        if (string.Equals(validFirst, validSecond, StringComparison.OrdinalIgnoreCase))
        {
            validSecond = string.Equals(validFirst, QuickLookupSettings.DefaultSecondLanguage,
                                        StringComparison.OrdinalIgnoreCase)
                ? QuickLookupSettings.DefaultFirstLanguage
                : QuickLookupSettings.DefaultSecondLanguage;
        }

        return new BilingualPair(validFirst, validSecond);
    }

    /// <summary>The pair after a pick in one slot, with the other slot settled.</summary>
    /// <param name="firstSlot">Which slot was picked in — the first is also the fallback.</param>
    /// <remarks>
    /// Picking the language the other slot is already holding is how the pair gets turned around.
    /// There is no reading of 「英文 ⇄ 英文」 that translates anything, and the one thing the user
    /// can have meant is that the language they just displaced goes to the other side — which is
    /// also the only way to change which of the two is the fallback.
    /// </remarks>
    public BilingualPair Pick(string picked, bool firstSlot)
    {
        var first  = firstSlot ? picked : First;
        var second = firstSlot ? Second : picked;

        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            if (firstSlot) second = First;
            else           first  = Second;
        }

        return new BilingualPair(first, second);
    }

    /// <summary>Which of the pair a text in <paramref name="source"/> is translated into.</summary>
    /// <remarks>
    /// The whole of what 雙語互譯 decides, in one line. The second language is the answer only for
    /// text already in the first; everything else — including the languages the pair does not
    /// mention at all, and text nothing could be told about — goes to the first, which is what makes
    /// it the first.
    /// </remarks>
    public string TargetFor(string? source) =>
        string.Equals(source, First, StringComparison.OrdinalIgnoreCase) ? Second : First;

    /// <summary>
    /// What the translation should have been asked for, once the engine has said what the text was.
    /// </summary>
    /// <param name="source">
    /// The language the engine's answer places the text in, where it places it anywhere — for the
    /// caller to show; null when the answer says nothing new.
    /// </param>
    /// <returns>
    /// The language to ask again in, or null when the guess already stands — which is the ordinary
    /// case, and the whole reason the characters are consulted first.
    /// </returns>
    /// <remarks>
    /// An OpenAI-compatible server reports nothing here and this does nothing, leaving the guess in
    /// place. For a pair written in two scripts that guess was never in doubt; for one written in
    /// the same script it means the mode stays on the first language, which is what the settings
    /// say happens to anything it cannot place.
    /// </remarks>
    public string? Correct(string? detected, string sentTarget, out string? source)
    {
        source = null;
        if (string.IsNullOrEmpty(detected)) return null;

        var key = LanguageKey(detected);
        var isFirst = LanguageKey(First) == key;
        var isSecond = LanguageKey(Second) == key;

        // 繁體 and 簡體 are both 「ZH」 to every engine that reports one, so on that pair this
        // answer cannot separate what OpenCC already separated from the characters. It stands aside
        // rather than overwriting a better answer with a coarser one.
        if (isFirst && isSecond) return null;

        source = isFirst ? First
               : isSecond ? Second
               : LanguageData.MapSourceToTargetCode(detected);

        var wanted = isFirst ? Second : First;
        return wanted.Equals(sentTarget, StringComparison.OrdinalIgnoreCase) ? null : wanted;
    }

    /// <summary>
    /// A language code reduced to the language it names, so the pair and the engine can be compared.
    /// </summary>
    /// <remarks>
    /// The pair is stored in target codes (EN-US, PT-BR) and an engine reports source codes (EN,
    /// PT), so neither side can be matched against the other as written. 繁體 and 簡體 both fold to
    /// ZH deliberately: an engine saying 「Chinese」 has not said which, and pretending otherwise is
    /// how this would come to re-translate 中文 into 中文.
    /// </remarks>
    private static string LanguageKey(string code)
    {
        var upper = code.ToUpperInvariant();
        return upper.StartsWith("ZH", StringComparison.Ordinal) ? "ZH" : upper.Split('-')[0];
    }
}
