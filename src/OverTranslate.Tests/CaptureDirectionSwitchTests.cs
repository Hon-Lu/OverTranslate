using OverTranslate.Views.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The 橫排／直排 switch on the capture toolbar: the chosen half shows only its mark, the other half
/// its name, and the switch keeps one width throughout.
/// </summary>
/// <remarks>
/// The width matters because the bar sizes to its content and is centred on the selection: a switch
/// that changed width as it was pressed would move the whole bar under the pointer.
/// </remarks>
public class CaptureDirectionSwitchTests
{
    private const double Mark = ToolbarWindow.DirectionMarkWidth;

    // Горизонтально and Вертикально, the pair furthest apart in any interface language.
    private const double Long = 83.4;
    private const double Short = 72.1;

    [Fact]
    public void WithHorizontalChosenOnlyTheVerticalNameShows()
    {
        var layout = DirectionSwitchLayout.At(0, Long, Short);

        Assert.Equal(0, layout.HorizontalNameWidth);
        Assert.Equal(Short, layout.VerticalNameWidth);
        Assert.Equal(0, layout.HorizontalNameOpacity);
        Assert.Equal(1, layout.VerticalNameOpacity);
        Assert.Equal(0, layout.ThumbLeft);
    }

    [Fact]
    public void WithVerticalChosenOnlyTheHorizontalNameShows()
    {
        var layout = DirectionSwitchLayout.At(1, Long, Short);

        Assert.Equal(Long, layout.HorizontalNameWidth);
        Assert.Equal(0, layout.VerticalNameWidth);
        Assert.Equal(1, layout.HorizontalNameOpacity);
        Assert.Equal(0, layout.VerticalNameOpacity);
        // On the vertical half's mark, which starts after the horizontal mark and its name.
        Assert.Equal(Mark + Long, layout.ThumbLeft);
    }

    /// <summary>
    /// The half that is not chosen is its mark and its own name, nothing more: the difference between
    /// the two names goes to the chosen half and its pill, not into a gap after the shorter name.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TheShorterNameLeavesNoGapAfterIt(double progress)
    {
        var layout = DirectionSwitchLayout.At(progress, Long, Short);

        if (progress == 0)
        {
            Assert.Equal(Mark, layout.VerticalMarkWidth);
            Assert.Equal(Mark + Long - Short, layout.HorizontalMarkWidth);
            Assert.Equal(layout.HorizontalMarkWidth, layout.ThumbWidth);
        }
        else
        {
            Assert.Equal(Mark, layout.HorizontalMarkWidth);
            Assert.Equal(Mark, layout.VerticalMarkWidth);
            Assert.Equal(Mark, layout.ThumbWidth);
        }
    }

    [Fact]
    public void NamesOfOneWidthLeaveThePillOneMarkWide()
    {
        foreach (var progress in new[] { 0.0, 0.5, 1.0 })
            Assert.Equal(Mark, DirectionSwitchLayout.At(progress, Long, Long).ThumbWidth, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.13)]
    [InlineData(0.5)]
    [InlineData(0.87)]
    [InlineData(1)]
    public void TheSwitchKeepsOneWidthThroughoutItsTravel(double progress)
    {
        var layout = DirectionSwitchLayout.At(progress, Long, Short);

        double width = layout.HorizontalMarkWidth + layout.HorizontalNameWidth +
                       layout.VerticalMarkWidth + layout.VerticalNameWidth;
        Assert.Equal(2 * Mark + Long, width, 6);
    }

    /// <summary>
    /// The pill always sits on a mark cell, never across a name: at every point of the travel it
    /// stays inside the tray.
    /// </summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void ThePillStaysInsideTheTray(double progress)
    {
        var layout = DirectionSwitchLayout.At(progress, Long, Short);

        Assert.InRange(layout.ThumbLeft, 0, Mark + Long);
        Assert.InRange(layout.ThumbLeft + layout.ThumbWidth, 0, 2 * Mark + Long);
    }

    /// <summary>
    /// A spring that is pushed past its target, or a progress read before it settles, must not draw
    /// a name wider than its room or the pill outside the tray.
    /// </summary>
    [Theory]
    [InlineData(-0.2)]
    [InlineData(1.2)]
    public void ProgressOutsideTheTravelIsHeldAtItsEnds(double progress)
    {
        var layout = DirectionSwitchLayout.At(progress, Long, Short);
        var end = DirectionSwitchLayout.At(progress < 0 ? 0 : 1, Long, Short);

        Assert.Equal(end, layout);
    }
}
