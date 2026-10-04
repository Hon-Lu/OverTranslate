using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimePublishOrderTests
{
    [Fact]
    public void PassesAreNumberedInTheOrderTheyWereRead()
    {
        var order = new RealtimePublishOrder();

        Assert.Equal(1, order.NextPass());
        Assert.Equal(2, order.NextPass());
        Assert.Equal(3, order.NextPass());
    }

    [Fact]
    public void PassesThatAnswerInOrderAreAllDrawn()
    {
        var order = new RealtimePublishOrder();
        var first = order.NextPass();
        var second = order.NextPass();

        Assert.True(order.TryClaim(first));
        Assert.True(order.TryClaim(second));
    }

    [Fact]
    public void AnOlderPassAnsweringLateIsNotDrawnOverANewerOne()
    {
        // The case this exists for: a long line sent first comes back after the short line that
        // replaced it. Drawing it would put the previous subtitle back on screen and leave it there.
        var order = new RealtimePublishOrder();
        var slow = order.NextPass();
        var quick = order.NextPass();

        Assert.True(order.TryClaim(quick));
        Assert.False(order.TryClaim(slow));
    }

    [Fact]
    public void APassIsDrawnAtMostOnce()
    {
        var order = new RealtimePublishOrder();
        var pass = order.NextPass();

        Assert.True(order.TryClaim(pass));
        Assert.False(order.TryClaim(pass));
    }

    [Fact]
    public void SkippedPassesDoNotBlockTheOnesAfterThem()
    {
        // Reads that end without drawing anything — unchanged text, a recogniser with no free slot —
        // still take a number, and the pass after them must not be held up by the gap.
        var order = new RealtimePublishOrder();
        order.NextPass();
        order.NextPass();
        var third = order.NextPass();

        Assert.True(order.TryClaim(third));
    }

    [Fact]
    public void ConcurrentClaimsOnlyLetOneThrough()
    {
        var order = new RealtimePublishOrder();
        var pass = order.NextPass();

        int claimed = 0;
        Parallel.For(0, 64, _ =>
        {
            if (order.TryClaim(pass)) Interlocked.Increment(ref claimed);
        });

        Assert.Equal(1, claimed);
    }
}
