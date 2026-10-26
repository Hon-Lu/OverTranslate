using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OverTranslate.Translation.OpenAi;

/// <summary>
/// Whether a segment that came back under the right id is actually a translation.
/// </summary>
/// <remarks>
/// <para>An answer can have every id right and still not translate: measured, one model copied
/// a whole page of Japanese through, another answered in Simplified Chinese, a third copied the
/// English. Asked again alone, most of those segments do get translated — so a segment that fails
/// here is sent again on its own, once.</para>
///
/// <para>Failing is: the source copied through unchanged, two or more kana or Hangul left in it, or
/// for Traditional Chinese two or more Simplified-only characters and more of them than
/// Traditional-only ones. What is deliberately not failed, because it legitimately stays as it is:
/// a source with no letters; Latin text with fewer than two lowercase words of three letters or
/// more — names, titles, acronyms, <c>AUTO ESKIP</c>; a copyright line; a name in katakana alone,
/// brackets and punctuation around it or not; Han alone copied with fewer than two Simplified-only
/// characters — a Japanese name, 凉泉桜花, has one; a title kept in corner brackets in the source's
/// words, inside a sentence that was translated; a source already in the target language; one
/// stray kana or Simplified character. The source given back with its brackets or punctuation put
/// right is still a copy. Measured on the good models, one segment in six hundred was
/// failed wrongly, and that costs one request.</para>
/// </remarks>
internal static class OpenAiContentCheck
{
    private static readonly Regex Kana = new(
        "[ぁ-ゖゝ-ゟァ-ヺヽ-ヿㇰ-ㇿｦ-ﾝ]",
        RegexOptions.CultureInvariant);

    private static readonly Regex Hangul = new("[가-힯ᄀ-ᇿ㄰-㆏]", RegexOptions.CultureInvariant);

    private static readonly Regex Han = new("[㐀-䶿一-鿿豈-﫿]", RegexOptions.CultureInvariant);

    private static readonly Regex KatakanaOnly = new(@"^[゠-ヿｦ-ﾟ・ー\s]+$", RegexOptions.CultureInvariant);

    /// <summary>A title in corner brackets: 「…」 or 『…』.</summary>
    private static readonly Regex Quoted = new("「[^「」]*」|『[^『』]*』", RegexOptions.CultureInvariant);

    private static readonly Regex LowercaseWord = new("(?<![A-Za-z])[a-z][a-z']{2,}", RegexOptions.CultureInvariant);

    /// <summary>A chat line's channel and name — <c>[TEAM] Taz:</c> — which are not what needs translating.</summary>
    private static readonly Regex ChatPrefix = new(@"^\S*\[[^\]]*\]\s*[^:：\s]*[:：]?\s*", RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="translation"/> passes for a translation of <paramref name="source"/>.</summary>
    /// <param name="targetTag">The target language as a BCP 47 tag: <c>zh-Hant</c>, <c>ja</c>, <c>en</c>.</param>
    public static bool Passes(string source, string translation, string targetTag) =>
        Failure(source, translation, targetTag) is null;

    /// <summary>Why <paramref name="translation"/> fails, or null when it passes.</summary>
    internal static string? Failure(string source, string translation, string targetTag)
    {
        var japanese = targetTag.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        var korean = targetTag.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
        var traditional = targetTag.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase);

        var s = OpenAiPrompt.OneLine(source);
        var t = OpenAiPrompt.OneLine(translation);

        // Copied is copied with the brackets or punctuation put right too: 「アイライク ユアライク given
        // back closed, a line given back in quotation marks.
        if (s.Length > 0 && (s == t || Letters(s) == Letters(t)))
        {
            if (Copied(s, targetTag, japanese, korean, traditional)) return "copied";
            if (IsKatakanaOnly(s)) return null;
        }

        if (!japanese && Kana.Count(WithoutKeptQuotes(t, s)) >= 2) return "kana";
        if (!korean && Hangul.Count(t) >= 2) return "hangul";

        if (traditional)
        {
            var (simplified, traditionalOnly) = ChineseScripts.Count(t);
            if (simplified >= 2 && simplified > traditionalOnly) return "simplified";
        }

        return null;
    }

    /// <summary>Whether a source that came back unchanged should have been translated.</summary>
    private static bool Copied(string source, string targetTag, bool japanese, bool korean, bool traditional)
    {
        if (IsKatakanaOnly(source)) return false;
        if (Kana.IsMatch(source)) return !japanese;
        if (Hangul.IsMatch(source)) return !korean;

        if (source.Contains('©') || source.Contains("rights reserved", StringComparison.OrdinalIgnoreCase))
            return false;

        if (LowercaseWord.Count(ChatPrefix.Replace(source, "", 1)) >= 2)
            return !targetTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        // Han alone, copied: wrong only when what was copied is Simplified and Traditional was asked
        // for. Japanese written in kanji alone is left as it is — 通知 is 通知 in both — and so is a
        // Japanese name, which shares one character with Simplified at most: 凉泉桜花, 佐々木学子.
        // Two, as for a translation.
        if (traditional && Han.IsMatch(source))
        {
            var (simplified, traditionalOnly) = ChineseScripts.Count(source);
            return simplified >= 2 && simplified >= traditionalOnly;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is katakana alone once brackets and punctuation are taken off:
    /// 「アイライク ユアライク is a title, as アイライク ユアライク is.
    /// </summary>
    private static bool IsKatakanaOnly(string text)
    {
        var letters = Letters(text);
        return letters.Trim().Length > 0 && KatakanaOnly.IsMatch(letters);
    }

    /// <summary><paramref name="text"/> less its brackets and punctuation, the katakana marks kept.</summary>
    private static string Letters(string text) =>
        string.Concat(text.Where(c => !char.IsPunctuation(c) && !char.IsSymbol(c) || c is '・' or 'ー'));

    /// <summary>
    /// <paramref name="translation"/> less every 「…」 or 『…』 whose words <paramref name="source"/> has
    /// as they are: a title kept as written, in a sentence that was translated, is not kana left
    /// behind. A translation that is nothing but the source in brackets is a copy, and caught as one
    /// before this.
    /// </summary>
    private static string WithoutKeptQuotes(string translation, string source) =>
        Quoted.Replace(translation, quote =>
            quote.Length > 2 && source.Contains(quote.Value[1..^1], StringComparison.Ordinal) ? "" : quote.Value);

    /// <summary>
    /// Counts the characters only Simplified Chinese writes, and those only Traditional writes.
    /// </summary>
    /// <remarks>
    /// <para>Windows' own conversion tables, through <c>LCMapStringEx</c>: a character the
    /// Traditional conversion changes is Simplified-only, one the Simplified conversion changes is
    /// Traditional-only. Measured against the conversion the checks were tuned with, the two agree
    /// on all but rare characters, and the common ones where they disagree are listed in
    /// <see cref="Either"/>.</para>
    ///
    /// <para>Off Windows there is nothing to ask, and nothing is counted.</para>
    /// </remarks>
    internal static class ChineseScripts
    {
        private const uint MapSimplified = 0x02000000;
        private const uint MapTraditional = 0x04000000;

        /// <summary>
        /// Characters that are ordinary in Traditional text too, though a conversion would change
        /// them: 公里, 傢伙, 台灣, 裏, 祕. Counted as neither.
        /// </summary>
        private static readonly HashSet<char> Either =
        [
            .. "里伙台干后余冲准么吃面只才了系卷布志制周丑范松游凶症胡斗佣借困谷姜几尸云舍克采征致",
            .. "喫樑癡祕纔脣苧裏覈扞垧墒嵛擀杆狸珏琅畲痴眯睃硷艹芸苄苟",
        ];

        public static (int Simplified, int Traditional) Count(string text)
        {
            if (!OperatingSystem.IsWindows() || !Han.IsMatch(text)) return (0, 0);

            var toTraditional = Map(text, MapTraditional);
            var toSimplified = Map(text, MapSimplified);
            int simplified = 0, traditional = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (!Han.IsMatch(c.ToString()) || Either.Contains(c)) continue;
                if (Changes(toTraditional, text, i)) simplified++;
                else if (Changes(toSimplified, text, i)) traditional++;
            }

            return (simplified, traditional);
        }

        /// <summary>Whether the conversion changed the character at <paramref name="i"/>.</summary>
        /// <remarks>Both conversions map one character to one, so the positions line up.</remarks>
        private static bool Changes(string? mapped, string text, int i) =>
            mapped is not null && mapped.Length == text.Length && mapped[i] != text[i];

        private static string? Map(string text, uint flags)
        {
            var buffer = new char[text.Length * 2 + 8];
            var length = LCMapStringEx("zh-CN", flags, text, text.Length, buffer, buffer.Length, 0, 0, 0);
            return length > 0 ? new string(buffer, 0, length) : null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int LCMapStringEx(
            string localeName, uint flags, string source, int sourceLength,
            [Out] char[] destination, int destinationLength,
            nint versionInformation, nint reserved, nint sortHandle);
    }
}
