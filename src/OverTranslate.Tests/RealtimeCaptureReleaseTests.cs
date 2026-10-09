using System.Drawing;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Ending a session must not wait for the capture to let go — 2.7.0 on Windows 10 is what that wait
/// cost — and a release that never finishes must not keep the application from exiting.
/// </summary>
public class RealtimeCaptureReleaseTests
{
    [Fact]
    public void AReleaseThatNeverReturnsDoesNotHoldTheCaller()
    {
        using var never = new ManualResetEventSlim(false);
        var capture = new FakeCapture(() => never.Wait());

        var watch = RealtimeCaptureRelease.Release(capture, TimeSpan.FromMilliseconds(50));

        // Returned at once; the release is still running somewhere else.
        Assert.False(watch.IsCompleted);
        Assert.True(capture.Started.Wait(TimeSpan.FromSeconds(5)));
        never.Set();
    }

    [Fact]
    public void TheReleaseRunsOnABackgroundThreadNotTheCallers()
    {
        var capture = new FakeCapture(() => { });

        RealtimeCaptureRelease.Release(capture, TimeSpan.FromSeconds(5)).Wait(TimeSpan.FromSeconds(5));

        Assert.True(capture.Started.IsSet);
        Assert.NotEqual(Environment.CurrentManagedThreadId, capture.DisposedOnThread);
        // A foreground thread stuck in a release would keep the process alive after the user quit.
        Assert.True(capture.DisposedOnBackgroundThread);
    }

    [Fact]
    public async Task AFailingReleaseIsContained()
    {
        var capture = new FakeCapture(() => throw new InvalidOperationException("device lost"));

        await RealtimeCaptureRelease.Release(capture, TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(capture.Started.IsSet);
    }

    [Fact]
    public async Task NothingToReleaseIsDoneAtOnce()
    {
        await RealtimeCaptureRelease.Release(null, TimeSpan.FromSeconds(5));
    }

    private sealed class FakeCapture(Action onDispose) : IRealtimeCaptureBackend
    {
        public ManualResetEventSlim Started { get; } = new(false);

        public int DisposedOnThread { get; private set; }

        public bool DisposedOnBackgroundThread { get; private set; }

        public string Name => "Fake";

        public Bitmap? GrabRegion(Rectangle screenBounds) => null;

        public string DescribeActivity() => "fake";

        public void Dispose()
        {
            DisposedOnThread = Environment.CurrentManagedThreadId;
            DisposedOnBackgroundThread = Thread.CurrentThread.IsBackground;
            Started.Set();
            onDispose();
        }
    }
}
