using OverTranslate.Services;
using OverTranslate.Views.Shell;
using Xunit;

namespace OverTranslate.Tests;

// The card's second line and button: what each press and each check result turns them into.
public sealed class UpdateCardStateTests
{
    [Fact]
    public void Opens_Idle_WithTheCheckButton()
    {
        var card = new UpdateCardState();
        card.Open(updateAvailable: false);

        Assert.Equal(UpdateCardStatus.Idle, card.Status);
        Assert.Equal(UpdateCardAction.Check, card.Action);
        Assert.True(card.ActionEnabled);
    }

    [Fact]
    public void Opens_OnTheReleaseWhenOneIsKnown()
    {
        var card = new UpdateCardState();
        card.Open(updateAvailable: true);

        Assert.Equal(UpdateCardStatus.Found, card.Status);
        Assert.Equal(UpdateCardAction.UpdateNow, card.Action);
    }

    [Fact]
    public void Checking_DisablesTheButtonWithoutChangingIt()
    {
        var card = new UpdateCardState();
        card.Open(false);

        Assert.True(card.BeginCheck());

        Assert.Equal(UpdateCardStatus.Checking, card.Status);
        Assert.Equal(UpdateCardAction.Check, card.Action);
        Assert.False(card.ActionEnabled);
        Assert.False(card.BeginCheck());
    }

    [Theory]
    [InlineData(UpdateCheckStatus.UpToDate, UpdateCardStatus.UpToDate, UpdateCardAction.Check)]
    [InlineData(UpdateCheckStatus.Failed, UpdateCardStatus.Failed, UpdateCardAction.Retry)]
    [InlineData(UpdateCheckStatus.NotInstalled, UpdateCardStatus.Failed, UpdateCardAction.Retry)]
    public void ACheckWithNoRelease_EndsOnItsOutcome(
        UpdateCheckStatus result, UpdateCardStatus expected, UpdateCardAction action)
    {
        var card = new UpdateCardState();
        card.Open(false);
        card.BeginCheck();

        card.CompleteCheck(result, updateAvailable: false);

        Assert.Equal(expected, card.Status);
        Assert.Equal(action, card.Action);
        Assert.True(card.ActionEnabled);
    }

    [Theory]
    [InlineData(UpdateCheckStatus.Available)]
    // The periodic check found one while this check failed; the release still wins.
    [InlineData(UpdateCheckStatus.Failed)]
    public void ACheckThatEndsWithAReleaseKnown_EndsOnIt(UpdateCheckStatus result)
    {
        var card = new UpdateCardState();
        card.Open(false);
        card.BeginCheck();

        card.CompleteCheck(result, updateAvailable: true);

        Assert.Equal(UpdateCardStatus.Found, card.Status);
        Assert.Equal(UpdateCardAction.UpdateNow, card.Action);
    }

    [Fact]
    public void Retry_StartsAnotherCheck()
    {
        var card = new UpdateCardState();
        card.Open(false);
        card.BeginCheck();
        card.CompleteCheck(UpdateCheckStatus.Failed, false);

        Assert.True(card.BeginCheck());
        Assert.Equal(UpdateCardStatus.Checking, card.Status);
    }

    [Fact]
    public void Reopening_DropsTheLastOutcome()
    {
        var card = new UpdateCardState();
        card.Open(false);
        card.BeginCheck();
        card.CompleteCheck(UpdateCheckStatus.UpToDate, false);

        card.Open(false);

        Assert.Equal(UpdateCardStatus.Idle, card.Status);
    }

    [Fact]
    public void Reopening_WhileACheckRuns_StillShowsIt()
    {
        var card = new UpdateCardState();
        card.Open(false);
        card.BeginCheck();

        card.Open(false);

        Assert.Equal(UpdateCardStatus.Checking, card.Status);
    }

    [Fact]
    public void AReleaseFoundWhileOpen_TakesOverTheCard_UnlessACheckIsRunning()
    {
        var idle = new UpdateCardState();
        idle.Open(false);
        idle.ReleaseFound();
        Assert.Equal(UpdateCardStatus.Found, idle.Status);

        var checking = new UpdateCardState();
        checking.Open(false);
        checking.BeginCheck();
        checking.ReleaseFound();
        Assert.Equal(UpdateCardStatus.Checking, checking.Status);
    }

    [Fact]
    public void WithAReleaseKnown_ThereIsNoCheckToStart()
    {
        var card = new UpdateCardState();
        card.Open(true);

        Assert.False(card.BeginCheck());
        Assert.Equal(UpdateCardStatus.Found, card.Status);
    }

    [Theory]
    [InlineData("2026-11-26 14:20", "2026-11-26 23:59", "M/d", "14:20")]
    [InlineData("2026-11-26 09:05", "2026-11-26 09:06", "M/d", "09:05")]
    [InlineData("2026-11-25 23:40", "2026-11-26 00:10", "M/d", "11/25 23:40")]
    [InlineData("2026-11-03 08:00", "2026-11-26 08:00", "dd.MM", "03.11 08:00")]
    public void LastChecked_IsTheTimeToday_AndTheDateOtherwise(string when, string now, string pattern, string expected)
    {
        Assert.Equal(expected, UpdateCardState.FormatLastChecked(DateTime.Parse(when), DateTime.Parse(now), pattern));
    }
}
