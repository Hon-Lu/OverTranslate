using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The lock-free hand-over of the kept frame between a capture backend's frame handler and its
/// polls — what replaced the lock the Windows 10 freeze was held under.
/// </summary>
public class WgcFrameHandoffTests
{
    [Fact]
    public void KeepingANewerFrameReleasesTheOneItDisplaces()
    {
        var handoff = new WgcFrameHandoff<Frame>();
        var older = new Frame();
        var newer = new Frame();

        handoff.Hold(older);
        handoff.Hold(newer);

        Assert.True(older.Disposed);
        Assert.False(newer.Disposed);
        Assert.Same(newer, handoff.Take());
    }

    [Fact]
    public void TakingHandsTheFrameOverWithoutReleasingIt()
    {
        var handoff = new WgcFrameHandoff<Frame>();
        var frame = new Frame();
        handoff.Hold(frame);

        var taken = handoff.Take();

        Assert.Same(frame, taken);
        Assert.False(frame.Disposed);
        Assert.False(handoff.HasHeld);
        Assert.Null(handoff.Take());
    }

    [Fact]
    public void ReleasingAfterATakeLeavesTheTakenFrameAlone()
    {
        // The poll took the frame and is reading it; the next arrival releases "the kept frame".
        var handoff = new WgcFrameHandoff<Frame>();
        var frame = new Frame();
        handoff.Hold(frame);

        var taken = handoff.Take();
        handoff.Release();

        Assert.Same(frame, taken);
        Assert.False(frame.Disposed);
    }

    [Fact]
    public void OnlyOneThreadReadsBackAtATime()
    {
        var handoff = new WgcFrameHandoff<Frame>();

        Assert.True(handoff.TryBeginRead());
        Assert.False(handoff.TryBeginRead());
        handoff.EndRead();
        Assert.True(handoff.TryBeginRead());
    }

    [Fact]
    public void EveryFrameIsReleasedExactlyOnceUnderContention()
    {
        // The frame handler keeps and releases while a poll takes; no frame may be lost to the pool
        // (never released) or released twice.
        var handoff = new WgcFrameHandoff<Frame>();
        var frames = new List<Frame>();
        var stop = 0;

        var poll = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                if (!handoff.TryBeginRead()) continue;
                try
                {
                    handoff.Take()?.Dispose();
                }
                finally
                {
                    handoff.EndRead();
                }
            }
        });

        for (var i = 0; i < 100_000; i++)
        {
            var frame = new Frame();
            frames.Add(frame);
            if (i % 3 == 0) handoff.Release();
            handoff.Hold(frame);
        }

        Volatile.Write(ref stop, 1);
        poll.Wait();
        handoff.Release();

        Assert.All(frames, f => Assert.Equal(1, f.DisposeCount));
    }

    private sealed class Frame : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public bool Disposed => DisposeCount > 0;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
