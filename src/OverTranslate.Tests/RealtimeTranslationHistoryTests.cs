using OverTranslate.Services.Providers;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The session history a model is shown: only added to at the end, cut to the newest half when
/// full or too long, and never holding a line that was not translated.
/// </summary>
public class RealtimeTranslationHistoryTests
{
    private static TranslationPair[] Round(int number) => [new($"line {number}", $"譯文 {number}")];

    [Fact]
    public void LinesAreKeptOldestFirst()
    {
        var history = new RealtimeTranslationHistory();

        history.Add(Round(1));
        history.Add([new("a", "甲"), new("b", "乙")]);

        Assert.Equal(["line 1", "a", "b"], history.Lines.Select(line => line.Source));
    }

    [Fact]
    public void ARequestWithNothingTranslatedAddsNothing()
    {
        var history = new RealtimeTranslationHistory();

        history.Add([]);

        Assert.Empty(history.Lines);
    }

    /// <summary>
    /// Counted in pairs, not requests: one past sixteen keeps the newest eight, so the lines after
    /// it start from the same prefix until the next cut.
    /// </summary>
    [Fact]
    public void PastSixteenPairsTheNewestEightAreKept()
    {
        var history = new RealtimeTranslationHistory();

        for (var i = 1; i <= RealtimeTranslationHistory.MaxPairs; i++) history.Add(Round(i));
        Assert.Equal(16, history.Lines.Count);

        history.Add(Round(17));

        Assert.Equal(Enumerable.Range(10, 8).Select(i => $"line {i}"), history.Lines.Select(line => line.Source));

        history.Add(Round(18));
        Assert.Equal(Enumerable.Range(10, 9).Select(i => $"line {i}"), history.Lines.Select(line => line.Source));
    }

    /// <summary>One request of many lines counts as many pairs: a screen of twenty keeps its last eight.</summary>
    [Fact]
    public void ABigRequestIsCountedByItsPairs()
    {
        var history = new RealtimeTranslationHistory();

        history.Add([.. Enumerable.Range(1, 20).Select(i => new TranslationPair($"line {i}", $"譯文 {i}"))]);

        Assert.Equal(Enumerable.Range(13, 8).Select(i => $"line {i}"), history.Lines.Select(line => line.Source));
    }

    [Fact]
    public void TooLongIsCutInHalfUntilItFits_ButTheNewestLineStays()
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
        Assert.Single(history2.Lines);
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
