using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeTranslationCacheTests
{
    [Fact]
    public void FenceKeepsWhatHasAlreadyBeenTranslated()
    {
        // The point of 暫停 → 繼續 over a scene that has not changed: the reading matches, so the
        // wording is already here and the provider is not asked a second time. What the fence is for
        // is the pass, not the wording — see ThePassInFlightWhenTheFenceMovedCannotPublish.
        var cache = new RealtimeTranslationCache();
        cache.Set("line", "old translation", cache.Generation);

        var afterFence = cache.Fence();

        Assert.True(cache.TryGet("line", afterFence, out var value));
        Assert.Equal("old translation", value);
    }

    [Fact]
    public void ThePassInFlightWhenTheFenceMovedCannotPublish()
    {
        // What a pause actually has to stop: a provider answer for the scene the user has left,
        // arriving after they paused and painting itself over the one they are looking at now.
        var cache = new RealtimeTranslationCache();
        var passInFlight = cache.Generation;

        var afterFence = cache.Fence();

        Assert.NotEqual(passInFlight, afterFence);
        Assert.False(cache.IsCurrent(passInFlight));
        Assert.True(cache.IsCurrent(afterFence));
        Assert.False(cache.TryGet("line", passInFlight, out _));
    }

    [Fact]
    public void ProviderAnswerThatReturnsAfterTheFenceIsNotStored()
    {
        // Harmless in itself — a translation of that text is a translation of that text — but the
        // rule stays simple: nothing from before the fence is written after it.
        var cache = new RealtimeTranslationCache();
        var passInFlight = cache.Generation;

        var afterFence = cache.Fence();
        cache.Set("line", "late old translation", passInFlight);

        Assert.False(cache.TryGet("line", afterFence, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void CurrentGenerationCanBeCachedAfterTheFence()
    {
        var cache = new RealtimeTranslationCache();
        var generation = cache.Fence();

        cache.Set("line", "new translation", generation);

        Assert.True(cache.TryGet("line", generation, out var value));
        Assert.Equal("new translation", value);
    }

    [Fact]
    public void StaleProviderCannotClearTheCurrentGenerationAtTheSizeLimit()
    {
        var cache = new RealtimeTranslationCache();
        var staleGeneration = cache.Generation;
        var currentGeneration = cache.Fence();
        cache.Set("line", "new translation", currentGeneration);

        cache.ClearIfOverLimit(0, staleGeneration);

        Assert.True(cache.TryGet("line", currentGeneration, out var value));
        Assert.Equal("new translation", value);
    }

    [Fact]
    public void TheSizeLimitStillEmptiesTheCache()
    {
        // The bound is what keeps a long session on scrolling content from growing without limit, and
        // it is the only thing that drops entries now that a pause does not.
        var cache = new RealtimeTranslationCache();
        cache.Set("a", "one", cache.Generation);
        cache.Set("b", "two", cache.Generation);

        cache.ClearIfOverLimit(1, cache.Generation);

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("a", cache.Generation, out _));
    }
}
