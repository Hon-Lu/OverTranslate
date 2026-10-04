using System.Drawing;
using OverTranslate.Services.Realtime;
using Xunit;
using Rect = System.Windows.Rect;
using Vector = System.Windows.Vector;
using WinSize = System.Windows.Size;

namespace OverTranslate.Tests;

public class RealtimeComparePlacementTests
{
    private static readonly Rectangle Screen = new(0, 0, 1920, 1080);

    private static Rectangle PlacedBox(RealtimeCompareSlot slot, Point offset) =>
        RealtimeComparePlacement.Displace(slot.Bounds, offset);

    [Fact]
    public void Horizontal_GoesAboveWhenThereIsRoom()
    {
        var slot = new RealtimeCompareSlot(new Rectangle(400, 800, 800, 100), RealtimeTextOrientation.Horizontal);

        var offset = RealtimeComparePlacement.Place([slot], Screen, gap: 6)[0];

        Assert.Equal(new Point(0, -106), offset);
    }

    [Fact]
    public void Horizontal_AgainstTheTopEdge_GoesBelow()
    {
        var slot = new RealtimeCompareSlot(new Rectangle(400, 20, 800, 100), RealtimeTextOrientation.Horizontal);

        var offset = RealtimeComparePlacement.Place([slot], Screen, gap: 6)[0];

        Assert.Equal(new Point(0, 106), offset);
    }

    [Fact]
    public void Horizontal_WithAnotherBlockAbove_GoesBelow()
    {
        // Two subtitle lines framed one above the other: the lower one's copy cannot go up, because
        // that would put a translation over the other block's source text.
        var upper = new RealtimeCompareSlot(new Rectangle(400, 500, 800, 100), RealtimeTextOrientation.Horizontal);
        var lower = new RealtimeCompareSlot(new Rectangle(400, 640, 800, 100), RealtimeTextOrientation.Horizontal);

        var offsets = RealtimeComparePlacement.Place([upper, lower], Screen, gap: 6);

        Assert.Equal(new Point(0, -106), offsets[0]);
        Assert.Equal(new Point(0, 106), offsets[1]);
    }

    [Fact]
    public void Vertical_GoesLeftFirst()
    {
        var slot = new RealtimeCompareSlot(new Rectangle(1200, 100, 200, 600), RealtimeTextOrientation.Vertical);

        var offset = RealtimeComparePlacement.Place([slot], Screen, gap: 6)[0];

        Assert.Equal(new Point(-206, 0), offset);
    }

    [Fact]
    public void Vertical_AgainstTheLeftEdge_GoesRight()
    {
        var slot = new RealtimeCompareSlot(new Rectangle(50, 100, 200, 600), RealtimeTextOrientation.Vertical);

        var offset = RealtimeComparePlacement.Place([slot], Screen, gap: 6)[0];

        Assert.Equal(new Point(206, 0), offset);
    }

    [Fact]
    public void Horizontal_TallerThanHalfTheScreen_FallsBackToTheSides()
    {
        // Neither above nor below fits a block this tall; the left does.
        var slot = new RealtimeCompareSlot(new Rectangle(1000, 200, 600, 700), RealtimeTextOrientation.Horizontal);

        var offset = RealtimeComparePlacement.Place([slot], Screen, gap: 6)[0];

        Assert.Equal(new Point(-606, 0), offset);
    }

    [Fact]
    public void NothingFits_TakesTheFirstChoiceClampedOntoTheScreen()
    {
        // Nearly the whole screen: no side has room, so the box goes above and is pulled back on.
        var slot = new RealtimeCompareSlot(new Rectangle(100, 100, 1700, 900), RealtimeTextOrientation.Horizontal);

        var box = PlacedBox(slot, RealtimeComparePlacement.Place([slot], Screen)[0]);

        Assert.True(Screen.Contains(box));
        Assert.Equal(new Rectangle(100, 0, 1700, 900), box);
    }

    [Fact]
    public void AutomaticBoxes_NeverCoverAnyBlock_AndStayOnScreen()
    {
        RealtimeCompareSlot[] slots =
        [
            new(new Rectangle(300, 850, 1300, 120), RealtimeTextOrientation.Horizontal),
            new(new Rectangle(1600, 80, 220, 600), RealtimeTextOrientation.Vertical),
            new(new Rectangle(40, 40, 500, 200), RealtimeTextOrientation.Horizontal),
        ];

        var offsets = RealtimeComparePlacement.Place(slots, Screen);

        for (int i = 0; i < slots.Length; i++)
        {
            var box = PlacedBox(slots[i], offsets[i]);
            Assert.True(Screen.Contains(box));
            Assert.Equal(slots[i].Bounds.Size, box.Size);
            foreach (var other in slots)
                Assert.False(RealtimeComparePlacement.Overlaps(box, other.Bounds));
        }
    }

    [Fact]
    public void AutomaticBoxes_AvoidEachOtherWhenTheyCan()
    {
        // A subtitle line with a vertical column just to its right. The column's first choice, its
        // left, is clear of both blocks but would land on the corner of the line's copy above it, so
        // the column takes its second choice instead.
        var line = new RealtimeCompareSlot(new Rectangle(500, 600, 400, 100), RealtimeTextOrientation.Horizontal);
        var column = new RealtimeCompareSlot(new Rectangle(1000, 150, 100, 440), RealtimeTextOrientation.Vertical);

        var offsets = RealtimeComparePlacement.Place([line, column], Screen, gap: 6);

        Assert.Equal(new Point(0, -106), offsets[0]);
        Assert.Equal(new Point(106, 0), offsets[1]);
        Assert.False(RealtimeComparePlacement.Overlaps(
            PlacedBox(line, offsets[0]), PlacedBox(column, offsets[1])));
    }

    [Fact]
    public void DraggedOffset_IsKept()
    {
        var slot = new RealtimeCompareSlot(
            new Rectangle(400, 800, 800, 100), RealtimeTextOrientation.Horizontal, new Point(50, -300));

        Assert.Equal(new Point(50, -300), RealtimeComparePlacement.Place([slot], Screen)[0]);
    }

    [Fact]
    public void DraggedOffset_PushedOffScreenByTheBlockMoving_IsClampedBack()
    {
        // The offset was right when it was dragged; the block has since been moved to the top edge.
        var slot = new RealtimeCompareSlot(
            new Rectangle(400, 30, 800, 100), RealtimeTextOrientation.Horizontal, new Point(0, -106));

        var box = PlacedBox(slot, RealtimeComparePlacement.Place([slot], Screen)[0]);

        Assert.Equal(new Rectangle(400, 0, 800, 100), box);
    }

    [Fact]
    public void AutomaticBox_AvoidsADraggedOne()
    {
        // The first block's copy was dragged to exactly where the second's would go by default.
        var dragged = new RealtimeCompareSlot(
            new Rectangle(1300, 100, 400, 100), RealtimeTextOrientation.Horizontal, new Point(-900, 494));
        var automatic = new RealtimeCompareSlot(new Rectangle(400, 700, 400, 100), RealtimeTextOrientation.Horizontal);

        var offsets = RealtimeComparePlacement.Place([dragged, automatic], Screen, gap: 6);

        Assert.Equal(new Point(0, 106), offsets[1]);
    }

    [Fact]
    public void Placement_IsRelativeToTheScreenItIsOn()
    {
        // A second monitor to the right, whose top is not at zero.
        var screen = new Rectangle(1920, -200, 2560, 1440);
        var slot = new RealtimeCompareSlot(new Rectangle(2000, -180, 600, 100), RealtimeTextOrientation.Horizontal);

        var offset = RealtimeComparePlacement.Place([slot], screen, gap: 6)[0];

        Assert.Equal(new Point(0, 106), offset);
    }

    // ── Dragging ─────────────────────────────────────────────────────────────────────────────────

    private static readonly Rect DipScreen = new(0, 0, 1000, 800);

    [Fact]
    public void Drag_InOpenSpace_MovesByTheDelta()
    {
        var moved = RealtimeComparePlacement.Drag(new Rect(100, 100, 200, 50), new Vector(30.5, -20), DipScreen);

        Assert.Equal(new Rect(130.5, 80, 200, 50), moved);
    }

    [Fact]
    public void Drag_IsClampedToTheScreen()
    {
        var moved = RealtimeComparePlacement.Drag(new Rect(900, 10, 80, 40), new Vector(200, -100), DipScreen);

        Assert.Equal(new Rect(920, 0, 80, 40), moved);
    }

    [Fact]
    public void Drag_GoesWhereverThePointerAsks_EvenRightOverTheBlock()
    {
        // The copy sits above its block; dragged straight down onto it, it is not stopped.
        var moved = RealtimeComparePlacement.Drag(new Rect(100, 100, 200, 50), new Vector(0, 106), DipScreen);

        Assert.Equal(new Rect(100, 206, 200, 50), moved);
    }

    // ── Resizing the block under a dragged copy ──────────────────────────────────────────────────

    private static readonly Rect Block = new(400, 400, 200, 100);

    [Fact]
    public void Resized_CopyAbove_KeepsItsGapToTheTopEdge()
    {
        var copy = new Rect(450, 290, 200, 100);              // 10 above, shifted right by 50
        var taller = new Rect(400, 350, 200, 150);            // pulled up by 50

        var kept = RealtimeComparePlacement.Resized(Block, copy, taller, DipScreen);

        Assert.Equal(new Rect(450, 190, 200, 150), kept);
        Assert.Equal(10, taller.Top - kept.Bottom, 6);
    }

    [Fact]
    public void Resized_CopyBelow_KeepsItsGapToTheBottomEdge()
    {
        var copy = new Rect(400, 520, 200, 100);              // 20 below
        var taller = new Rect(400, 380, 200, 150);            // pulled down at the bottom and up at the top

        var kept = RealtimeComparePlacement.Resized(Block, copy, taller, DipScreen);

        Assert.Equal(550, kept.Top);
        Assert.Equal(20, kept.Top - taller.Bottom, 6);
    }

    [Fact]
    public void Resized_CopyLeft_KeepsItsGapToTheLeftEdge()
    {
        var copy = new Rect(190, 400, 200, 100);              // 10 to the left
        var wider = new Rect(350, 400, 300, 100);

        var kept = RealtimeComparePlacement.Resized(Block, copy, wider, DipScreen);

        Assert.Equal(new Rect(40, 400, 300, 100), kept);
        Assert.Equal(10, wider.Left - kept.Right, 6);
    }

    [Fact]
    public void Resized_CopyRight_KeepsItsGapToTheRightEdge()
    {
        var copy = new Rect(606, 400, 200, 100);              // 6 to the right
        var wider = new Rect(400, 400, 250, 100);

        var kept = RealtimeComparePlacement.Resized(Block, copy, wider, DipScreen);

        Assert.Equal(656, kept.Left);
        Assert.Equal(250, kept.Width);
    }

    [Fact]
    public void Resized_CopyOverlappingOnAnAxis_KeepsItsOffsetFromTheCorner()
    {
        // Overlapping on both axes: half over the block, down and to the right.
        var copy = new Rect(450, 430, 200, 100);
        var bigger = new Rect(380, 390, 260, 140);

        var kept = RealtimeComparePlacement.Resized(Block, copy, bigger, DipScreen);

        Assert.Equal(new Rect(430, 420, 260, 140), kept);
    }

    [Fact]
    public void Resized_EachAxisIsDecidedOnItsOwn()
    {
        // Above the block (Y keeps the gap) but overlapping it across (X keeps the offset).
        var copy = new Rect(430, 294, 200, 100);
        var resized = new Rect(380, 360, 260, 140);

        var kept = RealtimeComparePlacement.Resized(Block, copy, resized, DipScreen);

        Assert.Equal(410, kept.X);
        Assert.Equal(6, resized.Top - kept.Bottom, 6);
    }

    [Fact]
    public void Resized_IsHeldOnTheScreen()
    {
        // Above a block near the top edge: pulled taller at the top, the gap would put it off screen.
        var block = new Rect(400, 120, 200, 100);
        var copy = new Rect(400, 14, 200, 100);
        var taller = new Rect(400, 60, 200, 160);

        var kept = RealtimeComparePlacement.Resized(block, copy, taller, DipScreen);

        Assert.Equal(0, kept.Top);
        Assert.Equal(taller.Size, kept.Size);
    }

    // ── Turned off for one block ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Place_HiddenSlot_TakesNoRoom()
    {
        // Another block's copy dragged into the room above this one sends this one's below — until
        // that copy is turned off.
        var other = new RealtimeCompareSlot(
            new Rectangle(100, 100, 200, 100), RealtimeTextOrientation.Horizontal, new Point(600, 434));
        var line = new RealtimeCompareSlot(new Rectangle(700, 640, 600, 100), RealtimeTextOrientation.Horizontal);

        var shown = RealtimeComparePlacement.Place([other, line], Screen, gap: 6)[1];
        var hidden = RealtimeComparePlacement.Place([other with { Hidden = true }, line], Screen, gap: 6)[1];

        Assert.Equal(new Point(0, 106), shown);
        Assert.Equal(new Point(0, -106), hidden);
    }

    [Fact]
    public void Place_HiddenSlot_ItsBlockIsStillKeptClearOf()
    {
        var upper = new RealtimeCompareSlot(
            new Rectangle(400, 500, 800, 100), RealtimeTextOrientation.Horizontal, Hidden: true);
        var lower = new RealtimeCompareSlot(new Rectangle(400, 640, 800, 100), RealtimeTextOrientation.Horizontal);

        var offset = RealtimeComparePlacement.Place([upper, lower], Screen, gap: 6)[1];

        Assert.False(RealtimeComparePlacement.Overlaps(PlacedBox(lower, offset), upper.Bounds));
    }

    // ── Scale ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Displace_ScalesTheSizeAndKeepsTheOffset()
    {
        var box = RealtimeComparePlacement.Displace(new Rectangle(400, 800, 802, 100), new Point(10, -60), 0.5);

        Assert.Equal(new Rectangle(410, 740, 401, 50), box);
    }

    [Fact]
    public void Place_Automatic_UsesTheScaledSize()
    {
        // Half the height above, so the gap to the block is still 6.
        var slot = new RealtimeCompareSlot(
            new Rectangle(400, 800, 800, 100), RealtimeTextOrientation.Horizontal, Scale: 0.5);

        var offset = RealtimeComparePlacement.Place([slot], Screen, gap: 6)[0];

        Assert.Equal(new Point(0, -56), offset);
    }

    [Fact]
    public void Place_Automatic_ScaledBoxFitsWhereTheFullOneWouldNot()
    {
        // 70 above the block: a full-size copy does not fit, a half-size one does.
        var slot = new RealtimeCompareSlot(
            new Rectangle(400, 70, 800, 100), RealtimeTextOrientation.Horizontal, Scale: 0.5);

        var offset = RealtimeComparePlacement.Place([slot], Screen, gap: 6)[0];

        Assert.Equal(new Point(0, -56), offset);
    }

    [Fact]
    public void Place_Dragged_IsHeldOnTheScreenAtItsScaledSize()
    {
        // Off the right edge: pulled back by the half width it has, not the block's full width.
        var slot = new RealtimeCompareSlot(
            new Rectangle(400, 800, 800, 100), RealtimeTextOrientation.Horizontal, new Point(1500, 0), 0.5);

        var box = RealtimeComparePlacement.Displace(
            slot.Bounds, RealtimeComparePlacement.Place([slot], Screen)[0], slot.Scale);

        Assert.Equal(new Rectangle(1520, 800, 400, 50), box);
    }

    [Fact]
    public void Resized_ShrunkCopyAbove_KeepsItsScaleAndItsGap()
    {
        var copy = new Rect(400, 344, 100, 50);               // half size, 6 above
        var taller = new Rect(400, 350, 200, 150);            // pulled up by 50

        var kept = RealtimeComparePlacement.Resized(Block, copy, taller, DipScreen, scale: 0.5);

        Assert.Equal(new Rect(400, 269, 100, 75), kept);
        Assert.Equal(6, taller.Top - kept.Bottom, 6);
    }

    [Fact]
    public void ScaleFromCorner_BottomRight_ShrinksAboutTheTopLeft()
    {
        var (box, scale) = RealtimeComparePlacement.ScaleFromCorner(
            new Rect(100, 200, 400, 100), new WinSize(400, 100), 3, new Vector(-200, -50), DipScreen);

        Assert.Equal(0.5, scale, 6);
        Assert.Equal(new Rect(100, 200, 200, 50), box);
    }

    [Fact]
    public void ScaleFromCorner_TopLeft_ShrinksAboutTheBottomRight()
    {
        var (box, scale) = RealtimeComparePlacement.ScaleFromCorner(
            new Rect(100, 200, 400, 100), new WinSize(400, 100), 0, new Vector(40, 10), DipScreen);

        Assert.Equal(0.9, scale, 6);
        Assert.Equal(500, box.Right, 6);
        Assert.Equal(300, box.Bottom, 6);
        Assert.Equal(360, box.Width, 6);
    }

    [Fact]
    public void ScaleFromCorner_OffTheDiagonal_StillFollowsThePull()
    {
        // Straight out sideways from the top-right corner, nothing vertical.
        var (_, scale) = RealtimeComparePlacement.ScaleFromCorner(
            new Rect(100, 200, 200, 50), new WinSize(400, 100), 1, new Vector(100, 0), DipScreen);

        Assert.True(scale > 0.5 && scale < 1.0);
    }

    [Fact]
    public void ScaleFromCorner_IsHeldBetweenHalfAndFull()
    {
        var shrunk = RealtimeComparePlacement.ScaleFromCorner(
            new Rect(100, 200, 400, 100), new WinSize(400, 100), 3, new Vector(-1000, -1000), DipScreen);
        var grown = RealtimeComparePlacement.ScaleFromCorner(
            new Rect(100, 200, 200, 50), new WinSize(400, 100), 3, new Vector(1000, 1000), DipScreen);

        Assert.Equal(RealtimeComparePlacement.MinScale, shrunk.Scale, 6);
        Assert.Equal(new Rect(100, 200, 200, 50), shrunk.Box);
        Assert.Equal(1.0, grown.Scale, 6);
        Assert.Equal(new Rect(100, 200, 400, 100), grown.Box);
    }

    [Fact]
    public void ScaleFromCorner_IsHeldOnTheScreen()
    {
        // Half size, its top-left 300 from the right edge: it can only grow to 300 wide there.
        var (box, scale) = RealtimeComparePlacement.ScaleFromCorner(
            new Rect(700, 600, 200, 50), new WinSize(400, 100), 3, new Vector(500, 500), DipScreen);

        Assert.Equal(0.75, scale, 6);
        Assert.Equal(new Rect(700, 600, 300, 75), box);
    }
}
