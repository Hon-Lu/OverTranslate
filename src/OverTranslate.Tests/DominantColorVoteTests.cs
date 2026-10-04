using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class DominantColorVoteTests
{
    [Fact]
    public void Dominant_IsNullWhenNothingWasAdded()
    {
        Assert.Null(new DominantColorVote().Dominant());
    }

    [Fact]
    public void Dominant_KeepsTheMajorityColourInsteadOfBlendingInTheMinority()
    {
        var vote = new DominantColorVote();
        for (int i = 0; i < 30; i++) vote.Add(250, 250, 250);
        for (int i = 0; i < 10; i++) vote.Add(230, 30, 30);

        var dominant = vote.Dominant();

        Assert.Equal(System.Windows.Media.Color.FromRgb(250, 250, 250), dominant);
    }

    [Fact]
    public void Dominant_AveragesNearbyShadesOfTheSameColour()
    {
        var vote = new DominantColorVote();
        for (int i = 0; i < 10; i++) vote.Add(240, 240, 240);
        for (int i = 0; i < 10; i++) vote.Add(250, 250, 250);
        for (int i = 0; i < 15; i++) vote.Add(230, 30, 30);

        var dominant = vote.Dominant();

        Assert.Equal(System.Windows.Media.Color.FromRgb(245, 245, 245), dominant);
    }
}
