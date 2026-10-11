using System.Drawing;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Which polls without a picture take a translation off the screen: only a region the source has
/// really left. The case this exists for: a still page made smaller, then larger again — its last
/// frame stays the small one, and the block that came back over the page had been cleared for good.
/// </summary>
public class CaptureMissTests
{
    private static readonly Rectangle Window = new(-2560, 0, 1400, 1300);
    private static readonly Rectangle LowerBlock = new(-2516, 850, 960, 220);

    // The page back at its full size, the last frame still the short one: late, not gone.
    [Fact]
    public void A_block_over_the_window_as_it_is_now_is_waiting()
    {
        Assert.Equal(CaptureMiss.Waiting, CaptureMissRules.ForWindow(true, Window, LowerBlock));
    }

    [Fact]
    public void A_block_the_window_no_longer_reaches_is_off_the_source()
    {
        var shorter = Window with { Height = 820 };

        Assert.Equal(CaptureMiss.NotOnSource, CaptureMissRules.ForWindow(true, shorter, LowerBlock));
    }

    [Fact]
    public void A_block_the_window_moved_away_from_is_off_the_source()
    {
        var moved = Window with { X = -1200 };

        Assert.Equal(CaptureMiss.NotOnSource, CaptureMissRules.ForWindow(true, moved, LowerBlock with { Width = 300 }));
    }

    // Minimised, hidden or cloaked: off the source wherever its rectangle says it is.
    [Fact]
    public void A_window_not_on_the_screen_is_off_the_source()
    {
        Assert.Equal(CaptureMiss.NotOnSource, CaptureMissRules.ForWindow(false, Window, LowerBlock));
    }

    [Fact]
    public void Screen_capture_clears_only_a_block_off_the_monitor()
    {
        var monitor = new Rectangle(-2560, 0, 2560, 1440);

        Assert.Equal(CaptureMiss.Waiting, CaptureMissRules.ForMonitor(monitor, LowerBlock));
        Assert.Equal(CaptureMiss.NotOnSource, CaptureMissRules.ForMonitor(monitor with { Width = 1280, Height = 720 }, LowerBlock));
        Assert.Equal(CaptureMiss.Waiting, CaptureMissRules.ForMonitor(null, LowerBlock));
    }

    // A window's frame is one of its two rectangles; anything else is the window at another size.
    [Theory]
    [InlineData(1384, 1292, true)]  // the extended frame bounds
    [InlineData(1400, 1300, true)]  // the window rectangle, with its invisible resize border
    [InlineData(1234, 812, false)]  // the frame from before the window was made larger
    public void A_frame_matches_the_window_at_one_of_its_two_sizes(int width, int height, bool matches)
    {
        Assert.Equal(matches, CaptureMissRules.MatchesWindow(new Size(width, height), new Size(1384, 1292), new Size(1400, 1300)));
    }

    private static readonly Size Small = new(1234, 812);
    private static readonly Size Large = new(1384, 1292);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    // Asked cheaply first; restarted when that has not brought a frame; bounded.
    [Fact]
    public void A_still_window_is_asked_to_repaint_then_the_capture_is_restarted()
    {
        var request = new FreshFrameRequest();
        var asks = new List<FreshFrameRequest.Ask>();
        for (var ms = 0; ms <= 6000; ms += 150)
        {
            var ask = request.Next(Small, Large, Ms(1000 + ms));
            if (ask != FreshFrameRequest.Ask.None) asks.Add(ask);
        }

        Assert.Equal(FreshFrameRequest.Ask.Redraw, asks[0]);
        Assert.Equal(FreshFrameRequest.MaxRestarts, asks.Count(a => a == FreshFrameRequest.Ask.Restart));
        Assert.Equal(FreshFrameRequest.MaxRedraws, asks.Count(a => a == FreshFrameRequest.Ask.Redraw));
        // Redraws are tried first: the restart only after they have had their second.
        Assert.True(asks.IndexOf(FreshFrameRequest.Ask.Restart) >= 1);
    }

    [Fact]
    public void Polls_close_together_ask_once()
    {
        var request = new FreshFrameRequest();

        Assert.Equal(FreshFrameRequest.Ask.Redraw, request.Next(Small, Large, Ms(0)));
        Assert.Equal(FreshFrameRequest.Ask.None, request.Next(Small, Large, Ms(100)));
        Assert.Equal(FreshFrameRequest.Ask.None, request.Next(Small, Large, Ms(250)));
        Assert.Equal(FreshFrameRequest.Ask.Redraw, request.Next(Small, Large, Ms(320)));
    }

    // A later resize is a new mismatch with its own budget, once the frame has caught up in between.
    [Fact]
    public void A_settled_window_starts_afresh_next_time()
    {
        var request = new FreshFrameRequest();
        for (var ms = 0; ms <= 6000; ms += 150) request.Next(Small, Large, Ms(ms));
        Assert.Equal(FreshFrameRequest.Ask.None, request.Next(Small, Large, Ms(6500)));

        request.Settled();

        Assert.Equal(FreshFrameRequest.Ask.Redraw, request.Next(Small, Large, Ms(20000)));
    }

    [Fact]
    public void Another_size_is_another_mismatch()
    {
        var request = new FreshFrameRequest();
        request.Next(Small, Large, Ms(0));

        Assert.Equal(FreshFrameRequest.Ask.Redraw, request.Next(Small, new Size(1500, 1292), Ms(50)));
    }
}
