using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Views.Overlay;
using Xunit;

namespace OverTranslate.Tests;

public class OverlayScreenshotExportTests
{
    [Fact]
    public void ExportOmitsDebugBoxesButKeepsBubblesAndUserAnnotations() => OnSta(() =>
    {
        var window = new OverlayWindow([], [], 0, 0, 100, 100, "EN", "ZH-HANT", false);
        try
        {
            var type = typeof(OverlayWindow);
            type.GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var screen = (System.Drawing.Rectangle)type.GetField("_physBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Canvas Layer(string name) => (Canvas)window.FindName(name);
            void Prepare(Canvas layer)
            {
                layer.Measure(new Size(100, 100));
                layer.Arrange(new Rect(0, 0, 100, 100));
                layer.UpdateLayout();
            }
            void Add(Canvas layer, Brush color, int x)
            {
                var rectangle = new System.Windows.Shapes.Rectangle { Width = 20, Height = 20, Fill = color };
                Canvas.SetLeft(rectangle, x);
                Canvas.SetTop(rectangle, 10);
                layer.Children.Add(rectangle);
                Prepare(layer);
            }
            var debug = Layer("DebugCanvas");
            Add(debug, Brushes.Blue, 10);
            Assert.Null(window.RenderOverlayForSelection(screen.Left, screen.Top, 100, 100));

            Add(Layer("BubbleBackgroundCanvas"), Brushes.Red, 10);
            Prepare(Layer("BubbleTextCanvas"));
            Add(Layer("AnnotationCanvas"), Brushes.Lime, 40);
            var bitmap = window.RenderOverlayForSelection(screen.Left, screen.Top, 100, 100);
            Assert.NotNull(bitmap);
            var pixels = new byte[100 * 100 * 4];
            bitmap.CopyPixels(pixels, 100 * 4, 0);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.Skip((15 * 100 + 15) * 4).Take(4).ToArray());
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels.Skip((15 * 100 + 45) * 4).Take(4).ToArray());
            Assert.Equal(Visibility.Visible, debug.Visibility);
            Assert.Single(debug.Children.Cast<UIElement>());
        }
        finally { window.Close(); }
    });

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
