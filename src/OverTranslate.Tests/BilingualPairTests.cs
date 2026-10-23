using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 雙語互譯's rules about the pair itself, shared by 取詞翻譯 and 替換翻譯 (#322).
/// </summary>
public class BilingualPairTests
{
    private const string Hant = "ZH-HANT";
    private const string Hans = "ZH-HANS";
    private const string English = "EN-US";
    private const string Japanese = "JA";

    private static readonly BilingualPair ChineseEnglish = new(Hant, English);

    [Fact]
    public void TextInTheFirstLanguageGoesToTheSecond()
    {
        Assert.Equal(English, ChineseEnglish.TargetFor(Hant));
    }

    [Theory]
    [InlineData(English)]
    [InlineData(Japanese)]   // in neither language
    [InlineData(null)]       // nothing could be told
    public void EverythingElseGoesToTheFirst(string? source)
    {
        Assert.Equal(Hant, ChineseEnglish.TargetFor(source));
    }

    [Fact]
    public void PickingTheOtherSlotsLanguageTurnsThePairAround()
    {
        Assert.Equal(new BilingualPair(English, Hant), ChineseEnglish.Pick(English, firstSlot: true));
        Assert.Equal(new BilingualPair(English, Hant), ChineseEnglish.Pick(Hant, firstSlot: false));
    }

    [Fact]
    public void PickingANewLanguageReplacesOnlyThatSlot()
    {
        Assert.Equal(new BilingualPair(Japanese, English), ChineseEnglish.Pick(Japanese, firstSlot: true));
        Assert.Equal(new BilingualPair(Hant, Japanese), ChineseEnglish.Pick(Japanese, firstSlot: false));
    }

    [Fact]
    public void AHandEditedPairOfOneLanguageMovesTheSecondSlot()
    {
        Assert.Equal(new BilingualPair(Japanese, English), BilingualPair.Load(Japanese, Japanese));
        Assert.Equal(new BilingualPair(English, Hant), BilingualPair.Load(English, English));
    }

    [Fact]
    public void AnEngineThatAgreesWithTheGuessChangesNothing()
    {
        // Sent to English because the characters said Chinese; the engine says Chinese too.
        Assert.Null(ChineseEnglish.Correct("ZH-TW", English, out var source));
        Assert.Equal(Hant, source);
    }

    [Fact]
    public void AnEngineThatDisagreesSendsItTheOtherWay()
    {
        // Sent to English, but the engine read the text as English: it belongs in Chinese.
        Assert.Equal(Hant, ChineseEnglish.Correct("EN", English, out var source));
        Assert.Equal(English, source);
    }

    [Fact]
    public void AnEngineReportingNothingLeavesTheGuess()
    {
        Assert.Null(ChineseEnglish.Correct(null, English, out var source));
        Assert.Null(source);
    }

    [Fact]
    public void ChineseFromTheEngineCannotOverruleTheTraditionalSimplifiedSplit()
    {
        var pair = new BilingualPair(Hant, Hans);
        Assert.Null(pair.Correct("ZH", Hans, out _));
    }

    // #339: Google reads 「вода」 as Macedonian. Under Russian ⇄ English that is a third language,
    // so Correct sends it into Russian — and Russian into Russian is the word itself.
    [Fact]
    public void AMisreadWordIsSentIntoTheLanguageItIsAlreadyIn()
    {
        var pair = new BilingualPair("RU", English);
        Assert.Equal("RU", pair.Correct("mk", English, out _));
    }

    [Theory]
    [InlineData("вода", "вода")]
    [InlineData("вода", "Вода")]         // capitalised on the way back
    [InlineData("вода", "вода.")]        // or given a full stop
    [InlineData("вода", " вода ")]
    [InlineData("Hund", "ＨＵＮＤ")]      // full width
    [InlineData("élève", "eleve")]       // accents dropped
    [InlineData("вода", "")]             // nothing back at all
    [InlineData("вода", null)]
    public void AnAnswerThatIsTheTextItselfIsNotWorthKeeping(string text, string? retranslated)
    {
        Assert.False(BilingualPair.IsWorthKeeping(text, retranslated));
    }

    // German under English ⇄ Russian is a genuine third language: sent into English it comes back
    // translated, and that is what the settings promise for anything outside the pair.
    [Theory]
    [InlineData("Hund", "Dog")]
    [InlineData("вода", "water")]
    [InlineData("水", "water")]
    public void ARealTranslationIsWorthKeeping(string text, string retranslated)
    {
        Assert.True(BilingualPair.IsWorthKeeping(text, retranslated));
    }

    [Theory]
    [InlineData("RU", true)]
    [InlineData("ru", true)]
    [InlineData("EN", true)]             // source code for the EN-US end
    [InlineData("mk", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IncludesMatchesEitherEndBySourceOrTargetCode(string? code, bool expected)
    {
        Assert.Equal(expected, new BilingualPair("RU", English).Includes(code));
    }
}
