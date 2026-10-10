using System.Drawing;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Which windows above the translated one take its overlays off the screen, and which overlays go.
/// The case this exists for: one game's subtitles left floating over a second game the user had
/// switched to, re-asserted on top every second.
/// </summary>
public class SourceWindowCoverageTests
{
    private const uint Own = 100;
    private const uint Source = 200;
    private const uint Other = 300;

    private static SourceWindowCoverage.WindowFacts Window(
        uint process = Other, bool visible = true, bool minimized = false, bool cloaked = false,
        bool clickThrough = false, string className = "UnrealWindow", Rectangle? bounds = null) =>
        new(process, visible, minimized, cloaked, clickThrough, className,
            bounds ?? new Rectangle(0, 0, 1920, 1080));

    [Fact]
    public void Another_applications_window_covers()
    {
        Assert.True(SourceWindowCoverage.CountsAsCover(Window(), Own, Source));
    }

    // The overlays, the control bar and anything opened from it are above the source on purpose.
    [Fact]
    public void Our_own_windows_never_cover()
    {
        Assert.False(SourceWindowCoverage.CountsAsCover(Window(process: Own), Own, Source));
    }

    // A game's own popups and child windows are part of what is being watched.
    [Fact]
    public void The_sources_own_windows_never_cover()
    {
        Assert.False(SourceWindowCoverage.CountsAsCover(Window(process: Source), Own, Source));
    }

    [Theory]
    [InlineData(false, false, false, false)] // hidden
    [InlineData(true, true, false, false)]   // minimised
    [InlineData(true, false, true, false)]   // cloaked: another virtual desktop, a suspended app
    [InlineData(true, false, false, true)]   // click-through: someone else's overlay
    public void Windows_the_user_cannot_see_or_click_do_not_cover(
        bool visible, bool minimized, bool cloaked, bool clickThrough)
    {
        var window = Window(visible: visible, minimized: minimized, cloaked: cloaked, clickThrough: clickThrough);

        Assert.False(SourceWindowCoverage.CountsAsCover(window, Own, Source));
    }

    [Fact]
    public void The_taskbar_does_not_cover()
    {
        Assert.False(SourceWindowCoverage.CountsAsCover(Window(className: "Shell_TrayWnd"), Own, Source));
    }

    [Fact]
    public void A_minimised_source_takes_every_overlay_away()
    {
        var view = new SourceWindowCoverage.View(Hidden: true, Covers: []);

        Assert.False(SourceWindowCoverage.LeavesVisible(view, new Rectangle(100, 100, 400, 80)));
    }

    // Two monitors: the other game is on the other screen, the translated one is in full view.
    [Fact]
    public void A_window_elsewhere_leaves_the_overlay_alone()
    {
        var view = new SourceWindowCoverage.View(false, [new Rectangle(-2560, 0, 2560, 1440)]);

        Assert.True(SourceWindowCoverage.LeavesVisible(view, new Rectangle(100, 900, 800, 120)));
    }

    [Fact]
    public void Only_the_overlays_under_a_window_go()
    {
        var view = new SourceWindowCoverage.View(false, [new Rectangle(0, 0, 600, 400)]);

        Assert.False(SourceWindowCoverage.LeavesVisible(view, new Rectangle(100, 100, 400, 80)));
        Assert.True(SourceWindowCoverage.LeavesVisible(view, new Rectangle(100, 900, 800, 120)));
    }
}
