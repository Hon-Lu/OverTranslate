using OverTranslate.Services.LocalAi;
using Xunit;

namespace OverTranslate.Tests;

public class LocalAiServerHostTests
{
    private sealed class FakeProcess : ILocalAiServerProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri BaseAddress { get; } = new("http://127.0.0.1:1/");
        public string ApiKey => "k";
        public Task Exited => _exited.Task;
        public string RecentLog => "log tail";
        public bool Killed { get; private set; }

        public void Crash() => _exited.TrySetResult();

        public void Kill()
        {
            Killed = true;
            _exited.TrySetResult();
        }

        public void Dispose() { }
    }

    private sealed class Rig
    {
        public DateTime Now = new(2026, 1, 1);
        public TimeSpan Idle = TimeSpan.FromMinutes(10);
        public List<FakeProcess> Started { get; } = [];
        public int Attempts;
        public Func<int, CancellationToken, Task>? Before;
        public LocalAiServerHost Host { get; }

        public Rig()
        {
            Host = new LocalAiServerHost(StartAsync, () => Idle, () => Now, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        }

        private async Task<ILocalAiServerProcess> StartAsync(LocalAiLaunch launch, CancellationToken token)
        {
            var attempt = Interlocked.Increment(ref Attempts);
            if (Before is not null) await Before(attempt, token);
            var process = new FakeProcess();
            Started.Add(process);
            return process;
        }
    }

    private static readonly LocalAiLaunch A = new("a", "a.gguf");
    private static readonly LocalAiLaunch B = new("b", "b.gguf");

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition());
    }

    [Fact]
    public void NothingRunsUntilSomethingAsks() =>
        Assert.Equal(LocalAiServerState.Stopped, new Rig().Host.State);

    [Fact]
    public async Task ManyRequestsAtOnce_StartOneServer()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Before = (_, _) => gate.Task;

        var requests = Enumerable.Range(0, 5).Select(_ => rig.Host.AcquireAsync(A)).ToList();
        await Until(() => rig.Host.State == LocalAiServerState.Starting);
        gate.SetResult();
        var leases = await Task.WhenAll(requests);

        Assert.Equal(1, rig.Attempts);
        Assert.All(leases, lease => Assert.Same(rig.Started[0], lease.Process));
        Assert.Equal(LocalAiServerState.Running, rig.Host.State);
    }

    [Fact]
    public async Task AnIdleServerIsReleased_AfterTheChosenTime()
    {
        var rig = new Rig();
        (await rig.Host.AcquireAsync(A)).Dispose();

        rig.Now += TimeSpan.FromMinutes(9);
        rig.Host.CheckIdle();
        Assert.Equal(LocalAiServerState.Running, rig.Host.State);

        rig.Now += TimeSpan.FromMinutes(1);
        rig.Host.CheckIdle();
        Assert.Equal(LocalAiServerState.Stopped, rig.Host.State);
        Assert.True(rig.Started[0].Killed);
    }

    [Fact]
    public async Task ARequestInProgressKeepsTheServer_HoweverLongItTakes()
    {
        var rig = new Rig();
        var lease = await rig.Host.AcquireAsync(A);

        rig.Now += TimeSpan.FromHours(3);
        rig.Host.CheckIdle();
        Assert.Equal(LocalAiServerState.Running, rig.Host.State);

        lease.Dispose();
        rig.Now += TimeSpan.FromMinutes(10);
        rig.Host.CheckIdle();
        Assert.Equal(LocalAiServerState.Stopped, rig.Host.State);
    }

    [Fact]
    public async Task ReleaseClosesTheServer_AndTheNextRequestStartsItAgain()
    {
        var rig = new Rig();
        (await rig.Host.AcquireAsync(A)).Dispose();

        rig.Host.Release();

        Assert.Equal(LocalAiServerState.Stopped, rig.Host.State);
        Assert.True(rig.Started[0].Killed);

        (await rig.Host.AcquireAsync(A)).Dispose();
        Assert.Equal(2, rig.Attempts);
        Assert.Equal(LocalAiServerState.Running, rig.Host.State);
    }

    [Fact]
    public async Task AskingForAnotherModel_ClosesTheOneRunning()
    {
        var rig = new Rig();
        (await rig.Host.AcquireAsync(A)).Dispose();

        (await rig.Host.AcquireAsync(B)).Dispose();

        Assert.True(rig.Started[0].Killed);
        Assert.Equal("b", rig.Host.ModelId);
        Assert.Equal(LocalAiServerState.Running, rig.Host.State);
    }

    [Fact]
    public async Task ACrash_IsBroughtBackWithoutBeingAsked()
    {
        var rig = new Rig();
        (await rig.Host.AcquireAsync(A)).Dispose();

        rig.Started[0].Crash();

        await Until(() => rig.Started.Count == 2 && rig.Host.State == LocalAiServerState.Running);
        Assert.Equal("a", rig.Host.ModelId);
    }

    [Fact]
    public async Task ARequestDuringARestart_WaitsForIt()
    {
        var rig = new Rig();
        (await rig.Host.AcquireAsync(A)).Dispose();
        var gate = new TaskCompletionSource();
        rig.Before = (_, _) => gate.Task;

        rig.Started[0].Crash();
        await Until(() => rig.Host.State == LocalAiServerState.Restarting);
        var request = rig.Host.AcquireAsync(A);
        Assert.False(request.IsCompleted);

        gate.SetResult();
        using var lease = await request;
        Assert.Same(rig.Started[1], lease.Process);
    }

    [Fact]
    public async Task CrashingOverAndOver_IsReportedAndNotRetriedForAWhile()
    {
        var rig = new Rig();
        (await rig.Host.AcquireAsync(A)).Dispose();

        for (var i = 0; i <= LocalAiServerHost.MaxCrashes; i++)
        {
            var count = rig.Started.Count;
            rig.Started[^1].Crash();
            if (i < LocalAiServerHost.MaxCrashes)
                await Until(() => rig.Started.Count == count + 1 && rig.Host.State == LocalAiServerState.Running);
        }

        await Until(() => rig.Host.State == LocalAiServerState.Failed);
        var attempts = rig.Attempts;
        await Assert.ThrowsAsync<LocalAiStartFailedException>(() => rig.Host.AcquireAsync(A));
        Assert.Equal(attempts, rig.Attempts);

        rig.Now += LocalAiServerHost.CoolDown;
        (await rig.Host.AcquireAsync(A)).Dispose();
        Assert.Equal(LocalAiServerState.Running, rig.Host.State);
    }

    [Fact]
    public async Task AServerThatStayedUp_HasItsCrashesForgiven()
    {
        var rig = new Rig();
        (await rig.Host.AcquireAsync(A)).Dispose();

        for (var i = 0; i < LocalAiServerHost.MaxCrashes * 3; i++)
        {
            var count = rig.Started.Count;
            rig.Now += LocalAiServerHost.StableAfter;
            rig.Started[^1].Crash();
            await Until(() => rig.Started.Count == count + 1 && rig.Host.State == LocalAiServerState.Running);
        }
    }

    [Fact]
    public async Task AStartThatFails_SaysSo()
    {
        var rig = new Rig { Before = (_, _) => throw new InvalidOperationException("no") };

        await Assert.ThrowsAsync<LocalAiStartFailedException>(() => rig.Host.AcquireAsync(A));

        Assert.Equal(LocalAiServerState.Failed, rig.Host.State);
        Assert.Contains("no", rig.Host.FailureDetail);
    }

    [Fact]
    public async Task HavingNoGpu_IsItsOwnFailure()
    {
        var rig = new Rig { Before = (_, _) => throw new LocalAiNoGpuException() };

        await Assert.ThrowsAsync<LocalAiNoGpuException>(() => rig.Host.AcquireAsync(A));
    }

    /// <summary>A caller walking away does not take the start with it: others are waiting on it.</summary>
    [Fact]
    public async Task ACancelledRequest_LeavesTheStartRunning()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Before = (_, _) => gate.Task;
        using var cancel = new CancellationTokenSource();

        var gone = rig.Host.AcquireAsync(A, cancel.Token);
        var waiting = rig.Host.AcquireAsync(A);
        await Until(() => rig.Host.State == LocalAiServerState.Starting);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gone);

        gate.SetResult();
        using var lease = await waiting;
        Assert.Equal(1, rig.Attempts);
    }

    [Theory]
    [InlineData("  Vulkan0: NVIDIA GeForce RTX 3070 (8232 MiB, 7250 MiB free)", "Vulkan0")]
    [InlineData("Available devices:\n  Vulkan0: AMD Radeon Graphics (8000 MiB, 8000 MiB free)\n  Vulkan1: NVIDIA GeForce RTX 3070 (8232 MiB, 7250 MiB free)", "Vulkan1")]
    [InlineData("  Vulkan0: AMD Radeon Graphics (8000 MiB, 8000 MiB free)", null)]
    [InlineData("Available devices:\n", null)]
    [InlineData("", null)]
    public void OnlyADedicatedCardIsEverNamed(string output, string? expected) =>
        Assert.Equal(expected, LocalAiGpu.ChooseDevice(output, [new LocalAiGpuDevice("NVIDIA GeForce RTX 3070", 8232)]));
}
