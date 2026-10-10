using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A region whose picture has gone away clears what it last drew — once, and not on the first miss,
/// which is only ever a capture being rebuilt for a resize.
/// </summary>
public class RealtimeFrameLossTests
{
    [Fact]
    public void A_short_gap_does_not_clear()
    {
        var loss = new RealtimeFrameLoss();

        for (var i = 1; i < RealtimeFrameLoss.ClearAfterPolls; i++)
            Assert.False(loss.Missed());
    }

    [Fact]
    public void Clears_once_when_the_picture_stays_away()
    {
        var loss = new RealtimeFrameLoss();

        var clears = Enumerable.Range(0, RealtimeFrameLoss.ClearAfterPolls * 5).Count(_ => loss.Missed());

        Assert.Equal(1, clears);
    }

    // The picture coming back starts the count again, so a second absence clears what the first
    // return drew.
    [Fact]
    public void A_picture_in_between_starts_the_count_again()
    {
        var loss = new RealtimeFrameLoss();
        for (var i = 1; i < RealtimeFrameLoss.ClearAfterPolls; i++) loss.Missed();

        loss.Seen();

        for (var i = 1; i < RealtimeFrameLoss.ClearAfterPolls; i++)
            Assert.False(loss.Missed());
        Assert.True(loss.Missed());
    }
}
