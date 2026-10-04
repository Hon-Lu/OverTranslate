using System.Windows;
using OverTranslate.Layout;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Where 快速翻譯's hint lands, which is the only thing telling the user their shortcut did
/// anything.
/// </summary>
/// <remarks>
/// A 1080p desktop with the taskbar taken off the bottom, and a card the size the hint actually is
/// with one line of text in it.
/// </remarks>
public class HintPlacementTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);
    private static readonly Size Card = new(240, 60);
    private const double Gap = 6;

    private static (int Left, int Top) Place(Point pointer) =>
        HintPlacement.Place(pointer, Card, WorkArea, Gap);

    [Fact]
    public void TheCardSitsUnderTheRightOfThePointer()
    {
        // Down and to the right, where the pointer's own hotspot cannot cover it.
        var (left, top) = Place(new Point(400, 300));

        Assert.Equal(412, left);
        Assert.Equal(312, top);
    }

    [Fact]
    public void APointerInTheBottomRightCorner_StillLeavesTheCardOnScreen()
    {
        var (left, top) = Place(new Point(1915, 1035));

        Assert.Equal(WorkArea.Right - Card.Width - Gap, left);
        Assert.Equal(WorkArea.Bottom - Card.Height - Gap, top);
    }

    [Fact]
    public void APointerOnASecondMonitorIsPlacedInThatMonitorsOwnArea()
    {
        // The work area is whichever screen the pointer is on, and its coordinates do not start at
        // zero — a card clamped against 0 would land on the primary monitor instead.
        var secondary = new Rect(1920, 0, 1920, 1080);

        var (left, top) = HintPlacement.Place(new Point(3830, 20), Card, secondary, Gap);

        Assert.Equal(secondary.Right - Card.Width - Gap, left);
        Assert.Equal(32, top);
    }

    [Fact]
    public void ACardBiggerThanTheScreenIsPinnedToTheNearEdge_RatherThanThrowing()
    {
        // Math.Clamp throws when the lower bound is above the upper one, and a long enough failure
        // message on a small enough monitor gets there.
        var (left, top) = HintPlacement.Place(
            new Point(10, 10), new Size(3000, 2000), WorkArea, Gap);

        Assert.Equal(Gap, left);
        Assert.Equal(Gap, top);
    }
}
