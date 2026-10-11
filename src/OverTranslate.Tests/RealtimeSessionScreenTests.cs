using System.Drawing;
using OverTranslate.Services.Realtime;
using Xunit;
using Screen = OverTranslate.Services.Realtime.RealtimeSessionScreen.Screen;

namespace OverTranslate.Tests;

/// <summary>
/// After the display changes under a running session, the bar is clamped into the session's screen
/// as it is now — the case it exists for: a game in fullscreen at 1280x720 on a 2560x1440 monitor,
/// which had left 結束 off the screen.
/// </summary>
public class RealtimeSessionScreenTests
{
    private static readonly Rectangle Started = new(0, 0, 2560, 1440);

    [Fact]
    public void The_same_monitor_is_found_at_its_new_size()
    {
        Screen[] now =
        [
            new(@"\\.\DISPLAY1", new Rectangle(-2560, 0, 2560, 1440)),
            new(@"\\.\DISPLAY2", new Rectangle(0, 0, 1280, 720)),
        ];

        Assert.Equal(new Rectangle(0, 0, 1280, 720), RealtimeSessionScreen.Resolve(@"\\.\DISPLAY2", Started, now));
    }

    [Fact]
    public void A_monitor_that_is_gone_falls_back_to_the_one_covering_most_of_where_it_was()
    {
        Screen[] now =
        [
            new(@"\\.\DISPLAY1", new Rectangle(-2560, 0, 2560, 1440)),
            new(@"\\.\DISPLAY3", new Rectangle(0, 0, 1920, 1080)),
        ];

        Assert.Equal(new Rectangle(0, 0, 1920, 1080), RealtimeSessionScreen.Resolve(@"\\.\DISPLAY2", Started, now));
    }

    [Fact]
    public void With_no_screens_at_all_the_old_rectangle_stands()
    {
        Assert.Equal(Started, RealtimeSessionScreen.Resolve(@"\\.\DISPLAY2", Started, []));
    }
}
