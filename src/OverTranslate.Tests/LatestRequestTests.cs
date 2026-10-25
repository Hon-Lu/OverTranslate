using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The guard the text windows put around each translation: a newer request cancels the one before
/// and makes its answer not count.
/// </summary>
public class LatestRequestTests
{
    [Fact]
    public void StartingANewRequestCancelsTheOneBeforeAndMakesItStale()
    {
        var request = new LatestRequest();
        var (first, firstToken) = request.Start();
        var (second, secondToken) = request.Start();

        Assert.True(firstToken.IsCancellationRequested);
        Assert.False(request.IsCurrent(first));
        Assert.False(secondToken.IsCancellationRequested);
        Assert.True(request.IsCurrent(second));
    }

    /// <summary>
    /// Giving up without starting another — the box emptied, the window closed — does both as well.
    /// </summary>
    [Fact]
    public void SupersedingCancelsAndMakesStaleWithoutStartingAnother()
    {
        var request = new LatestRequest();
        var (seq, token) = request.Start();

        request.Supersede();

        Assert.True(token.IsCancellationRequested);
        Assert.False(request.IsCurrent(seq));
    }

    /// <summary>
    /// Every cancelled request is a stale one, which is what lets callers check the number first
    /// and keep a cancellation off the error line.
    /// </summary>
    [Fact]
    public void ACancelledRequestIsNeverTheCurrentOne()
    {
        var request = new LatestRequest();
        var started = Enumerable.Range(0, 5).Select(_ => request.Start()).ToList();
        request.Supersede();
        started.Add(request.Start());

        foreach (var (seq, token) in started)
            Assert.True(token.IsCancellationRequested != request.IsCurrent(seq));
    }

    /// <summary>A window with nothing in flight can supersede freely, and the next request is unaffected.</summary>
    [Fact]
    public void SupersedingWithNothingInFlightIsHarmless()
    {
        var request = new LatestRequest();
        request.Supersede();
        request.Supersede();

        var (seq, token) = request.Start();
        Assert.True(request.IsCurrent(seq));
        Assert.False(token.IsCancellationRequested);
    }
}
