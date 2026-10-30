using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A line already on its way is waited for rather than sent again, until its pass is done with it
/// or the session moves to another generation.
/// </summary>
public class RealtimeInFlightTranslationsTests
{
    [Fact]
    public void TheFirstToNeedALineSendsIt_TheNextWaitsForIt()
    {
        var inFlight = new RealtimeInFlightTranslations();

        var (first, sendFirst) = inFlight.Claim("key", 1);
        var (second, sendSecond) = inFlight.Claim("key", 1);

        Assert.True(sendFirst);
        Assert.False(sendSecond);
        Assert.Same(first, second);
    }

    [Fact]
    public void AfterAFenceTheLineIsSentAfresh()
    {
        var inFlight = new RealtimeInFlightTranslations();
        var (old, _) = inFlight.Claim("key", 1);

        var (fresh, send) = inFlight.Claim("key", 2);

        Assert.True(send);
        Assert.NotSame(old, fresh);

        // The old pass finishing does not take the new line off the list.
        old.Complete();
        Assert.False(inFlight.Claim("key", 2).Send);
    }

    [Fact]
    public void ALineIsDoneOnceItsPassHasFinishedWithIt()
    {
        var inFlight = new RealtimeInFlightTranslations();
        var (line, _) = inFlight.Claim("key", 1);
        Assert.False(line.Done.IsCompleted);

        line.Complete();
        line.Complete();
        Assert.True(line.Done.IsCompleted);
    }

    [Fact]
    public void ADoneLineIsSentAgainByTheNextPassThatNeedsIt()
    {
        var inFlight = new RealtimeInFlightTranslations();
        var (line, _) = inFlight.Claim("key", 1);

        line.Complete();

        Assert.Equal(0, inFlight.Count);
        Assert.True(inFlight.Claim("key", 1).Send);
    }
}
