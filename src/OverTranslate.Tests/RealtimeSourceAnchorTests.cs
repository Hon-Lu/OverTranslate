using System.Drawing;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 視窗擷取's blocks stay on the window they were drawn over: they follow it when it moves, and
/// wait while it is another size — a game in fullscreen at a lower resolution, or back from one at a
/// different size — rather than read whatever part of it now sits under the old place.
/// </summary>
public class RealtimeSourceAnchorTests
{
    private static readonly Rectangle Anchor = new(200, 120, 1600, 960);
    private static readonly Rectangle Block = new(856, 168, 288, 67);

    [Fact]
    public void A_window_that_has_not_moved_leaves_the_block_where_it_was()
    {
        Assert.Equal(Block, RealtimeSourceAnchor.Follow(Block, Anchor, Anchor));
        Assert.Equal(Point.Empty, RealtimeSourceAnchor.Offset(Anchor, Anchor));
    }

    [Fact]
    public void A_moved_window_takes_its_blocks_with_it()
    {
        var moved = Anchor with { X = 631, Y = 322 };

        Assert.Equal(new Point(431, 202), RealtimeSourceAnchor.Offset(Anchor, moved));
        Assert.Equal(new Rectangle(1287, 370, 288, 67), RealtimeSourceAnchor.Follow(Block, Anchor, moved));
    }

    // The primary monitor switched to 1280x720 for the game's fullscreen: the same block at the same
    // place would be reading a different part of a picture laid out again.
    [Fact]
    public void A_window_at_another_size_has_no_answer()
    {
        var fullscreen = new Rectangle(0, 0, 1280, 720);

        Assert.Null(RealtimeSourceAnchor.Offset(Anchor, fullscreen));
        Assert.Null(RealtimeSourceAnchor.Follow(Block, Anchor, fullscreen));
    }

    [Fact]
    public void Back_at_its_size_the_blocks_return_wherever_the_window_is()
    {
        var back = Anchor with { X = 240, Y = 100 };

        Assert.Equal(new Rectangle(896, 148, 288, 67), RealtimeSourceAnchor.Follow(Block, Anchor, back));
    }
}
