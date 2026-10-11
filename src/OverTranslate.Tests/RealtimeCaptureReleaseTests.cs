using System.Drawing;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Ending a session must not wait for the capture to let go — 2.7.0 on Windows 10 is what that wait
/// cost. Which thread the capture objects are closed on is the backend's own business now: each
/// closes them on its frame thread, so the release only has to keep the wait off the caller.
/// </summary>
public class RealtimeCaptureReleaseTests
{
    [Fact]
    public void TheReleaseDoesNotRunOnTheCallersThread()
    {
        var capture = new FakeCapture(() => { });

        var watch = RealtimeCaptureRelease.Release(capture);

        Assert.True(watch.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(capture.Started.IsSet);
        Assert.NotEqual(Environment.CurrentManagedThreadId, capture.DisposedOnThread);
    }

    [Fact]
    public void AReleaseThatNeverReturnsDoesNotHoldTheCaller()
    {
        using var never = new ManualResetEventSlim(false);
        var capture = new FakeCapture(() => never.Wait());

        var watch = RealtimeCaptureRelease.Release(
            capture, release => Task.Run(release), TimeSpan.FromMilliseconds(50));

        // Returned at once; the release is still running elsewhere.
        Assert.False(watch.IsCompleted);
        Assert.True(capture.Started.Wait(TimeSpan.FromSeconds(5)));
        never.Set();
        Assert.True(watch.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AFailingReleaseIsContained()
    {
        var capture = new FakeCapture(() => throw new InvalidOperationException("device lost"));

        var watch = RealtimeCaptureRelease.Release(capture);

        Assert.True(watch.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(capture.Started.IsSet);
    }

    [Fact]
    public async Task NothingToReleaseIsDoneAtOnce()
    {
        await RealtimeCaptureRelease.Release(null, _ => throw new InvalidOperationException(), TimeSpan.FromSeconds(5));
    }

    private sealed class FakeCapture(Action onDispose) : IRealtimeCaptureBackend
    {
        public ManualResetEventSlim Started { get; } = new(false);

        public int DisposedOnThread { get; private set; }

        public string Name => "Fake";

        public Bitmap? GrabRegion(Rectangle screenBounds) => null;

        public string DescribeActivity() => "fake";

        public string DescribeSteps() => "grab=[idle]";

        public void Dispose()
        {
            DisposedOnThread = Environment.CurrentManagedThreadId;
            Started.Set();
            onDispose();
        }
    }
}
