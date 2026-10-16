using System.Drawing;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A translation that failed is not asked for again at every poll: the retry waits, longer with each
/// failure in a row, while a picture that changes is still read at once. See RealtimeRetryBackoff.
/// </summary>
public class RealtimeRetryBackoffTests
{
    private TimeSpan _now = TimeSpan.FromSeconds(100);

    private RealtimeRetryBackoff Backoff() => new(() => _now);

    private static readonly TimeSpan First = RealtimeRegionState.FirstWaitAfterFailure;

    // How long after the request the retry is first taken, stepping the clock a tenth of a second.
    private TimeSpan WaitUntilTaken(RealtimeRetryBackoff backoff)
    {
        var start = _now;
        for (int step = 0; step < 1000; step++)
        {
            if (backoff.TakeRequest()) return _now - start;
            _now += TimeSpan.FromMilliseconds(100);
        }
        return TimeSpan.MaxValue;
    }

    [Fact]
    public void A_retry_with_no_failure_behind_it_is_taken_at_once()
    {
        var backoff = Backoff();
        backoff.Request();

        Assert.True(backoff.TakeRequest());
        Assert.False(backoff.TakeRequest());
    }

    [Fact]
    public void A_retry_after_a_failure_waits_and_is_then_taken()
    {
        var backoff = Backoff();
        backoff.RequestAfterFailure();

        _now += First - TimeSpan.FromMilliseconds(1);
        Assert.False(backoff.TakeRequest());

        _now += TimeSpan.FromMilliseconds(1);
        Assert.True(backoff.TakeRequest());
    }

    [Fact]
    public void Each_failure_in_a_row_waits_twice_as_long_up_to_the_longest_wait()
    {
        var backoff = Backoff();

        var waits = new List<TimeSpan>();
        for (int failure = 0; failure < 7; failure++)
        {
            backoff.RequestAfterFailure();
            waits.Add(WaitUntilTaken(backoff));
        }

        var longest = RealtimeRegionState.LongestWaitAfterFailure;
        Assert.Equal([First, First * 2, First * 4, First * 8, longest, longest, longest], waits);
    }

    [Fact]
    public void A_translation_drawn_whole_starts_the_count_over()
    {
        var backoff = Backoff();
        backoff.RequestAfterFailure();
        backoff.RequestAfterFailure();
        backoff.RequestAfterFailure();

        backoff.Succeeded();

        // The request already made is still taken, now without a wait...
        Assert.True(backoff.TakeRequest());
        // ...and the next failure waits the first wait again, not a doubled one.
        backoff.RequestAfterFailure();
        Assert.Equal(First, WaitUntilTaken(backoff));
    }

    private static Bitmap Picture(Color colour)
    {
        var bitmap = new Bitmap(60, 30);
        using var canvas = Graphics.FromImage(bitmap);
        canvas.Clear(colour);
        return bitmap;
    }

    private static Func<IReadOnlyList<Rectangle>?, FrameFingerprint> Frame(Bitmap bitmap) =>
        areas => FrameFingerprint.Capture(bitmap, areas);

    // One poll as the region's loop runs it: a due retry forgets what is shown, then the region is examined.
    private static RealtimeReadReason Poll(
        RealtimeRetryBackoff backoff, RealtimeRegionState state, Bitmap picture)
    {
        if (backoff.TakeRequest()) state.Invalidate();
        return state.Examine(Frame(picture));
    }

    [Fact]
    public void While_a_retry_waits_the_same_picture_is_not_read_but_a_changed_one_is()
    {
        using var shown = Picture(Color.White);
        using var next = Picture(Color.Black);
        var backoff = Backoff();
        var state = new RealtimeRegionState();
        state.MarkRendered([], Frame(shown), "");

        backoff.RequestAfterFailure();

        // The clock does not move: the wait has not passed at any of these polls.
        for (int poll = 0; poll < 20; poll++)
            Assert.Equal(RealtimeReadReason.Nothing, Poll(backoff, state, shown));

        int polls = 0;
        while (Poll(backoff, state, next) == RealtimeReadReason.Nothing && ++polls < 100) { }
        Assert.True(polls <= RealtimeRegionState.MaxUnsettledPolls);
    }
}
