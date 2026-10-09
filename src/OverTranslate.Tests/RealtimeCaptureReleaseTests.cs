using System.Drawing;
using System.Windows.Threading;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Ending a session must not wait for the capture to let go — 2.7.0 on Windows 10 is what that wait
/// cost — and the release must still happen on the thread that built the capture: Windows 10 refuses
/// to close a capture session from any other (RPC_E_WRONG_THREAD), and each refusal left one running.
/// </summary>
public class RealtimeCaptureReleaseTests
{
    [Fact]
    public void TheReleaseRunsOnTheOwnersDispatcherNotTheCallers()
    {
        using var owner = new DispatcherThread();
        var capture = new FakeCapture(() => { });

        var watch = RealtimeCaptureRelease.Release(
            capture, RealtimeCaptureRelease.OnDispatcher(owner.Dispatcher), TimeSpan.FromSeconds(5));

        Assert.True(watch.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(capture.Started.IsSet);
        Assert.Equal(owner.ThreadId, capture.DisposedOnThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, capture.DisposedOnThread);
    }

    [Fact]
    public void TheReleaseWaitsBehindWhatTheOwnerIsAlreadyDoing()
    {
        // Queued below rendering: the teardown the user sees, queued first, runs first.
        using var owner = new DispatcherThread();
        var order = new List<string>();
        using var gate = new ManualResetEventSlim(false);
        owner.Dispatcher.InvokeAsync(() => { gate.Wait(); order.Add("teardown"); }, DispatcherPriority.Render);
        var capture = new FakeCapture(() => order.Add("release"));

        var watch = RealtimeCaptureRelease.Release(
            capture, RealtimeCaptureRelease.OnDispatcher(owner.Dispatcher), TimeSpan.FromSeconds(5));
        gate.Set();

        Assert.True(watch.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(["teardown", "release"], order);
    }

    [Fact]
    public void AReleaseThatNeverReturnsDoesNotHoldTheCaller()
    {
        using var never = new ManualResetEventSlim(false);
        using var owner = new DispatcherThread();
        var capture = new FakeCapture(() => never.Wait());

        var watch = RealtimeCaptureRelease.Release(
            capture, RealtimeCaptureRelease.OnDispatcher(owner.Dispatcher), TimeSpan.FromMilliseconds(50));

        // Returned at once; the release is still running on the owner.
        Assert.False(watch.IsCompleted);
        Assert.True(capture.Started.Wait(TimeSpan.FromSeconds(5)));
        never.Set();
        Assert.True(watch.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void NothingIsRunInlineWhenTheOwnerHasShutDown()
    {
        // Running it on the caller's stack at exit is the one way a release could hold the exit.
        var owner = new DispatcherThread();
        var dispatcher = owner.Dispatcher;
        owner.Dispose();
        var capture = new FakeCapture(() => { });

        var watch = RealtimeCaptureRelease.Release(
            capture, RealtimeCaptureRelease.OnDispatcher(dispatcher), TimeSpan.FromSeconds(5));

        Assert.True(watch.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(capture.Started.IsSet);
    }

    [Fact]
    public void AFailingReleaseIsContained()
    {
        using var owner = new DispatcherThread();
        var capture = new FakeCapture(() => throw new InvalidOperationException("device lost"));

        var watch = RealtimeCaptureRelease.Release(
            capture, RealtimeCaptureRelease.OnDispatcher(owner.Dispatcher), TimeSpan.FromSeconds(5));

        Assert.True(watch.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(capture.Started.IsSet);
    }

    [Fact]
    public async Task NothingToReleaseIsDoneAtOnce()
    {
        await RealtimeCaptureRelease.Release(null, _ => throw new InvalidOperationException(), TimeSpan.FromSeconds(5));
    }

    /// <summary>An STA thread running a WPF dispatcher — the shape of the UI thread.</summary>
    private sealed class DispatcherThread : IDisposable
    {
        private readonly Thread _thread;

        public DispatcherThread()
        {
            using var ready = new ManualResetEventSlim(false);
            Dispatcher? dispatcher = null;
            _thread = new Thread(() =>
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Dispatcher = dispatcher!;
            ThreadId = _thread.ManagedThreadId;
        }

        public Dispatcher Dispatcher { get; }

        public int ThreadId { get; }

        public void Dispose()
        {
            Dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
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
