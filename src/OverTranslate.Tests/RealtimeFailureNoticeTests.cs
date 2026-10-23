using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The retried failure on the running capsule (#345): it covers the language pair until it clears,
/// unless clicked away — and clicked away, only for as long as it is the same failure.
/// </summary>
public class RealtimeFailureNoticeTests
{
    private const string Refused = "Translation failed: unsupported language pair";

    [Fact]
    public void AFailure_TakesThePairsPlace()
    {
        var notice = new RealtimeFailureNotice();

        Assert.True(notice.Set(Refused));
        Assert.True(notice.StandsInForPair);
    }

    [Fact]
    public void ClickedAway_ThePairComesBack_ButTheFailureIsStillKnown()
    {
        var notice = new RealtimeFailureNotice();
        notice.Set(Refused);

        Assert.True(notice.Dismiss());
        Assert.False(notice.StandsInForPair);
        Assert.Equal(Refused, notice.Message);
    }

    // The session reports a failure once however many retries run into it; each retry that fails
    // the same way must not bring back what the user has just clicked away.
    [Fact]
    public void TheSameFailureAgain_StaysAway()
    {
        var notice = new RealtimeFailureNotice();
        notice.Set(Refused);
        notice.Dismiss();

        Assert.False(notice.Set(Refused));
        Assert.False(notice.StandsInForPair);
    }

    [Fact]
    public void ADifferentFailure_ShowsAgain()
    {
        var notice = new RealtimeFailureNotice();
        notice.Set(Refused);
        notice.Dismiss();

        Assert.True(notice.Set("Translation failed: quota exceeded"));
        Assert.True(notice.StandsInForPair);
    }

    // Changing the pair restarts the session, which clears its failures: if the new pair is refused
    // too, that is the answer to what the user just did, and it has to be seen.
    [Fact]
    public void TheSameFailure_AfterItHadCleared_ShowsAgain()
    {
        var notice = new RealtimeFailureNotice();
        notice.Set(Refused);
        notice.Dismiss();

        notice.Set(null);
        notice.Set(Refused);

        Assert.True(notice.StandsInForPair);
    }

    [Fact]
    public void NothingFailing_HasNothingToDismiss()
    {
        var notice = new RealtimeFailureNotice();

        Assert.False(notice.Dismiss());
        Assert.False(notice.IsDismissed);
        Assert.False(notice.Set(null));
    }
}
