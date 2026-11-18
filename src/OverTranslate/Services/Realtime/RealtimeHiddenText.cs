using System.Text;
using System.Text.RegularExpressions;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// The groups a realtime block reads, follows and keeps track of like any other, but does not
/// draw, because a translation of them would tell the reader nothing the screen does not: a
/// number, a level, a stat's abbreviation, a fence read as a row of O's. Decided from the text
/// alone, so the same text is hidden wherever it is and whichever engine translates.
/// </summary>
/// <remarks>
/// <para>Hidden, not dropped. A group judged here stays in everything the region knows — the
/// strips it watches, the reading it compares the next one with, the lines it follows while the
/// page moves, what counts as having gone quiet — and only the overlay leaves it out. Dropped
/// before all that, a dialogue box going from a sentence to 「……」 would read as empty, and an empty
/// read is believed only the second time, which a picture that then holds still never gives it:
/// the sentence's translation would stay up over the 「……」. Kept, the reading has changed, and the
/// sentence's translation goes at the pass that saw it go, as any change does.</para>
///
/// <para>Judged group by group, after grouping, never line by line: a number that is part of a
/// sentence, or sits in one group with a word, is that group's and is drawn with it. So every
/// group with a word in it is drawn exactly as it was before this existed.</para>
///
/// <para>MEASURED (2026-11-19) over 591 captures and 165 more, in all three presets, every group
/// <see cref="NothingToTranslate"/> takes looked at by eye: none of them had anything in it a
/// reader would want translated. What the translators make of them (499 short texts, seven
/// engines): numbers and levels come back as themselves 86–90% of the time and otherwise get
/// worse (30s → 30年代, L 15 → 長15, 100! → 100年!); stat abbreviations come back as the wrong
/// word a third of the time (HP → 惠普, MP → 國會議員, DEX → 地塞米松) on every engine but one;
/// strings of O's and l's come back as words (OOO → 物件導向, Ill → 患病的).</para>
/// </remarks>
internal static class RealtimeHiddenText
{
    /// <summary>
    /// Whether a group is left untranslated and undrawn before any translation is asked for:
    /// <see cref="CarriesNoWords"/> or <see cref="IsShapeNoise"/>.
    /// </summary>
    public static bool NothingToTranslate(string? text) => CarriesNoWords(text) || IsShapeNoise(text);

    /// <summary>
    /// Whether a group holds no words a translation could help with: numbers and symbols, with
    /// nothing beside them but single letters, level marks and stat abbreviations — or symbols
    /// alone, or stat abbreviations alone.
    /// </summary>
    /// <remarks>
    /// <para>What is left out, word by word:</para>
    /// <list type="bullet">
    /// <item>digits, symbols and punctuation (12:30, 1/2, +5, 108%, ★, 「……」);</item>
    /// <item>one Latin letter, against a number or beside it: a unit (84s, 7600R, x958), a button
    /// or a grade (A 1/3, 89 F 120), or an icon the recogniser read as a letter (a 189);</item>
    /// <item>a level mark, as printed or as misread over a moving picture (Lv, Lvl, Lvi, LxI);</item>
    /// <item>a stat abbreviation (HP, MP, SP, EP, ATK, DEX…) — see <see cref="StatAbbreviations"/> —
    /// and a sort label of single letters (A-Z).</item>
    /// </list>
    /// <para>Any other letter is a word and keeps the group: two Latin letters together that are not
    /// a listed abbreviation (OK, GO, Io, No, AM, Dev), and every letter of any other script (是,
    /// はい, 월, 5月, 15일). A lone letter or a misread level mark needs a number beside it to be
    /// left out; a listed abbreviation does not, and 「HP」 or 「Lv」 alone is left out as
    /// 「HP 100/200」 is. Full-width Latin is folded first, so ｘ９５８ is judged as x958.</para>
    ///
    /// <para>「OpenAI 相容」 does not even send a text without a letter in it, and hands it back as its
    /// own translation (see OpenAiChatTranslator).</para>
    /// </remarks>
    public static bool CarriesNoWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var folded = FoldWidth(text);

        var digits = false;
        var letters = false;
        var abbreviation = false;
        foreach (var word in folded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var count = 0;
            var latin = true;
            var hasDigit = false;
            // By rune, so a letter outside the basic plane (𠮷) is a letter and not two symbols.
            foreach (var rune in word.EnumerateRunes())
            {
                if (Rune.IsNumber(rune)) hasDigit = true;
                else if (Rune.IsLetter(rune))
                {
                    count++;
                    latin &= rune.IsAscii;
                }
            }

            digits |= hasDigit;
            if (count == 0) continue;
            letters = true;
            if (!latin) return false;
            if (count == 1) continue;
            var runs = LetterRuns(word);
            // Before the level mark: LV and Lv are listed, and alone they still say nothing.
            if (runs.All(IsStatAbbreviation) || IsSortLabel(word, runs))
            {
                abbreviation = true;
                continue;
            }
            if (runs.Count == 1 && LevelMark.IsMatch(runs[0])) continue;
            return false;
        }

        // Symbols alone carry no words either; a letter or a level mark needs a number to be the
        // unit or the mark of.
        return digits || !letters || abbreviation;
    }

    /// <summary>
    /// The abbreviations a game's numbers are labelled with, which no translator can read without
    /// the game around them.
    /// </summary>
    /// <remarks>
    /// <para>A list rather than a shape. Two to four capitals is also GO, OK, RUN, BAG, NEW, END,
    /// TOP, SAVE, LOAD, SKIP, AUTO, QUIT, Dev — words a reader may need translated, and every
    /// engine translates them. Only what is listed is left out, so nothing outside it ever is.</para>
    ///
    /// <para>MEASURED (2026-11-19, 63 abbreviations, en→zh-TW): the wrong word 20 times on Google,
    /// 16 on Bing, 21 on 有道, 5 on Microsoft — HP as 惠普 and MP as 國會議員 on all of them — and the
    /// game's meaning 13–22 times (EXP → 經驗值, ATK → 攻擊力), which is what leaving them out gives
    /// up: a reader of the game knows them anyway. In capitals, or a capital and lower case (Hp,
    /// Atk, Def → 定義); KO in capitals only. Not CON, which is a word, nor EVA, which is a name.
    /// FPS with them: an overlay's frame counter, read beside a game's numbers as often as they are.
    /// Outside a game the same letters can mean what the translator said (the AP of a news logo, the
    /// PP stamped on a plastic tub), and there too they are left as they are.</para>
    /// </remarks>
    internal static readonly IReadOnlySet<string> StatAbbreviations = new HashSet<string>(StringComparer.Ordinal)
    {
        "HP", "MP", "SP", "EP", "TP", "AP", "BP", "PP", "CP", "XP", "EXP", "LV", "LVL",
        "ATK", "DEF", "SPD", "DEX", "STR", "INT", "AGI", "LUK", "VIT", "MND", "WIS", "CHA",
        "MAG", "RES", "CRIT", "ACC", "DMG", "DPS", "AOE", "MOV", "KO", "FPS",
    };

    private static bool IsStatAbbreviation(string letters)
    {
        var upper = letters.ToUpperInvariant();
        if (!StatAbbreviations.Contains(upper)) return false;
        if (letters == upper) return true;
        // Hp, Atk, Def — not hP, and not Ko, which is a name as often as a knockout.
        return upper != "KO" && char.IsUpper(letters[0]) && letters[1..] == letters[1..].ToLowerInvariant();
    }

    // A-Z, Z-A, A~Z: a sort button. Single letters joined by a dash or a tilde, nothing else —
    // U.S. and I/O are words.
    private static bool IsSortLabel(string word, List<string> runs) =>
        runs.Count >= 2 && runs.All(run => run.Length == 1)
        && word.All(c => char.IsLetter(c) || c is '-' or '~' or '‐' or '–' || char.IsSymbol(c));

    // Lv, LV, lv, Lvl, LVL, lvl — and what the recogniser makes of them over a moving picture:
    // Lvi, LvI, Lvt, LvL, LxI, Ly, Lw, LL. The second letter is one a v is misread as and no
    // English word of three letters or fewer starts with: "Lo", "Lot", "Lol" and "Lap" stay words.
    private static readonly Regex LevelMark =
        new("^(?:[Ll][vVyYwWxX][lLiItT]?|LL)$", RegexOptions.CultureInvariant);

    // The runs of letters in a word, in order: "HP/MP" is HP and MP, "Lv15" is Lv.
    private static List<string> LetterRuns(string word)
    {
        var runs = new List<string>();
        var run = new StringBuilder();
        foreach (var c in word)
        {
            if (char.IsLetter(c))
            {
                run.Append(c);
                continue;
            }
            if (run.Length > 0) runs.Add(run.ToString());
            run.Clear();
        }
        if (run.Length > 0) runs.Add(run.ToString());
        return runs;
    }

    /// <summary>
    /// Whether a group is a pattern the recogniser read as letters: three or more characters, every
    /// one of them from the same family of look-alikes — O o 0 ○, l I i 1 |, 一 ー ―, 口 ロ □.
    /// </summary>
    /// <remarks>
    /// <para>A row of rings on a costume came back as OOOOOO six readings in nine; a fence, a
    /// railing or a striped wall does the same with l and 1. Translators make words of them —
    /// OOO → 物件導向, Ill → 患病的, 口口口 → 嘴巴, 一一一 → 一點十一一 — and a pattern that holds still
    /// would keep one up for as long as it is there.</para>
    ///
    /// <para>One family only, so a word made of two of them is still a word: Io, lol, Oil. Three at
    /// least, so 一一 (one by one) and OO are words too. And not a row of capital I's, which is a
    /// Roman numeral as often as it is a fence — III comes back as 三 from the translators that
    /// change it. Strings with digits in them are <see cref="CarriesNoWords"/>'s.</para>
    /// </remarks>
    public static bool IsShapeNoise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var marks = FoldWidth(text).Where(c => !char.IsWhiteSpace(c) && (char.IsLetterOrDigit(c) || LookAlikeSymbols.Contains(c))).ToList();
        if (marks.Count < 3 || marks.All(c => c == 'I')) return false;
        return LookAlikes.Any(family => marks.All(family.Contains));
    }

    private static readonly string[] LookAlikes = ["Oo0○〇◯", "lIi1|丨ǀ", "一ー―—─", "口ロ□"];

    private const string LookAlikeSymbols = "○〇◯|丨ǀ―—─□";

    // Full-width ASCII as its half-width self, and nothing else: a fuller normalisation would turn
    // ℃ into °C and Ⅲ into III, symbols and numbers into letters.
    private static string FoldWidth(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text)
            folded.Append(c is >= '！' and <= '～' ? (char)(c - 0xFEE0) : c == '　' ? ' ' : c);
        return folded.ToString();
    }
}
