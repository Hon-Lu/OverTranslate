using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 雙語互譯's half of the decision: which of the user's two languages the text is written in.
/// </summary>
/// <remarks>
/// The codes here are the ones the pair is actually stored in — target codes, so EN-US rather than
/// EN — because that is what the window hands this.
/// </remarks>
public class BilingualDirectionTests
{
    private const string Hant = "ZH-HANT";
    private const string Hans = "ZH-HANS";
    private const string English = "EN-US";
    private const string Japanese = "JA";
    private const string Korean = "KO";
    private const string French = "FR";

    // ═══════════ The pairs this application is actually set to ═══════════

    [Theory]
    [InlineData("翻譯")]                 // Han only
    [InlineData("這")]                   // one character, which is what 取詞翻譯 is usually given
    [InlineData("這個 API 要怎麼用")]     // Latin inside Chinese: the Han still outnumbers it
    public void ChineseGoesToEnglishUnderTheChineseEnglishPair(string text)
    {
        Assert.Equal(Hant, BilingualDirection.ResolveSource(text, Hant, English));
    }

    [Theory]
    [InlineData("Good morning")]
    [InlineData("a")]
    [InlineData("Use the 注音 input")]   // Chinese inside English: the Latin outnumbers it
    public void EnglishGoesToChineseUnderTheChineseEnglishPair(string text)
    {
        Assert.Equal(English, BilingualDirection.ResolveSource(text, Hant, English));
    }

    /// <remarks>
    /// The case the whole scoring shape exists for. A selected Japanese term is very often kanji and
    /// nothing else, and a rule that asked 「is this Han or kana?」 would call 「大丈夫」 Chinese. The
    /// question is which of the two, and 英文 cannot write a kanji at all.
    /// </remarks>
    [Theory]
    [InlineData("大丈夫")]
    [InlineData("必要")]
    [InlineData("地図")]
    public void KanjiOnlyJapaneseIsStillJapaneseWhenTheOtherHalfIsEnglish(string text)
    {
        Assert.Equal(Japanese, BilingualDirection.ResolveSource(text, Japanese, English));
    }

    [Theory]
    [InlineData("こんにちは")]
    [InlineData("ドラゴン")]
    [InlineData("Wi-Fiが繋がらない")]     // kana outscores the Latin the sentence borrows
    public void KanaIsJapaneseUnderTheJapaneseEnglishPair(string text)
    {
        Assert.Equal(Japanese, BilingualDirection.ResolveSource(text, Japanese, English));
    }

    /// <remarks>
    /// Latin scores for 英文 and for nobody else, so a borrowed English word inside a Japanese page
    /// goes to Japanese rather than tying.
    /// </remarks>
    [Fact]
    public void ABorrowedEnglishWordIsEnglishEvenOnTheJapanesePair()
    {
        Assert.Equal(English, BilingualDirection.ResolveSource("iPhone", Japanese, English));
    }

    [Fact]
    public void HangulIsKoreanAndHanIsNotWhenTheyArePairedTogether()
    {
        Assert.Equal(Korean, BilingualDirection.ResolveSource("안녕하세요", Hant, Korean));
        Assert.Equal(Hant, BilingualDirection.ResolveSource("早安", Hant, Korean));
    }

    // ═══════════ Mixed scripts ═══════════

    /// <remarks>
    /// A Japanese sentence is written in two scripts at once and both of them are Japanese, which is
    /// why each language is scored rather than the text being assigned a single script. Here 中文
    /// can only claim the kanji while 日文 claims the whole sentence.
    /// </remarks>
    [Fact]
    public void JapaneseBeatsChineseOnAMixedSentenceBecauseBothItsScriptsCount()
    {
        Assert.Equal(
            Japanese,
            BilingualDirection.ResolveSource("東京都に住んでいます", Hant, Japanese));
    }

    /// <remarks>
    /// The other way round, and the reason kana is not treated as proof on its own: a Chinese line
    /// quoting one Japanese name is still a Chinese line, and the reader wants it in English.
    /// </remarks>
    [Fact]
    public void AJapaneseNameQuotedInsideChineseDoesNotMakeTheLineJapanese()
    {
        Assert.Equal(
            Hant, BilingualDirection.ResolveSource("這個角色叫做ドラゴン", Hant, English));
    }

    /// <remarks>
    /// U+30FB sits in the katakana block but separates foreign names in Chinese too. Were it to
    /// score, this line of kanji would tip to 日文 on the strength of one separator; undecided is
    /// the answer kanji-only text is supposed to get on this pair.
    /// </remarks>
    [Fact]
    public void TheKatakanaMiddleDotIsPunctuationRatherThanEvidenceOfJapanese()
    {
        Assert.Null(BilingualDirection.ResolveSource("瑪麗・居禮", Hant, Japanese));
    }

    /// <remarks>
    /// The mark next to it in the same block does score: nothing but Japanese writes U+30FC.
    /// </remarks>
    [Fact]
    public void TheProlongedSoundMarkStillCounts()
    {
        Assert.Equal(Japanese, BilingualDirection.ResolveSource("サーバー", Hant, Japanese));
    }

    // ═══════════ Text in neither language ═══════════

    /// <remarks>
    /// Naming the third language is what puts 「日文 → 繁體中文」 in the header instead of a bare
    /// 「自動偵測」. It only works where the script belongs to one language on its own.
    /// </remarks>
    [Fact]
    public void AThirdLanguageIsNamedWhenItsScriptBelongsToOnlyOneLanguage()
    {
        Assert.Equal(Japanese, BilingualDirection.ResolveSource("こんにちは", Hant, English));
        Assert.Equal(Korean, BilingualDirection.ResolveSource("안녕하세요", Hant, English));
    }

    /// <remarks>
    /// French under 中文／英文 is read as English, and that is the right answer to the question being
    /// asked: the target is 繁體中文 either way. Only the label is approximate, and the engine's own
    /// reported language corrects it.
    /// </remarks>
    [Fact]
    public void AThirdLatinLanguageScoresAsTheLatinHalfOfThePair()
    {
        Assert.Equal(English, BilingualDirection.ResolveSource("Bonjour", Hant, English));
    }

    // ═══════════ Nothing to go on ═══════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("3.14")]
    [InlineData("23:29")]
    [InlineData("!?…")]
    public void TextWithNoScriptOfItsOwnIsLeftUndecided(string text)
    {
        Assert.Null(BilingualDirection.ResolveSource(text, Hant, English));
    }

    /// <remarks>
    /// Digits carry no script, but the letters beside them do — 「Lv」 is two Latin letters and that
    /// is enough to answer a pair whose other half is written in Han.
    /// </remarks>
    [Fact]
    public void LettersCountEvenWhenDigitsOutnumberThem()
    {
        Assert.Equal(English, BilingualDirection.ResolveSource("Lv.100", Hant, English));
    }

    /// <remarks>
    /// Two Latin languages have nothing in the characters to separate them. Undecided is the honest
    /// answer; the window translates into the first language and lets the engine settle it.
    /// </remarks>
    [Fact]
    public void TwoLatinLanguagesCannotBeSeparatedFromTheCharacters()
    {
        Assert.Null(BilingualDirection.ResolveSource("Bonjour", English, French));
        Assert.Null(BilingualDirection.ResolveSource("Good morning", English, French));
    }

    /// <remarks>
    /// Kanji with no kana under 中文／日文 is the one pair where the kanji-only case really is
    /// undecidable — 日文 has no second script to score with here.
    /// </remarks>
    [Fact]
    public void KanjiOnlyTextIsUndecidedWhenBothLanguagesWriteInHan()
    {
        Assert.Null(BilingualDirection.ResolveSource("日本語能力試験", Hant, Japanese));
    }

    // ═══════════ 繁體／簡體, through OpenCC ═══════════

    [Fact]
    public void TheChineseVariantPairIsSeparatedByItsOwnCharacters()
    {
        Assert.Equal(Hant, BilingualDirection.ResolveSource("這個時間點", Hant, Hans));
        Assert.Equal(Hans, BilingualDirection.ResolveSource("这个时间点", Hant, Hans));
    }

    [Fact]
    public void TheChineseVariantPairIsSeparatedWhicheverWayRoundItIsSet()
    {
        Assert.Equal(Hant, BilingualDirection.ResolveSource("這個時間點", Hans, Hant));
        Assert.Equal(Hans, BilingualDirection.ResolveSource("这个时间点", Hans, Hant));
    }

    /// <remarks>
    /// Characters the two variants share carry no answer, and inventing one would send half of all
    /// Chinese input the wrong way.
    /// </remarks>
    [Fact]
    public void CharactersCommonToBothChineseVariantsStayUndecided()
    {
        Assert.Null(BilingualDirection.ResolveSource("大人", Hant, Hans));
    }
}
