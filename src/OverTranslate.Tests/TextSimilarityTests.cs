using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// This decides whether a re-recognition of the same region gets translated and repainted, so the
/// two ways it can be wrong are both visible to the user: too strict and an unchanged line flickers
/// while it is retranslated, too loose and a line that really changed never updates.
/// </summary>
public class TextSimilarityTests
{
    private const string Line =
        "The old lighthouse had not been lit for thirty years, and nobody in the village missed it.";

    [Fact]
    public void IdenticalTextIsTheSame()
    {
        Assert.True(TextSimilarity.IsSameContent(Line, Line));
    }

    [Fact]
    public void SpacingDifferencesAreTheSame()
    {
        // Where recognition wobbles most, and what carries the least meaning.
        Assert.True(TextSimilarity.IsSameContent("hello   world  again", "hello world again"));
        Assert.True(TextSimilarity.IsSameContent(" leading and trailing ", "leading and trailing"));
    }

    [Fact]
    public void AFewCharactersOfNoiseInALongLineIsTheSame()
    {
        // The measured case: the same unchanged subtitle read as 222, 227, 230 characters.
        var noisy = Line.Replace("thirty", "thlrty").Replace("village", "vi1lage");

        Assert.True(TextSimilarity.IsSameContent(Line, noisy));
    }

    [Fact]
    public void DifferentWordsAreNotTheSame()
    {
        const string other =
            "The harbour master waved the last boat in and went home to a cold supper by the window.";

        Assert.False(TextSimilarity.IsSameContent(Line, other));
    }

    [Fact]
    public void ARealChangeToOneWordOfAShortLineIsNotTheSame()
    {
        // Short text gets no tolerance at all: a couple of characters here is the whole meaning.
        Assert.False(TextSimilarity.IsSameContent("HP 100", "HP 190"));
        Assert.False(TextSimilarity.IsSameContent("Yes", "No"));
        Assert.False(TextSimilarity.IsSameContent("Level 7", "Level 8"));
    }

    [Fact]
    public void ALineGrowingOrLosingAWholeSentenceIsNotTheSame()
    {
        Assert.False(TextSimilarity.IsSameContent(Line, Line + " But the keeper still walked up every evening."));
    }

    [Fact]
    public void EmptyIsOnlyTheSameAsEmpty()
    {
        Assert.True(TextSimilarity.IsSameContent("", ""));
        Assert.True(TextSimilarity.IsSameContent("", "   "));
        Assert.False(TextSimilarity.IsSameContent("", Line));
        Assert.False(TextSimilarity.IsSameContent(Line, ""));
    }

    [Fact]
    public void ToleranceIsProportionalRatherThanFixed()
    {
        // Same absolute number of altered characters, different line lengths: noise in a long line,
        // a rewrite of a short one.
        var shortLine = "twelve chars";                     // exactly at the tolerance threshold
        var shortEdited = "twelve XXXXs";

        Assert.False(TextSimilarity.IsSameContent(shortLine, shortEdited));
        Assert.True(TextSimilarity.IsSameContent(Line, Line.Replace("nobody", "nabody")));
    }

    [Fact]
    public void CjkTextIsMeasuredTheSameWay()
    {
        const string sentence = "燈塔已經三十年沒有點亮了，村子裡沒有人想念它。";

        Assert.True(TextSimilarity.IsSameContent(sentence, sentence.Replace("想念", "想唸")));
        Assert.False(TextSimilarity.IsSameContent(sentence, "港務長揮手送走最後一艘船，回家吃了一頓冷掉的晚餐。"));
    }

    // ── Two readings of one sentence, or two sentences? ──────────────────────────────────────────

    [Theory]
    // Consecutive reads of one unchanged subtitle, taken from a live session's log. Each pair is
    // past what IsSameContent forgives and nowhere near a change of line.
    [InlineData("Where haveyongone!", "Where have you gone?!")]
    [InlineData("Where have you gonez", "Where have you gonez! E")]
    [InlineData("Oh,no! None ot the others Can tind a Way to", "Oh no! None ot the others can find a way to")]
    public void TwoReadingsOfOneSentenceAreRecognisedAsSuch(string first, string second)
    {
        Assert.True(TextSimilarity.IsSameSentence(first, second));
        Assert.True(TextSimilarity.IsSameSentence(second, first));
    }

    [Theory]
    // Pairs from a 693-translation sample, all of them one line read twice with its fragments in a
    // different order or half its letters confused. Every one sits between 20% and 30% — the stretch
    // the bound was raised across once there was enough data to read them all by hand.
    [InlineData("rather dispiited Minato seem -san", "rather dispirited Minato-san. seem")]
    [InlineData("heard a bunch of experts are there to study it.", "Mheard a bunchc are there to study it.")]
    [InlineData("Look at this!tssparkling rellyriht!", "Look at this It's sparking realy bright!")]
    public void AMangledRereadingIsStillTheSameSentence(string first, string second)
    {
        Assert.True(TextSimilarity.IsSameSentence(first, second));
    }

    [Fact]
    public void AReadingThatFoundSomethingTheOtherMissedIsNewText()
    {
        // Where the bound stops, and why it stops there. This region holds a player's furniture as
        // well as its subtitles; the second reading caught a subtitle the first did not. Calling
        // these one line would hold the old translation on screen and lose the new subtitle.
        Assert.False(TextSimilarity.IsSameSentence(
            "Live House \"CiRcLE ライブハウス「さーく ｐｉｃｏ２-01",
            "Live House \"CiRcLE This is nice!"));
    }

    [Theory]
    // Real consecutive subtitles from the same session. Judging any of these "the same sentence"
    // would leave the new line untranslated on screen for as long as it lasted.
    [InlineData("It's so relaxing.", "It'd be nice if we could all go for a soak one day.")]
    [InlineData("I wish CiRCLE could turn into a hot spring resort.", "No that's not happening.")]
    [InlineData("That was pretty good.", "Nice session, girls.")]
    [InlineData("You girls just keep getting better-", "Ugh was the floor always uneven here?")]
    public void DifferentSentencesAreNotTwoReadingsOfOne(string first, string second)
    {
        Assert.False(TextSimilarity.IsSameSentence(first, second));
    }

    [Fact]
    public void ShortTextIsNeverTreatedAsARereading()
    {
        // "What?!" against "EW What?!" is 33% apart and genuinely the same line, but at six
        // characters one stray glyph is a third of it — the same reason IsSameContent withholds its
        // tolerance here. Letting these through would make "Yes" and "No" one sentence.
        Assert.False(TextSimilarity.IsSameSentence("What?!", "EW What?!"));
        Assert.False(TextSimilarity.IsSameSentence("HP 100", "HP 190"));
    }

    [Fact]
    public void AnythingIdenticalEnoughToSkipIsAlsoTheSameSentence()
    {
        // IsSameSentence is the wider of the two judgements, so it must never disagree with the
        // narrower one — a pass may check them in either order without changing what happens.
        Assert.True(TextSimilarity.IsSameSentence(Line, Line));
        Assert.True(TextSimilarity.IsSameSentence(Line, Line.Replace("lighthouse", "Iighthouse")));
    }
}
