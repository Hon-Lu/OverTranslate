using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The capture backends' readback throttle, driven by a timeline instead of a compositor.
/// </summary>
/// <remarks>
/// Capture only emits a frame when the screen changes. Measured on a still comic page scrolled once:
/// a marker repainted just before the scroll was read back, the scroll's own frame arrived 68ms
/// later and was thrown away as too soon, and nothing else arrived for 0.8s — so every poll in
/// between compared the old page against itself and saw no change.
/// </remarks>
public class WgcFrameThrottleTests
{
    private static readonly TimeSpan MaxFrameAge = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(1);

    [Fact]
    public void TheLastFrameOfABurstReachesTheNextPollOnAStillScreen()
    {
        var capture = new Timeline();
        capture.Frame(0, id: 1);
        capture.Frame(68, id: 2);   // the scroll, too soon after the read before it
        // Nothing else is ever composed: the page holds still from here on.
        Assert.Equal(1, capture.Poll(100));
        Assert.Equal(2, capture.Poll(250));
    }

    [Fact]
    public void ANewerFrameReplacesTheOneHeld()
    {
        var capture = new Timeline();
        capture.Frame(0, id: 1);
        capture.Frame(40, id: 2);
        capture.Frame(90, id: 3);
        Assert.Equal(3, capture.Poll(150));
    }

    [Fact]
    public void AMovingPictureIsStillReadBackAtMostOncePerMaxFrameAge()
    {
        // A video or a game: a frame every refresh at 144Hz, polled every 150ms for ten seconds.
        var capture = new Timeline();
        var nextPoll = 0.0;
        for (var at = 0.0; at < 10_000; at += 1000.0 / 144)
        {
            while (nextPoll <= at)
            {
                capture.Poll(nextPoll);
                nextPoll += 150;
            }
            capture.Frame(at, id: (int)at);
        }

        var gaps = capture.Reads.Zip(capture.Reads.Skip(1), (a, b) => b - a);
        Assert.All(gaps, gap => Assert.True(gap >= MaxFrameAge.TotalMilliseconds, $"reads {gap}ms apart"));
        Assert.InRange(capture.Reads.Count, 60, (int)(10_000 / MaxFrameAge.TotalMilliseconds) + 1);
    }

    [Fact]
    public void NothingIsHeldOnceNobodyIsPolling()
    {
        Assert.Equal(
            WgcFrameThrottle.Arrival.Skip,
            WgcFrameThrottle.OnArrival(true, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2), MaxFrameAge, IdleAfter));
        Assert.Equal(
            WgcFrameThrottle.Arrival.Read,
            WgcFrameThrottle.OnArrival(false, TimeSpan.Zero, TimeSpan.FromSeconds(2), MaxFrameAge, IdleAfter));
    }

    /// <summary>The backend's bookkeeping around the policy, with times in milliseconds.</summary>
    private sealed class Timeline
    {
        private int? _latest;
        private double _latestAt;
        private double _lastGrabAt;
        private int? _held;

        public List<double> Reads { get; } = [];

        public void Frame(double at, int id)
        {
            switch (WgcFrameThrottle.OnArrival(
                _latest is not null, Ms(at - _latestAt), Ms(at - _lastGrabAt), MaxFrameAge, IdleAfter))
            {
                case WgcFrameThrottle.Arrival.Hold:
                    _held = id;
                    break;
                case WgcFrameThrottle.Arrival.Skip:
                    _held = null;
                    break;
                default:
                    _held = null;
                    Read(at, id);
                    break;
            }
        }

        public int? Poll(double at)
        {
            _lastGrabAt = at;
            if (_held is { } held && WgcFrameThrottle.ReadsHeldFrame(Ms(at - _latestAt), MaxFrameAge))
            {
                _held = null;
                Read(at, held);
            }
            return _latest;
        }

        private void Read(double at, int id)
        {
            _latest = id;
            _latestAt = at;
            Reads.Add(at);
        }

        private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);
    }
}
