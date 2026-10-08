namespace OverTranslate.Translation;

/// <summary>
/// Divides one request's texts by the script they are written in, for an engine that detects one
/// language for a whole request rather than one per text.
/// </summary>
/// <remarks>
/// <para>Youdao and TranSmart both answer a detected request with a single source language, and
/// translate every text in it from that one. Measured 2026-10-08: an English, a Japanese and a
/// Korean sentence sent together were all read as Korean by TranSmart, and the Japanese one came
/// back as 「是的，你需要嗎？」; Youdao read 「안녕하세요」 and 「Hello」 as Korean and left the English
/// untranslated. A screen is usually in one language, but a Japanese game's 「HP」 and 「OK」 are not.</para>
///
/// <para>So a detected request is sent as one request per script, side by side, each still detected
/// by the engine. Nothing here decides a language — kana is not taken to mean Japanese, nor Han to
/// mean Chinese — it only keeps texts the detector would have to read differently apart, so its one
/// answer is right for all of them. A screen in one script is still one request.</para>
/// </remarks>
internal static class ScriptGroups
{
    private enum Script { Kana, Hangul, Han, Other }

    /// <summary>The texts' indices, one list per script found, in the order each script first appears.</summary>
    public static IReadOnlyList<IReadOnlyList<int>> Of(IReadOnlyList<string> texts)
    {
        var groups = new Dictionary<Script, List<int>>();
        var order = new List<Script>();

        for (var i = 0; i < texts.Count; i++)
        {
            var script = ScriptOf(texts[i]);
            if (!groups.TryGetValue(script, out var indices))
            {
                groups[script] = indices = [];
                order.Add(script);
            }
            indices.Add(i);
        }

        return order.Select(script => (IReadOnlyList<int>)groups[script]).ToList();
    }

    /// <summary>
    /// Sends each script's texts as a request of its own, side by side, and puts the answers back in
    /// the order the texts were given.
    /// </summary>
    /// <param name="send">Sends one request; must answer exactly one entry per text it was given.</param>
    public static async Task<IReadOnlyList<TextTranslation?>> SendAsync(
        string engineName, IReadOnlyList<string> texts,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<TextTranslation?>>> send)
    {
        var groups = Of(texts);
        if (groups.Count == 1) return await send(texts);

        var answers = await Task.WhenAll(groups.Select(group => send(group.Select(i => texts[i]).ToList())));

        var merged = new TextTranslation?[texts.Count];
        for (var g = 0; g < groups.Count; g++)
        {
            // Checked here rather than left to the base class, which would only see the total and
            // could not tell which group's answers had slid into which texts.
            if (answers[g].Count != groups[g].Count)
                throw new TranslationEngineException(engineName, $"answered {answers[g].Count} of {groups[g].Count} texts");

            for (var k = 0; k < groups[g].Count; k++)
                merged[groups[g][k]] = answers[g][k];
        }

        return merged;
    }

    /// <remarks>
    /// Kana before Han, because Japanese is written with both and a single kana is what tells it
    /// apart; Hangul before Han for the same reason in the rarer Korean text that carries Hanja.
    /// </remarks>
    private static Script ScriptOf(string text)
    {
        bool han = false, hangul = false;
        foreach (var c in text)
        {
            if (c is >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ' or >= 'ｦ' and <= 'ﾟ') return Script.Kana;
            if (c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏') hangul = true;
            else if (c is >= '㐀' and <= '鿿' or >= '豈' and <= '﫿') han = true;
        }

        return hangul ? Script.Hangul : han ? Script.Han : Script.Other;
    }
}
