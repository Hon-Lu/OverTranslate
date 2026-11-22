using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Where the 顯示外觀 panel opens against the bar: never over it, whichever edge of the screen the
/// bar has been moved to. A screen 0–1440 tall, a bar 52 tall, a 6 px gap.
/// </summary>
public class RealtimeAppearancePanelPlacementTests
{
    private const int Gap = 6;

    private static (bool Up, int Top) Place(int barTop, int height, bool? decided = null) =>
        RealtimeAppearancePanel.PlaceVertically(barTop, barTop + 52, 0, 1440, height, Gap, decided);

    [Fact]
    public void OpensAboveWhenItFits()
    {
        var (up, top) = Place(barTop: 1200, height: 800);
        Assert.True(up);
        Assert.Equal(1200 - Gap - 800, top);
    }

    [Fact]
    public void OpensBelowWhenOnlyBelowFits()
    {
        var (up, top) = Place(barTop: 300, height: 800);
        Assert.False(up);
        Assert.Equal(300 + 52 + Gap, top);
    }

    // The bug: a bar moved up the screen, room above for the panel but not for the window that holds
    // a picker too. The window was pushed down onto the screen, and the panel at its bottom sat on the bar.
    [Theory]
    [InlineData(900)]
    [InlineData(700)]
    public void NeverLiesOverTheBar(int barTop)
    {
        var (up, top) = Place(barTop, height: 1040);
        if (up) Assert.Equal(barTop - Gap, top + 1040);
        else Assert.Equal(barTop + 52 + Gap, top);
    }

    [Fact]
    public void KeepsTheSideItFirstChose()
    {
        var (up, _) = Place(barTop: 300, height: 800, decided: true);
        Assert.True(up);
    }
}
