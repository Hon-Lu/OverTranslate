using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OverTranslate.Models;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// A kept framing drawn small: the screen or window it belongs to as an outline at its own
/// proportions, and each block as a filled box where it sits.
/// </summary>
/// <remarks>
/// <para>What tells two presets of one target apart when the user has not named them — their
/// automatic names differ only by （2）, （3） — and what shows at a glance which part of the screen a
/// named one covers. Only the boxes are drawn: no picture of the screen is kept, so there is nothing
/// private in the settings file or on the page.</para>
///
/// <para>Drawn from the stored blocks, which are relative to the screen's or the window's top-left,
/// against the size they were saved for. A block a hand-edited file put outside that size is cut at
/// the outline rather than drawn over the neighbouring row.</para>
/// </remarks>
internal static class RealtimeFramingThumbnail
{
    // The widest a thumbnail is allowed relative to its height: past this an ultra-wide window
    // would take the row's room from its name.
    private const double MaxAspect = 2.4;

    // The narrowest: a tall portrait window still gets a box worth looking at.
    private const double MinAspect = 0.6;

    /// <summary>A thumbnail <paramref name="height"/> DIP tall for <paramref name="preset"/>.</summary>
    public static FrameworkElement Create(RealtimeFramingPreset preset, double height)
    {
        var area = preset.IsForWindow ? preset.WindowSize : preset.Screen;
        double areaWidth = Math.Max(1, area?.Width ?? 1);
        double areaHeight = Math.Max(1, area?.Height ?? 1);
        double width = Math.Round(height * Math.Clamp(areaWidth / areaHeight, MinAspect, MaxAspect));

        // Inside the 1 px outline.
        double innerWidth = width - 2;
        double innerHeight = height - 2;
        double scaleX = innerWidth / areaWidth;
        double scaleY = innerHeight / areaHeight;

        var canvas = new Canvas { Width = innerWidth, Height = innerHeight, ClipToBounds = true };
        foreach (var block in preset.Blocks ?? [])
        {
            if (block is null) continue;

            // At least a pixel and a half either way, so a thin subtitle strip still shows.
            double left = Math.Clamp(block.X * scaleX, 0, innerWidth);
            double top = Math.Clamp(block.Y * scaleY, 0, innerHeight);
            double boxWidth = Math.Max(1.5, block.Width * scaleX);
            double boxHeight = Math.Max(1.5, block.Height * scaleY);

            var box = new Rectangle
            {
                Width = boxWidth,
                Height = boxHeight,
                StrokeThickness = 0,
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
            box.SetResourceReference(Shape.FillProperty, "AppAccent");
            Canvas.SetLeft(box, left);
            Canvas.SetTop(box, top);
            canvas.Children.Add(box);
        }

        var frame = new Border
        {
            Width = width,
            Height = height,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = canvas,
            SnapsToDevicePixels = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "AppTextSecondary");
        frame.SetResourceReference(Border.BackgroundProperty, "AppAccentSubtle");
        return frame;
    }
}
