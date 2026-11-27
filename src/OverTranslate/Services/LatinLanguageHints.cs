namespace OverTranslate.Services;

/// <summary>
/// Which of two Latin-script languages a text is in, from what only one of them writes.
/// </summary>
/// <remarks>
/// <para>Two tells, counted for a language only when the other half of the pair does not share them:
/// its most common function words (le, der, el, the …) and the accented letters it uses (è, ß, ñ, ã …).
/// 「de」 counts for neither of 法文／西班牙文, and 「é」 for neither of 法文／葡萄牙文; a shared
/// tell says nothing about which of the two it is.</para>
///
/// <para>Measured (#441) on 48 sentences, 24 short phrases and 15 single words per language, for all
/// fifteen pairs of the six: 693 of 720 sentences named right and 2 wrong, the rest undecided; on
/// English pairs none wrong. Real Wikipedia prose came out 92 % right, most of the misses English
/// citation titles inside the other language's page. Single words are mostly
/// undecided, which is the answer they deserve — 「chat」 and 「pain」 are words in both. What it
/// buys is the first request: the engine's detected language and the echo test in
/// <see cref="BilingualTranslation"/> still correct any guess, this or the old 「the first language」,
/// so a wrong one costs one request and a right one saves it. Against Microsoft and Google that took
/// a sentence from 1.5 requests to 1.1 on average with the same final answers, and with an engine
/// that reports no language at all it named the source of 60–69 sentences in 72 instead of 35.</para>
/// </remarks>
internal static class LatinLanguageHints
{
    private sealed record Hints(HashSet<string> Words, string Letters);

    private static readonly Dictionary<string, Hints> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EN"] = new(Set("the of and to in is you that it for are was on with as this be at have from or by not but what all were we when your can there an which they he she my if do would will so no don't i'm it's"), ""),
        ["FR"] = new(Set("le la les de des du en y et est un une que qui pour dans pas sur avec il elle nous vous ce cette au aux ne se sont je tu mon ma mes son sa ses leur être c'est n'est d'un d'une l'"), "éèêëàâçœæîïôûùÿ"),
        ["DE"] = new(Set("der die das und ist nicht ein eine zu den von mit sich des auf für im in dem auch es an als wir sie ich du bitte wird oder aus bei nach noch kein dass"), "äöüß"),
        ["ES"] = new(Set("el la los las de del y que en un una es por con no para se su al lo como más pero sus le ya este esta muy sin sobre también"), "ñáéíóúü¿¡"),
        ["IT"] = new(Set("il lo la gli le di del della dei e che un una è per con non sono si da al in nel questo questa ma come più anche ho hai mi ti ci"), "àèéìíòóù"),
        ["PT"] = new(Set("o os as de do da dos das e que um uma é para com não por se em no na nos nas ao à mais mas como seu sua você eu"), "ãõâêôáéíóúàçü"),
    };

    /// <summary>The language of the pair the text is in, or null when it does not say.</summary>
    /// <param name="first">A target code (EN-US, FR, PT-BR) or a source one (EN, PT).</param>
    public static string? Resolve(string text, string first, string second)
    {
        if (!Languages.TryGetValue(BaseCode(first), out var a) ||
            !Languages.TryGetValue(BaseCode(second), out var b) ||
            BaseCode(first).Equals(BaseCode(second), StringComparison.OrdinalIgnoreCase))
            return null;

        var lower = text.ToLowerInvariant();
        int scoreA = 0, scoreB = 0;

        foreach (var c in lower)
        {
            var inA = a.Letters.Contains(c);
            var inB = b.Letters.Contains(c);
            if (inA && !inB) scoreA++;
            else if (inB && !inA) scoreB++;
        }

        foreach (var word in Words(lower))
        {
            var inA = a.Words.Contains(word);
            var inB = b.Words.Contains(word);
            if (inA && !inB) scoreA++;
            else if (inB && !inA) scoreB++;
        }

        return scoreA > scoreB ? first : scoreB > scoreA ? second : null;
    }

    private static string BaseCode(string code) => code.Split('-')[0];

    /// <summary>
    /// Runs of letters and apostrophes, with an elided article (l', d', n') also counted on its own:
    /// 「l'hôtel」 is French by its l' whatever the noun is.
    /// </summary>
    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            var part = char.IsLetter(c) || ((c is '\'' or '’') && start >= 0);
            if (part)
            {
                if (start < 0) start = i;
                continue;
            }
            if (start < 0) continue;

            var word = text[start..i].Replace('’', '\'').TrimEnd('\'');
            start = -1;
            if (word.Length == 0) continue;
            yield return word;
            var apostrophe = word.IndexOf('\'');
            if (apostrophe > 0 && apostrophe < word.Length - 1)
                yield return word[..(apostrophe + 1)];
        }
    }

    private static HashSet<string> Set(string words) =>
        new(words.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}
