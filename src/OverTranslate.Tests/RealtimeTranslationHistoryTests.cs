using OverTranslate.Services.Providers;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The session history a model is shown: only added to at the end, cut in half when full or too
/// long, and never holding a line that was not translated.
/// </summary>
public class RealtimeTranslationHistoryTests
{
    private static TranslationPair[] Round(int number) => [new($"line {number}", $"譯文 {number}")];

    [Fact]
    public void RoundsAreKeptOldestFirst()
    {
        var history = new RealtimeTranslationHistory();

        history.Add(Round(1));
        history.Add([new("a", "甲"), new("b", "乙")]);

        Assert.Equal(["line 1", "a", "b"], history.Lines.Select(line => line.Source));
        Assert.Equal(2, history.Rounds);
    }

    [Fact]
    public void ARoundWithNothingTranslatedIsNotARound()
    {
        var history = new RealtimeTranslationHistory();

        history.Add([]);

        Assert.Equal(0, history.Rounds);
    }

    /// <summary>
    /// One past the limit keeps the newest half, so the rounds after it start from the same prefix
    /// until the next cut.
    /// </summary>
    [Fact]
    public void PastTheLimitTheNewestHalfIsKept()
    {
        var history = new RealtimeTranslationHistory();

        for (var i = 1; i <= RealtimeTranslationHistory.MaxRounds; i++) history.Add(Round(i));
        Assert.Equal(RealtimeTranslationHistory.MaxRounds, history.Rounds);

        history.Add(Round(9));

        Assert.Equal(["line 6", "line 7", "line 8", "line 9"], history.Lines.Select(line => line.Source));

        history.Add(Round(10));
        Assert.Equal(["line 6", "line 7", "line 8", "line 9", "line 10"], history.Lines.Select(line => line.Source));
    }

    [Fact]
    public void TooLongIsCutInHalfUntilItFits_ButTheNewestRoundStays()
    {
        var history = new RealtimeTranslationHistory();
        var longLine = new string('字', RealtimeTranslationHistory.TokenLimit / 2 + 1);

        history.Add([new(longLine, "x")]);
        history.Add([new(longLine, "y")]);
        history.Add([new(longLine, "z")]);

        Assert.Equal(["z"], history.Lines.Select(line => line.Translation));

        var history2 = new RealtimeTranslationHistory();
        var tooLong = new string('字', RealtimeTranslationHistory.TokenLimit * 2);
        history2.Add([new(tooLong, "only")]);
        Assert.Equal(1, history2.Rounds);
    }

    [Fact]
    public void ARoundFromBeforeAClearIsRefused()
    {
        var history = new RealtimeTranslationHistory();
        history.Add(Round(1));

        history.Clear();
        history.Add(Round(2), stillCurrent: () => false);

        Assert.Empty(history.Lines);
    }

    /// <summary>
    /// A chat: every line still up was translated in an earlier round, so the screen beside the new
    /// lines is already all in the history, and none of it goes again as the screen.
    /// </summary>
    [Fact]
    public void TheScreenLeavesOutWhatTheHistoryHas()
    {
        var history = new RealtimeTranslationHistory();
        history.Add([new("一行目", "第一行"), new("二行目", "第二行")]);
        history.Add([new("三行目", "第三行")]);

        Assert.Empty(RealtimeTranslationHistory.Beside(
            [new("一行目", "第一行"), new("二行目", "第二行"), new("三行目", "第三行")], history.Lines));
        Assert.Equal(
            [new TranslationPair("看板", "招牌")],
            RealtimeTranslationHistory.Beside([new("二行目", "第二行"), new("看板", "招牌")], history.Lines));
    }

    [Theory]
    [InlineData("你好", "", 2)]
    [InlineData("こんにちは", "", 5)]
    [InlineData("안녕", "", 2)]
    [InlineData("abcdefgh", "", 2)]
    [InlineData("hello", "你好", 3)]
    public void TokensAreEstimatedByScript(string source, string translation, int expected)
    {
        Assert.Equal(expected, RealtimeTranslationHistory.EstimateTokens([new(source, translation)]));
    }
}
