using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OverTranslate.Services;
using OverTranslate.Views.Capture;

namespace LayoutProbe;

/// <summary>
/// Opens a real <see cref="ToolbarWindow"/> and reads back its two segmented controls.
/// </summary>
/// <remarks>
/// Both trays share their columns so that each half is the width of the longer label, which is what
/// lets the pill travel one fixed distance. Neither of those is a number written in the markup —
/// they fall out of the text in whatever language the interface is in — so the only way to know
/// they hold is to lay the window out and look.
/// </remarks>
internal static class ToolbarLayout
{
    public static void Report()
    {
        // Pressing a segment is how the toolbar saves the choice, and this probe presses two of
        // them. Without putting them back, running the probe would quietly change which mode the
        // user's next capture opens on — a measuring instrument that moves what it measures.
        var settings = SettingsService.Instance.Current.Capture;
        var storedMode = settings.LayoutMode;
        var storedVertical = settings.VerticalText;
        try
        {
            Measure();
        }
        finally
        {
            settings.LayoutMode = storedMode;
            settings.VerticalText = storedVertical;
            SettingsService.Instance.Save();
            Console.WriteLine();
            Console.WriteLine($"restored settings: mode={storedMode} vertical={storedVertical}");
        }
    }

    private static void Measure()
    {
        var toolbar = new ToolbarWindow(200, 200, 800, 400, "AUTO", "ZH-TW")
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            // Off-screen: this opens a real window, and a probe should not flash one over whatever
            // the user is doing.
            Left = -4000,
            Top = -4000,
        };

        toolbar.Show();
        toolbar.UpdateLayout();
        toolbar.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        toolbar.UpdateLayout();

        Console.WriteLine($"window   w={toolbar.ActualWidth:0.0} h={toolbar.ActualHeight:0.0}");
        Console.WriteLine($"opens on mode={toolbar.CurrentLayoutMode} vertical={toolbar.IsVerticalText}");
        Console.WriteLine("         (read from this machine's settings file)");

        Console.WriteLine();
        Console.WriteLine("segments");
        foreach (var name in new[]
                 { "GeneralModeSeg", "InterfaceModeSeg", "HorizontalSeg", "VerticalSeg" })
        {
            var segment = (RadioButton)toolbar.FindName(name)!;
            Console.WriteLine(
                $"  {name,-18} w={segment.ActualWidth,6:0.0} h={segment.ActualHeight,6:0.0} " +
                $"checked={segment.IsChecked == true,-5} tooltip={segment.ToolTip}");
        }

        Console.WriteLine();
        Console.WriteLine("pills   shiftX is where the pill sits now; one column's width is a full travel");
        Pill(toolbar, "LayoutModeThumb", "LayoutModeThumbShift");
        DirectionPill(toolbar);

        // The travel is only observable by moving it, and the pill animates — so read the target
        // the animation was given rather than a frame of it.
        Console.WriteLine();
        Console.WriteLine("after pressing the other half of each tray");
        Press(toolbar, toolbar.CurrentLayoutMode == OverTranslate.Services.Ocr.CaptureLayoutMode.Interface
            ? "GeneralModeSeg"
            : "InterfaceModeSeg");
        Press(toolbar, toolbar.IsVerticalText ? "HorizontalSeg" : "VerticalSeg");
        toolbar.UpdateLayout();
        Settle(toolbar);
        Console.WriteLine($"  mode={toolbar.CurrentLayoutMode} vertical={toolbar.IsVerticalText}");
        Pill(toolbar, "LayoutModeThumb", "LayoutModeThumbShift");
        DirectionPill(toolbar);

        toolbar.Close();
    }

    private static void Pill(ToolbarWindow toolbar, string thumb, string shift)
    {
        var border = (FrameworkElement)toolbar.FindName(thumb)!;
        var transform = (TranslateTransform)border.RenderTransform;
        Console.WriteLine($"  {thumb,-16} w={border.ActualWidth,6:0.0} shiftX={transform.X,6:0.0}");
    }

    /// <summary>
    /// The direction switch shares out a fixed width rather than two equal columns: the chosen half
    /// is a mark, the other a mark and its name, and the pill — one mark wide — sits on its left margin.
    /// </summary>
    private static void DirectionPill(ToolbarWindow toolbar)
    {
        var thumb = (FrameworkElement)toolbar.FindName("DirectionThumb")!;
        var tray = (FrameworkElement)toolbar.FindName("DirectionSwitch")!;
        var h = (FrameworkElement)toolbar.FindName("HorizontalSeg")!;
        var v = (FrameworkElement)toolbar.FindName("VerticalSeg")!;
        Console.WriteLine(
            $"  {"DirectionThumb",-16} w={thumb.ActualWidth,6:0.0} left={thumb.Margin.Left,6:0.0} " +
            $"tray={tray.ActualWidth:0.0} halves={h.ActualWidth:0.0}+{v.ActualWidth:0.0}");
    }

    private static void Press(ToolbarWindow toolbar, string segment) =>
        ((RadioButton)toolbar.FindName(segment)!).IsChecked = true;

    /// <summary>
    /// Lets the pill's animation finish, because until it does the transform still reads where the
    /// pill set off from.
    /// </summary>
    /// <remarks>
    /// The layout-mode travel is a 220ms eased animation (see ToolbarWindow.RenderLayoutModeThumb),
    /// the direction switch a spring that settles in well under a second (ToolbarWindow.Direction). Read
    /// immediately after the press, the transform reports 0 whichever half is now chosen, which
    /// looks exactly like a pill that never moved. Pumping the dispatcher for longer than the
    /// animation is the difference between measuring the travel and measuring the start of it.
    /// </remarks>
    private static void Settle(ToolbarWindow toolbar)
    {
        var until = DateTime.UtcNow.AddMilliseconds(900);
        while (DateTime.UtcNow < until)
        {
            toolbar.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(20);
        }
    }
}
