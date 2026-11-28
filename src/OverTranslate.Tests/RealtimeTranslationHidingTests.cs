using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

// 隱藏譯文: a switch on the sitting, independent of 暫停, that framing, the end of the session and a
// capture standing on it all have something to say about. The controller applies it to windows; the
// rule itself is here.
public class RealtimeTranslationHidingTests
{
    [Fact]
    public void ASessionStartsWithTheWordsShowing() =>
        Assert.False(new RealtimeTranslationHiding().Hidden);

    [Fact]
    public void EachPressWhileTranslatingFlipsIt()
    {
        var hiding = new RealtimeTranslationHiding();

        Assert.True(hiding.Toggle(translating: true, standingDownForCapture: false));
        Assert.True(hiding.Hidden);

        Assert.True(hiding.Toggle(translating: true, standingDownForCapture: false));
        Assert.False(hiding.Hidden);
    }

    [Fact]
    public void WhileFramingThereIsNothingToHide()
    {
        var hiding = new RealtimeTranslationHiding();

        Assert.False(hiding.Toggle(translating: false, standingDownForCapture: false));
        Assert.False(hiding.Hidden);
    }

    [Fact]
    public void AShortcutPressedUnderACaptureMovesNothing()
    {
        var hiding = new RealtimeTranslationHiding();
        hiding.Toggle(translating: true, standingDownForCapture: false);

        Assert.False(hiding.Toggle(translating: true, standingDownForCapture: true));
        // The capture puts the session back as it found it: hidden.
        Assert.True(hiding.Hidden);
    }

    [Fact]
    public void FramingAgainOrEndingTheSessionShowsTheWordsAgain()
    {
        var hiding = new RealtimeTranslationHiding();
        hiding.Toggle(translating: true, standingDownForCapture: false);

        hiding.Reset();

        Assert.False(hiding.Hidden);
    }

    // Hiding names nothing on the capsule — the held-down eye says it — so only the pause and the
    // model load are left to choose between.
    [Theory]
    [InlineData(false, false, RealtimeRunStatus.Running)]
    [InlineData(false, true, RealtimeRunStatus.LoadingModels)]
    [InlineData(true, false, RealtimeRunStatus.Paused)]
    [InlineData(true, true, RealtimeRunStatus.Paused)]
    public void TheCapsuleNamesOneStatePausedFirst(bool paused, bool loading, RealtimeRunStatus expected) =>
        Assert.Equal(expected, RealtimeRunStatusRule.For(paused, loading));
}
