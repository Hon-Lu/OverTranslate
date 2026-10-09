using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class StallWatchTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task WorkThatFinishesInTimeIsNotReported()
    {
        var stalled = 0;
        var late = 0;

        await StallWatch.Watch(
            Task.CompletedTask, Short, () => stalled++, () => late++);

        Assert.Equal(0, stalled);
        Assert.Equal(0, late);
    }

    [Fact]
    public async Task WorkStillRunningIsReportedOnceAndItsLateFinishToo()
    {
        var work = new TaskCompletionSource();
        var stalled = new TaskCompletionSource();
        var late = 0;

        var watch = StallWatch.Watch(work.Task, Short, () => stalled.TrySetResult(), () => late++);

        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(watch.IsCompleted);
        Assert.Equal(0, late);

        work.SetResult();
        await watch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, late);
    }

    [Fact]
    public async Task AStalledWorkThatFailsStillCountsAsFinished()
    {
        var work = new TaskCompletionSource();
        var late = 0;

        var watch = StallWatch.Watch(work.Task, Short, () => work.SetException(new InvalidOperationException()), () => late++);

        await watch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, late);
    }

    [Fact]
    public void TheCallerIsNeverHeld()
    {
        var work = new TaskCompletionSource();

        var watch = StallWatch.Watch(work.Task, TimeSpan.FromMinutes(5), () => { });

        Assert.False(watch.IsCompleted);
        work.SetResult();
    }
}
