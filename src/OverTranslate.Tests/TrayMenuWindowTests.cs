using Xunit;
using OverTranslate.Layout;
using OverTranslate.Views.Shell;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace OverTranslate.Tests;

/// <summary>
/// The tray menu behaves like a context menu even though it is implemented as a WPF window.
/// </summary>
public class TrayMenuWindowTests
{
    private const double ShadowInset = 18;

    private static string Source(string file) => File.ReadAllText(Path.Combine(
        StringsParityTests.ProjectDirectory(), "Views", "Shell", file));

    [Fact]
    public void The_menu_uses_the_pointer_monitor_and_physical_pixels()
    {
        var source = Source("TrayMenuWindow.xaml.cs");

        Assert.Contains("Screen.FromPoint", source);
        Assert.Contains("ScreenGeometry.ScaleAt", source);
        Assert.Contains("ScreenGeometry.MoveToPhysical", source);
        Assert.DoesNotContain("SystemParameters.WorkArea", source);
    }

    /// <summary>
    /// The menu is measured against the whole monitor, never against the work area.
    /// </summary>
    /// <remarks>
    /// This is the whole of the bug it replaced, and it is one property long, so it is worth a test
    /// of its own. The work area is the screen minus the taskbar; the tray icon is on the taskbar.
    /// Measured against the work area the menu physically cannot reach the icon that opened it — it
    /// stops at the taskbar edge and floats there. Only an icon inside the overflow flyout, which
    /// does sit in the work area, looked right.
    /// </remarks>
    [Fact]
    public void The_menu_is_kept_on_the_monitor_and_not_pushed_off_the_taskbar()
    {
        var source = Source("TrayMenuWindow.xaml.cs");

        Assert.Contains("Screen.FromPoint(_cursorPhys).Bounds", source);
        Assert.DoesNotContain("WorkingArea", source);
    }

    [Fact]
    public void Showing_the_menu_activates_it_so_clicking_elsewhere_deactivates_and_closes_it()
    {
        var source = Source("TrayMenuWindow.xaml.cs");

        Assert.Contains("Activate();", source);
        Assert.Contains("Deactivated", source);
    }

    [Fact]
    public void Clicking_the_menu_chrome_dismisses_it_without_treating_a_button_as_chrome()
    {
        var source = Source("TrayMenuWindow.xaml.cs");

        Assert.Contains("PreviewMouseDown", source);
        Assert.Contains("DismissOnNonButtonClick", source);
        Assert.Contains("IsInsideButton", source);
    }

    [Fact]
    public void A_button_and_its_content_are_not_menu_chrome()
    {
        OnUiThread(() =>
        {
            var label = new TextBlock();
            var button = new Button { Content = label };

            Assert.True(TrayMenuWindow.IsInsideButton(button));
            Assert.True(TrayMenuWindow.IsInsideButton(label));
            Assert.False(TrayMenuWindow.IsInsideButton(new Border()));
        });
    }

    /// <remarks>
    /// A tray icon on a left taskbar is at x=20, and the menu has to be able to start there. It
    /// leans over the taskbar by an icon width and spends the rest of itself on the screen, which is
    /// the same trade the bottom taskbar gets.
    /// </remarks>
    [Fact]
    public void A_left_taskbar_does_not_push_the_menu_away_from_the_pointer()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var pointer = new Point(20, 600);
        var window = new Size(240, 180);

        var (left, top) = TrayMenuPlacement.Place(pointer, window, monitor, 4, ShadowInset);

        Assert.Equal(pointer.X, left + ShadowInset);
        Assert.Equal(pointer.Y, top + window.Height - ShadowInset);
    }

    [Fact]
    public void A_top_taskbar_hangs_the_menu_below_the_pointer_because_there_is_no_room_above()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var pointer = new Point(900, 20);
        var window = new Size(240, 180);

        var (left, top) = TrayMenuPlacement.Place(pointer, window, monitor, 4, ShadowInset);

        Assert.Equal(pointer.X, left + ShadowInset);
        Assert.Equal(pointer.Y, top + ShadowInset);
    }

    [Fact]
    public void A_right_taskbar_uses_the_visible_bottom_right_corner()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var window = new Size(240, 180);
        var pointer = new Point(1900, 600);

        var (left, top) = TrayMenuPlacement.Place(pointer, window, monitor, 4, ShadowInset);

        Assert.Equal(pointer.X, left + window.Width - ShadowInset);
        Assert.Equal(pointer.Y, top + window.Height - ShadowInset);
    }

    [Fact]
    public void A_pointer_on_a_negative_coordinate_monitor_stays_on_that_monitor()
    {
        var monitor = new Rect(-1920, 0, 1920, 1080);
        var window = new Size(240, 180);
        var pointer = new Point(-10, 1060);

        var (left, top) = TrayMenuPlacement.Place(pointer, window, monitor, 4, ShadowInset);

        Assert.Equal(pointer.X, left + window.Width - ShadowInset);
        Assert.Equal(pointer.Y, top + window.Height - ShadowInset);
        Assert.True(left + ShadowInset >= monitor.Left, "the menu never leaves the monitor it was opened on");
    }

    /// <summary>
    /// The reported case: an icon dragged out of the overflow onto the taskbar itself, so the
    /// pointer is inside the taskbar band rather than above it.
    /// </summary>
    /// <remarks>
    /// The menu leans over the taskbar to get there, which it is allowed to do — it is topmost, so
    /// it is drawn above the taskbar rather than clipped by it, and it is gone on the next click.
    /// </remarks>
    [Fact]
    public void A_pointer_inside_the_taskbar_still_gets_the_corner_put_on_it()
    {
        var monitor = new Rect(0, 0, 1920, 1080);      // taskbar occupies 1040..1080
        var window = new Size(276, 216);
        var pointer = new Point(400, 1060);

        var (left, top) = TrayMenuPlacement.Place(pointer, window, monitor, 4, ShadowInset);

        Assert.Equal(pointer.X, left + ShadowInset);
        Assert.Equal(pointer.Y, top + window.Height - ShadowInset);
    }

    /// <remarks>
    /// The one thing the clamp is still for: a pointer at the very bottom of the screen would put
    /// the menu edge off the monitor, and that is where it stops.
    /// </remarks>
    [Fact]
    public void A_pointer_at_the_very_bottom_edge_keeps_the_menu_on_screen()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var window = new Size(276, 216);

        var (_, top) = TrayMenuPlacement.Place(
            new Point(400, 1079), window, monitor, 4, ShadowInset);

        Assert.Equal(monitor.Bottom - 4, top + window.Height - ShadowInset);
    }

    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
