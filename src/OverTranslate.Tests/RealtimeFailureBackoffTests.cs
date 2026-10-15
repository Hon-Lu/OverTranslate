using System.Drawing;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A pass that failed is not repeated at every poll over the picture it failed on: the region waits,
/// longer with each failure in a row, and reads at once when the picture changes. See
/// RealtimeRegionState.FirstWaitAfterFailure.
/// </summary>
public class RealtimeFailureBackoffTests
{
    private static Bitmap Picture(Color colour)
    {
        var bitmap = new Bitmap(60, 30);
        using var canvas = Graphics.FromImage(bitmap);
        canvas.Clear(colour);
        return bitmap;
    }

    private static Func<IReadOnlyList<Rectangle>?, FrameFingerprint> Frame(Bitmap bitmap) =>
        areas => FrameFingerprint.Capture(bitmap, areas);

    private static int Polls(TimeSpan wait) => (int)Math.Round(wait / RealtimeRegionState.PollInterval);

    // Polls of the same picture until the region asks to read it; a large number when it never does.
    private static int PollsUntilRead(RealtimeRegionState state, Bitmap picture, int limit = 500)
    {
        for (int poll = 1; poll <= limit; poll++)
            if (state.Examine(Frame(picture)) != RealtimeReadReason.Nothing) return poll;
        return int.MaxValue;
    }

    // A region about to read this picture, as it is when a pass over it is first attempted.
    private static RealtimeRegionState Reading(Bitmap picture)
    {
        var state = new RealtimeRegionState();
        Assert.NotEqual(int.MaxValue, PollsUntilRead(state, picture));
        return state;
    }

    [Fact]
    public void Without_a_failure_an_unread_picture_is_read_again_at_once()
    {
        using var picture = Picture(Color.White);
        var state = Reading(picture);

        // Nothing recorded, as after a pass that threw before this existed: read at the next poll.
        Assert.Equal(1, PollsUntilRead(state, picture));
    }

    [Fact]
    public void After_a_failure_the_same_picture_waits()
    {
        using var picture = Picture(Color.White);
        var state = Reading(picture);

        state.MarkFailed(Frame(picture));

        Assert.True(state.HasFailed);
        Assert.Equal(Polls(RealtimeRegionState.FirstWaitAfterFailure), PollsUntilRead(state, picture));
    }

    [Fact]
    public void Each_failure_in_a_row_waits_twice_as_long_up_to_the_longest_wait()
    {
        using var picture = Picture(Color.White);
        var state = Reading(picture);

        var waits = new List<int>();
        for (int failure = 0; failure < 7; failure++)
        {
            state.MarkFailed(Frame(picture));
            waits.Add(PollsUntilRead(state, picture));
        }

        var first = RealtimeRegionState.FirstWaitAfterFailure;
        int longest = Polls(RealtimeRegionState.LongestWaitAfterFailure);
        Assert.Equal(
            [Polls(first), Polls(first * 2), Polls(first * 4), Polls(first * 8), longest, longest, longest],
            waits);
    }

    [Fact]
    public void A_picture_that_changes_after_a_failure_is_read_at_once()
    {
        using var failed = Picture(Color.White);
        using var next = Picture(Color.Black);
        var state = Reading(failed);

        state.MarkFailed(Frame(failed));

        // Only the ordinary wait for a change to settle, nowhere near the wait after a failure.
        Assert.True(PollsUntilRead(state, next) <= RealtimeRegionState.MaxUnsettledPolls + 1);
    }

    [Fact]
    public void A_pass_that_gets_through_ends_the_wait()
    {
        using var picture = Picture(Color.White);
        var state = Reading(picture);
        state.MarkFailed(Frame(picture));
        state.MarkFailed(Frame(picture));

        state.MarkRendered([], Frame(picture), "");

        Assert.False(state.HasFailed);
        // Back to the ordinary rules: a picture read and unchanged is not read again.
        Assert.Equal(RealtimeReadReason.Nothing, state.Examine(Frame(picture)));

        // And the count starts over: the next failure waits the first wait again, not a doubled one
        // (plus the poll a changed picture is given to settle).
        using var next = Picture(Color.Black);
        state.MarkFailed(Frame(next));
        int waited = PollsUntilRead(state, next);
        int first = Polls(RealtimeRegionState.FirstWaitAfterFailure);
        Assert.InRange(waited, first, first + RealtimeRegionState.MaxUnsettledPolls + 1);
    }
}
