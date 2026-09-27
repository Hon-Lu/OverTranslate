namespace OverTranslate.Translation;

/// <summary>
/// Finds the sentence a detector left alone because of the label in front of it: 「角色名稱：艾莉絲
/// She has been waiting…」, read as Chinese and handed back whole when Chinese is what was asked for.
/// </summary>
/// <remarks>
/// <para>Measured on 「Google 翻譯 (Web)」 and 「(RPC)」 (2026-09-28); Chrome, Microsoft and Bing
/// translate the same texts. Given <c>sl=en</c>, both Google endpoints translate them too. What the
/// endpoint says it detected is no help in telling these apart: Web read that sentence as
/// <c>en</c> and still left it, read 「提示：Press any key to continue」 as <c>zh-CN</c>, and read
/// 「這個 API 要怎麼用」 — rightly left as it is — as <c>en</c>. So the text is looked at instead: a
/// few words of Latin script beside some Chinese, Japanese or Korean, still there word for word in
/// the answer.</para>
///
/// <para>Only when the rest was left alone as well, or the engine read the text as the language it
/// was asked for (a simplified label comes back in traditional characters, its sentence
/// untouched). A Japanese label that was translated, beside an English sentence that was not, is
/// the other thing: asking again with the sentence's language would lose the label's translation
/// to gain the sentence's.</para>
/// </remarks>
internal static class MixedScriptText
{
    /// <summary>Enough Chinese, Japanese or Korean for the text to be mixed at all.</summary>
    private const int MinimumCloseSetCharacters = 2;

    /// <summary>
    /// Enough of a sentence that leaving it is a miss: 「Press any key to continue」 is five words
    /// and twenty-one letters, while <c>API</c>, <c>Lv.5</c> and a name are well under.
    /// </summary>
    private const int MinimumWords = 3;
    private const int MinimumLetters = 12;

    private static readonly char[] Trimmed =
        [' ', '\t', '\r', '\n', '\'', '’', '-', '.', ',', '!', '?', ';', ':', '"', '(', ')', '&', '/'];

    /// <returns>
    /// The Latin-script clause the answer left as it was, or null when the answer is not one of these.
    /// </returns>
    public static string? UntranslatedClause(string text, TextTranslation answer, string targetLanguage)
    {
        var clause = LatinClause(text);
        if (clause is null || !answer.Text.Contains(clause, StringComparison.OrdinalIgnoreCase))
            return null;

        var untouched = string.Equals(answer.Text.Trim(), text.Trim(), StringComparison.Ordinal);
        return untouched || SameLanguage(answer.DetectedLanguage, targetLanguage) ? clause : null;
    }

    /// <summary>
    /// The longest run of Latin script in a text that also has Chinese, Japanese or Korean in it,
    /// if it is long enough to be a sentence.
    /// </summary>
    internal static string? LatinClause(string text)
    {
        if (text.Count(IsCloseSet) < MinimumCloseSetCharacters) return null;

        string? best = null;
        var bestLetters = 0;
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && IsClausePart(text[i]))
            {
                if (start < 0) start = i;
                continue;
            }

            if (start < 0) continue;
            var span = text[start..i].Trim(Trimmed);
            start = -1;

            var letters = span.Count(IsLatinLetter);
            if (letters > bestLetters)
            {
                best = span;
                bestLetters = letters;
            }
        }

        if (best is null) return null;

        var words = best.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Count(word => word.Count(IsLatinLetter) >= 2);
        return words >= MinimumWords && bestLetters >= MinimumLetters ? best : null;
    }

    /// <summary>Whether two language codes name the same language, script and region aside.</summary>
    public static bool SameLanguage(string a, string b) =>
        a.Length > 0 && b.Length > 0 &&
        string.Equals(BaseOf(a), BaseOf(b), StringComparison.OrdinalIgnoreCase);

    private static string BaseOf(string code)
    {
        var dash = code.IndexOf('-');
        return dash < 0 ? code : code[..dash];
    }

    // Latin letters with their accents, so a French or German sentence counts as much as English.
    private static bool IsLatinLetter(char c) => char.IsLetter(c) && c <= 'ɏ';

    private static bool IsClausePart(char c) =>
        IsLatinLetter(c) || c is >= '0' and <= '9' || Array.IndexOf(Trimmed, c) >= 0;

    private static bool IsCloseSet(char c) =>
        c is >= '぀' and <= 'ヿ'      // kana
            or >= '㐀' and <= '鿿'    // Han
            or >= '가' and <= '힯'    // Hangul
            or >= '豈' and <= '﫿';   // compatibility ideographs
}
