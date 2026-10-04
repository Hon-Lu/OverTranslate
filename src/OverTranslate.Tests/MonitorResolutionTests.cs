using System.Drawing;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Turning the one screen a realtime session runs on into the monitor its capture attaches to.
/// </summary>
/// <remarks>
/// Almost everything about monitor capture needs a compositor and is measured with
/// <c>WgcProbe exclusion</c> instead. This part does not: it is a rectangle going in and a handle
/// coming out, and it is where a multi-screen desktop breaks things quietly — a display to the left
/// of the primary has negative coordinates, and a remembered layout can name a screen that is no
/// longer arranged the way it was.
/// </remarks>
public class MonitorResolutionTests
{
    private static Rectangle Primary =>
        System.Windows.Forms.Screen.PrimaryScreen is { } screen
            ? screen.Bounds
            : new Rectangle(0, 0, 1920, 1080);

    [Fact]
    public void AScreenResolvesToAMonitor()
    {
        Assert.NotEqual(IntPtr.Zero, WgcMonitorCaptureBackend.MonitorFor(Primary));
    }

    [Fact]
    public void ARectangleInsideAScreenIsOnThatScreensMonitor()
    {
        // What a session actually asks with: not the screen itself but a block drawn on it.
        var block = new Rectangle(Primary.X + 100, Primary.Y + 100, 200, 80);

        Assert.Equal(
            WgcMonitorCaptureBackend.MonitorFor(Primary),
            WgcMonitorCaptureBackend.MonitorFor(block));
    }

    [Fact]
    public void ARectangleOffEveryScreenStillResolvesToTheNearestMonitor()
    {
        // The remembered-layout case: the screen this was saved against has been unplugged or moved,
        // and refusing to start would be a worse answer than attaching to the display next to it.
        var stranded = new Rectangle(-100_000, -100_000, 800, 600);

        Assert.NotEqual(IntPtr.Zero, WgcMonitorCaptureBackend.MonitorFor(stranded));
    }
}
