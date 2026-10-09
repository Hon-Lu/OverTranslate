using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The warning a stuck region loop leaves behind. It is the only trace a user's log will have, so
/// it has to name the loop and the step by itself.
/// </summary>
public class RealtimeLoopWatchTests
{
    [Fact]
    public void OnlyTheLoopsStillRunningAreNamed()
    {
        var stuck = new StepMarker("starting");
        stuck.Set("grab");
        var done = new StepMarker("starting");
        done.Set("ended");

        var described = RealtimeLoopWatch.DescribeUnfinished(
        [
            new RegionLoop(0, new TaskCompletionSource().Task, stuck),
            new RegionLoop(1, Task.CompletedTask, done),
        ]);

        Assert.StartsWith("region 0 at grab for ", described);
        Assert.Contains("ms on thread ", described);
        Assert.DoesNotContain("region 1", described);
    }

    [Fact]
    public async Task LoopsThatStopInTimeLeaveNoWarning()
    {
        var watch = RealtimeLoopWatch.WatchStop(
            [new RegionLoop(0, Task.CompletedTask, new StepMarker("ended"))], null, TimeSpan.FromSeconds(5));

        await watch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void NoLoopsIsNothingToWatch()
    {
        Assert.True(RealtimeLoopWatch.WatchStop([], null, TimeSpan.FromSeconds(5)).IsCompleted);
    }

    [Fact]
    public void AStepMarkerSaysTheLastStepEntered()
    {
        var step = new StepMarker("idle");
        step.Set("held readback");

        Assert.Equal("held readback", step.Step);
        Assert.Contains($"on thread {Environment.CurrentManagedThreadId}", step.Describe());
    }
}
