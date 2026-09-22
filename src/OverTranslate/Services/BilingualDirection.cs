using System.Text;
using OpenccNetLib;

namespace OverTranslate.Services;

/// <summary>
/// Which half of a 雙語互譯 pair a piece of text is written in.
/// </summary>
/// <remarks>
/// The question this answers is deliberately narrow, and that is what makes it answerable. It is not
/// 「what language is this?」 — an open-set guess that every statistical detector gets wrong on short
/// input, which is exactly what 取詞翻譯 is given: one word, pulled out of a sentence. It is 「of
/// these two, which one」, and for any pair written in different scripts that is decidable from the
/// characters alone, at any length, down to a single one.
///
/// So the whole of it is a count. Each of the two languages is scored by how many characters of the
/// text are written in a script that language uses, and the higher score wins. Scoring per language
/// rather than deciding a single script for the text is what makes Japanese work: kana and Han are
/// both Japanese, so a sentence in both scores for 日文 twice over, while 中文 only counts the Han —
/// and a lone 「大丈夫」 with no kana at all still scores for 日文 as long as the other half of the
/// pair is not another Han language.
///
/// A tie is an honest answer, not a failure. It means either that the text is in neither language,
/// or that the pair shares a script — 英文／法文, 繁體／簡體 — and there is nothing in the characters
/// to separate them. The caller translates into the first language, which is what the settings panel
/// already promises for anything unrecognised, and the engine's own reported source language settles
/// it afterwards where it can.
/// </remarks>
internal static class BilingualDirection
{
    /// <summary>
    /// The language <paramref name="text"/> appears to be written in.
    /// </summary>
    /// <returns>
    /// One of <paramref name="first"/> or <paramref name="second"/> when the characters say so; a
    /// third language when the script names one on its own and neither half of the pair claims it;
    /// or null when nothing can be told, which the caller reads as 「translate into the first」.
    /// </returns>
    public static string? ResolveSource(string text, string first, string second)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var tally = Tally(text);
        var firstScore = Score(first, tally);
        var secondScore = Score(second, tally);

        if (firstScore > secondScore) return first;
        if (secondScore > firstScore) return second;

        // Neither language has a single character it could have written, so whatever this is, it is
        // not one of the two. Naming it is a bonus for the header rather than something the
        // direction depends on — the answer is the first language either way.
        if (firstScore == 0) return SoleLanguageOf(tally);

        return ResolveSharedScript(text, first, second);
    }

    /// <summary>
    /// Separates a pair that is written in one script, in the one case where that is possible.
    /// </summary>
    /// <remarks>
    /// 繁體／簡體 is the one shared-script pair this application's readers plausibly set, and it is
    /// also the one that can be settled locally: OpenCC is already here for the dictionary, and its
    /// <c>ZhoCheck</c> is the same test 繁簡轉換 relies on. Two Latin languages or two Cyrillic ones
    /// have no such tell and are left to the engine.
    /// </remarks>
    private static string? ResolveSharedScript(string text, string first, string second)
    {
        if (!IsChineseVariantPair(first, second)) return null;

        return Opencc.ZhoCheck(text) switch
        {
            1 => TraditionalChinese,
            2 => SimplifiedChinese,
            _ => null,
        };
    }

    private const string TraditionalChinese = "ZH-HANT";
    private const string SimplifiedChinese = "ZH-HANS";

    private static bool IsChineseVariantPair(string first, string second) =>
        (Is(first, TraditionalChinese) && Is(second, SimplifiedChinese)) ||
        (Is(first, SimplifiedChinese) && Is(second, TraditionalChinese));

    private static bool Is(string code, string other) =>
        code.Equals(other, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one language a script belongs to, where there is only one.
    /// </summary>
    /// <remarks>
    /// Kana, Hangul and Greek each name a language by themselves among the languages this
    /// application offers. Han does not — it could be either Chinese or Japanese — and Latin and
    /// Cyrillic are shared by twenty-odd languages between them, so those stay unnamed.
    /// </remarks>
    private static string? SoleLanguageOf(in ScriptTally tally)
    {
        var best = Math.Max(tally.Kana, Math.Max(tally.Hangul, tally.Greek));
        if (best == 0) return null;

        if (tally.Kana == best) return "JA";
        return tally.Hangul == best ? "KO" : "EL";
    }

    private static int Score(string languageCode, in ScriptTally tally) => FamilyOf(languageCode) switch
    {
        // Han only: Chinese does not write in kana, so a Japanese sentence scores here for its
        // kanji alone and loses to 日文, which counts both halves of its own writing system.
        LanguageFamily.Chinese  => tally.Han,
        LanguageFamily.Japanese => tally.Han + tally.Kana,

        // Hangul only. Hanja is rare enough in modern Korean that counting Han here would turn
        // 中文／韓文 into a tie on every Chinese input, which is a far worse trade.
        LanguageFamily.Korean   => tally.Hangul,

        LanguageFamily.Cyrillic => tally.Cyrillic,
        LanguageFamily.Greek    => tally.Greek,

        // Latin letters count for Latin languages alone, never for 日文 or 中文. Japanese prose is
        // full of Latin words, and letting it claim them would make 「iPhone」 a tie under 日文／英文
        // instead of the English it plainly is.
        _ => tally.Latin,
    };

    private enum LanguageFamily { Latin, Chinese, Japanese, Korean, Cyrillic, Greek }

    /// <remarks>
    /// Takes both the target codes the pair is stored in (EN-US, ZH-HANT, PT-BR) and the source-side
    /// codes an engine reports a detected language as (EN, ZH, PT), because both reach this.
    /// </remarks>
    private static LanguageFamily FamilyOf(string code)
    {
        var upper = code.ToUpperInvariant();

        if (upper.StartsWith("ZH", StringComparison.Ordinal)) return LanguageFamily.Chinese;

        return upper.Split('-')[0] switch
        {
            "JA" => LanguageFamily.Japanese,
            "KO" => LanguageFamily.Korean,
            "BG" or "RU" or "UK" => LanguageFamily.Cyrillic,
            "EL" => LanguageFamily.Greek,
            _ => LanguageFamily.Latin,
        };
    }

    private readonly record struct ScriptTally(
        int Han, int Kana, int Hangul, int Latin, int Cyrillic, int Greek);

    /// <remarks>
    /// Runes rather than chars, so the Han extensions above the basic plane are counted once each
    /// rather than twice as surrogate halves that belong to no script at all.
    ///
    /// Digits, punctuation, spaces and symbols are counted for nobody. They carry no script — every
    /// language in the pair writes them the same way — so a line of them is left undecided rather
    /// than scored for whichever half happens to be asked first. Letters beside them still count:
    /// 「Lv.100」 is two Latin letters and three digits, and the letters are the part that answers.
    ///
    /// Its own ranges rather than <c>LayoutScriptDetection</c>'s. That one folds Han and kana
    /// together into one CJK bucket, which is the right partition for choosing grouping geometry and
    /// the wrong one here: telling 中文 from 日文 is the entire job of the kana column.
    /// </remarks>
    private static ScriptTally Tally(string text)
    {
        int han = 0, kana = 0, hangul = 0, latin = 0, cyrillic = 0, greek = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case var v when IsHan(v):      han++;      break;
                case var v when IsKana(v):     kana++;     break;
                case var v when IsHangul(v):   hangul++;   break;
                case var v when IsLatin(v):    latin++;    break;
                case var v when IsCyrillic(v): cyrillic++; break;
                case var v when IsGreek(v):    greek++;    break;
            }
        }

        return new ScriptTally(han, kana, hangul, latin, cyrillic, greek);
    }

    private static bool IsHan(int v) =>
        v is >= 0x3400 and <= 0x4DBF or   // Extension A
            >= 0x4E00 and <= 0x9FFF or    // Unified Ideographs
            >= 0xF900 and <= 0xFAFF or    // Compatibility Ideographs
            >= 0x20000 and <= 0x323AF;    // Extensions B and up

    /// <remarks>
    /// U+30FB 「・」 is inside the katakana block but is a separator, used in Chinese and Japanese
    /// alike for foreign names. Counting it would let one middle dot in a Chinese line score for
    /// 日文. U+30FC 「ー」 stays: the prolonged sound mark is Japanese and nothing else uses it.
    /// </remarks>
    private static bool IsKana(int v) =>
        v != 0x30FB &&
        (v is >= 0x3041 and <= 0x309F or   // Hiragana
             >= 0x30A0 and <= 0x30FF or    // Katakana
             >= 0x31F0 and <= 0x31FF or    // Katakana phonetic extensions
             >= 0xFF66 and <= 0xFF9D);     // Halfwidth katakana

    private static bool IsHangul(int v) =>
        v is >= 0x1100 and <= 0x11FF or    // Jamo
            >= 0x3130 and <= 0x318F or     // Compatibility jamo
            >= 0xA960 and <= 0xA97F or     // Jamo Extended-A
            >= 0xAC00 and <= 0xD7A3 or     // Syllables
            >= 0xD7B0 and <= 0xD7FF;       // Jamo Extended-B

    private static bool IsLatin(int v) =>
        v is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= 0x00C0 and <= 0x024F or     // Latin-1 letters, Extended-A and Extended-B
            >= 0x1E00 and <= 0x1EFF or     // Extended Additional
            >= 0xFF21 and <= 0xFF3A or     // Fullwidth A-Z
            >= 0xFF41 and <= 0xFF5A;       // Fullwidth a-z

    private static bool IsCyrillic(int v) =>
        v is >= 0x0400 and <= 0x052F;

    private static bool IsGreek(int v) =>
        v is >= 0x0370 and <= 0x03FF or
            >= 0x1F00 and <= 0x1FFF;
}
