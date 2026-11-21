using OverTranslate.Views.Realtime;
using Xunit;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace OverTranslate.Tests;

public class RealtimeEditModeControlTests
{
    private const double ScreenHeight = 1080;
    private const double Gap = 6;
    private static readonly Size Control = new(300, 80);

    private static double Top(Rect block, Rect? copy = null) =>
        RealtimeEditWindow.ModeControlTop(block, Control, block.Left, copy, ScreenHeight, Gap);

    [Fact]
    public void RoomAbove_GoesAbove()
    {
        var block = new Rect(400, 800, 800, 100);

        Assert.Equal(800 - 80 - 6, Top(block));
    }

    [Fact]
    public void AgainstTheTopEdge_TucksInsideRatherThanBelow()
    {
        var block = new Rect(400, 20, 800, 100);

        Assert.Equal(20 + 6, Top(block));
    }

    [Fact]
    public void JustEnoughRoomAbove_StillGoesAbove()
    {
        var block = new Rect(400, 86, 800, 100);

        Assert.Equal(0, Top(block));
    }

    [Fact]
    public void OwnCopyAbove_TucksInside()
    {
        // The automatic copy of a horizontal block sits right where the trays would.
        var block = new Rect(400, 800, 800, 100);
        var copy = new Rect(400, 694, 800, 100);

        Assert.Equal(800 + 6, Top(block, copy));
    }

    [Fact]
    public void OwnCopyDraggedClear_GoesAboveAgain()
    {
        var block = new Rect(400, 800, 800, 100);
        var copy = new Rect(1300, 500, 800, 100);

        Assert.Equal(800 - 80 - 6, Top(block, copy));
    }

    [Fact]
    public void OwnCopyBelow_GoesAbove()
    {
        var block = new Rect(400, 800, 800, 100);
        var copy = new Rect(400, 906, 800, 100);

        Assert.Equal(800 - 80 - 6, Top(block, copy));
    }

    [Fact]
    public void CopyOnlyTouchingTheTrays_GoesAbove()
    {
        var block = new Rect(400, 800, 800, 100);
        var copy = new Rect(400, 614, 800, 100); // bottom edge flush with the trays' top

        Assert.Equal(800 - 80 - 6, Top(block, copy));
    }

    [Fact]
    public void TuckedInside_IsKeptOnTheScreen()
    {
        var block = new Rect(400, 1040, 800, 40);
        var copy = new Rect(400, 950, 800, 40);

        Assert.Equal(ScreenHeight - 80, Top(block, copy));
    }

    private const double RemoveSize = 22;

    // The remove button's top, with the trays placed the way the layer places them.
    private static double RemoveTop(Rect block, Rect? copy = null) =>
        RealtimeEditWindow.RemoveTop(block, Top(block, copy), Control.Height, RemoveSize, ScreenHeight, Gap);

    [Fact]
    public void Remove_TraysAbove_StaysLevelWithTheTopEdge()
    {
        var block = new Rect(400, 800, 800, 100);

        Assert.Equal(800, RemoveTop(block));
    }

    [Fact]
    public void Remove_NarrowBlockAgainstTheTopEdge_StepsBelowTheTrays()
    {
        // Narrower than the trays: tucked inside they would cover the button.
        var block = new Rect(400, 20, 200, 100);

        Assert.Equal(20 + 6 + 80 + 6, RemoveTop(block));
    }

    [Fact]
    public void Remove_WideBlockAgainstTheTopEdge_StepsBelowTheTraysToo()
    {
        var block = new Rect(400, 20, 1200, 100);

        Assert.Equal(20 + 6 + 80 + 6, RemoveTop(block));
    }

    [Fact]
    public void Remove_InTheTopRightCorner_StepsBelowTheTrays()
    {
        // Against the right edge the button is tucked inside as well; only its left changes.
        var block = new Rect(1700, 0, 220, 100);

        Assert.Equal(0 + 6 + 80 + 6, RemoveTop(block));
    }

    [Fact]
    public void Remove_TraysTuckedForTheOwnCopy_StepsBelowTheTrays()
    {
        var block = new Rect(400, 800, 800, 100);
        var copy = new Rect(400, 694, 800, 100);

        Assert.Equal(800 + 6 + 80 + 6, RemoveTop(block, copy));
    }

    [Fact]
    public void Remove_BelowTheTrays_IsKeptOnTheScreen()
    {
        var block = new Rect(400, 1040, 800, 40);
        var copy = new Rect(400, 950, 800, 40);

        Assert.Equal(ScreenHeight - RemoveSize, RemoveTop(block, copy));
    }
}
