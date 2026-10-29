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
    private const double Room = 83.4; // Горизонтально, the longest name in any interface language

    [Fact]
    public void WithHorizontalChosenOnlyTheVerticalNameShows()
    {
        var layout = DirectionSwitchLayout.At(0, Room);

        Assert.Equal(0, layout.HorizontalNameWidth);
        Assert.Equal(Room, layout.VerticalNameWidth);
        Assert.Equal(0, layout.HorizontalNameOpacity);
        Assert.Equal(1, layout.VerticalNameOpacity);
        Assert.Equal(0, layout.ThumbLeft);
    }

    [Fact]
    public void WithVerticalChosenOnlyTheHorizontalNameShows()
    {
        var layout = DirectionSwitchLayout.At(1, Room);

        Assert.Equal(Room, layout.HorizontalNameWidth);
        Assert.Equal(0, layout.VerticalNameWidth);
        Assert.Equal(1, layout.HorizontalNameOpacity);
        Assert.Equal(0, layout.VerticalNameOpacity);
        // On the vertical half's mark, which starts after the horizontal mark and its name.
        Assert.Equal(Mark + Room, layout.ThumbLeft);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.13)]
    [InlineData(0.5)]
    [InlineData(0.87)]
    [InlineData(1)]
    public void TheSwitchKeepsOneWidthThroughoutItsTravel(double progress)
    {
        var layout = DirectionSwitchLayout.At(progress, Room);

        double width = Mark + layout.HorizontalNameWidth + Mark + layout.VerticalNameWidth;
        Assert.Equal(2 * Mark + Room, width, 6);
    }

    /// <summary>
    /// The pill is one mark wide and always sits on a mark, never across a name: at every point of
    /// the travel its left edge stays inside the span from the first mark to the second.
    /// </summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void ThePillTravelsBetweenTheTwoMarks(double progress)
    {
        var layout = DirectionSwitchLayout.At(progress, Room);

        Assert.InRange(layout.ThumbLeft, 0, Mark + Room);
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
        var layout = DirectionSwitchLayout.At(progress, Room);
        var end = DirectionSwitchLayout.At(progress < 0 ? 0 : 1, Room);

        Assert.Equal(end, layout);
    }
}
