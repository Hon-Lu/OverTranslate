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

    [Theory]
    [InlineData(false, false, false, RealtimeRunStatus.Running)]
    [InlineData(false, false, true, RealtimeRunStatus.LoadingModels)]
    [InlineData(false, true, false, RealtimeRunStatus.TranslationHidden)]
    // Hidden is the user's own choice and what they will look for when the words do not come back;
    // the pulsing dot still says the models are loading.
    [InlineData(false, true, true, RealtimeRunStatus.TranslationHidden)]
    [InlineData(true, false, false, RealtimeRunStatus.Paused)]
    // Paused and hidden at once: the pause is the one that has to be undone first.
    [InlineData(true, true, false, RealtimeRunStatus.Paused)]
    [InlineData(true, true, true, RealtimeRunStatus.Paused)]
    public void TheCapsuleNamesOneStatePausedFirst(
        bool paused, bool hidden, bool loading, RealtimeRunStatus expected) =>
        Assert.Equal(expected, RealtimeRunStatusRule.For(paused, hidden, loading));
}
