using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeBackgroundCadenceTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(20, 80)]
    [InlineData(50, 50)]
    [InlineData(80, 20)]
    [InlineData(100, 0)]
    [InlineData(101, 0)]
    [InlineData(250, 0)]
    public void RestAfter_TargetsTenHzWithoutCatchUpForExpensiveFrames(int workMs, int restMs)
    {
        var work = TimeSpan.FromMilliseconds(workMs);
        var rest = RealtimeBackgroundCadence.RestAfter(work);
        Assert.Equal(TimeSpan.FromMilliseconds(restMs), rest);
        Assert.True(work + rest >= RealtimeBackgroundCadence.TargetInterval);
        Assert.True(rest >= TimeSpan.Zero);
    }
}
