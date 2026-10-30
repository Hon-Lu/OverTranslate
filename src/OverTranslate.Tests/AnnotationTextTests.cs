using System.Windows;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Views.Overlay;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A 文字 mark (#362) once it has been typed: that it lands in the picture where the box was, at the
/// size asked for, and is then a mark like any other — inside its bounds, and rubbed out by the eraser.
/// </summary>
/// <remarks>
/// On an STA thread for the same reason as <see cref="AnnotationEraserTests"/>: laying a mark down
/// rasterises it.
/// </remarks>
public class AnnotationTextTests
{
    private static readonly Rect Surface = new(0, 0, 600, 300);

    private static AnnotationStroke Text(string text, double x, double y, double size,
        bool bold = false, bool italic = false, bool underline = false, bool strikethrough = false) =>
        new()
        {
            Tool      = AnnotationTool.Text,
            Color     = Colors.Red,
            Thickness = 0,
            Opacity   = 1,
            Points    = [new Point(x, y)],
            Text      = text,
            TextStyle = new AnnotationTextStyle(
                new FontFamily("Segoe UI"), size, bold, italic, underline, strikethrough),
        };

    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = e; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>Whether any ink shows inside the box, scanning every pixel of it.</summary>
    private static bool AnyInk(InkSurface surface, Rect box)
    {
        for (double y = box.Top; y < box.Bottom; y++)
            for (double x = box.Left; x < box.Right; x++)
                if (surface.InkAt(new Point(x, y)) > 0.3) return true;
        return false;
    }

    private static InkSurface Laid(AnnotationStroke stroke, double scale = 1)
    {
        var surface = new InkSurface();
        surface.Ensure(Surface, scale);
        surface.Lay(stroke);
        return surface;
    }

    [Fact]
    public void TypedWords_LandToTheRightOfAndBelowWhereTheBoxWas()
    {
        OnStaThread(() =>
        {
            var surface = Laid(Text("HELLO", 100, 100, 40));

            Assert.True(AnyInk(surface, new Rect(100, 100, 120, 40)));
            Assert.False(AnyInk(surface, new Rect(0, 0, 95, 300)));
            Assert.False(AnyInk(surface, new Rect(0, 0, 600, 95)));
            Assert.False(AnyInk(surface, new Rect(0, 150, 600, 150)));
        });
    }

    /// <summary>The bounds are what the picture, the mask and the history lean on, so they must hold all the ink.</summary>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, true)]
    public void TheWordsBounds_HoldEveryPixelOfInk(bool bold, bool italic, bool underline, bool strikethrough)
    {
        OnStaThread(() =>
        {
            var stroke = Text("Wjg 文字テスト\nsecond line", 80, 60, 36, bold, italic, underline, strikethrough);
            var surface = Laid(stroke);
            var bounds = stroke.Bounds;

            Assert.True(AnyInk(surface, bounds));
            Assert.False(AnyInk(surface, new Rect(0, 0, bounds.Left, 300)));
            Assert.False(AnyInk(surface, new Rect(bounds.Right, 0, 600 - bounds.Right, 300)));
            Assert.False(AnyInk(surface, new Rect(0, 0, 600, bounds.Top)));
            Assert.False(AnyInk(surface, new Rect(0, bounds.Bottom, 600, 300 - bounds.Bottom)));
        });
    }

    [Fact]
    public void ANewLine_StartsASecondLineBelowTheFirst()
    {
        OnStaThread(() =>
        {
            var one = Text("AAAA", 50, 50, 30);
            var two = Text("AAAA\nAAAA", 50, 50, 30);

            Assert.True(two.Bounds.Height > one.Bounds.Height * 1.5);
            Assert.True(AnyInk(Laid(two), new Rect(50, 50 + 30 * 1.2, 80, 30)));
        });
    }

    /// <summary>
    /// The size is set in the overlay's DIP and drawn at the picture's own scale, so at 150% the
    /// words are half as tall again in pixels — the size the user asked for in pixels, divided by the
    /// scale on the way in, comes back out.
    /// </summary>
    [Fact]
    public void AtAHigherScale_TheWordsAreAsTallInPixelsAsAsked()
    {
        OnStaThread(() =>
        {
            const double px = 60;
            const double scale = 1.5;
            var surface = Laid(Text("H", 10, 10, px / scale), scale);

            // Glyph height in DIP, measured, then turned back into pixels.
            double top = double.NaN, bottom = double.NaN;
            for (double y = 0; y < 200; y += 0.5)
            {
                bool ink = AnyInk(surface, new Rect(10, y, 60, 0.5));
                if (ink && double.IsNaN(top)) top = y;
                if (ink) bottom = y;
            }

            // An H's cap height is about 0.7 of the em in Segoe UI.
            double capPx = (bottom - top) * scale;
            Assert.InRange(capPx, px * 0.6, px * 0.8);
        });
    }

    [Fact]
    public void TheEraser_RubsOutWordsLikeAnyOtherMark()
    {
        OnStaThread(() =>
        {
            var stroke = Text("IIIIIIII", 100, 100, 40, bold: true);
            var surface = Laid(stroke);
            var bounds = stroke.Bounds;

            surface.Erase(new Point(bounds.Left, bounds.Top + bounds.Height / 2),
                          new Point(bounds.Right, bounds.Top + bounds.Height / 2), bounds.Height);

            Assert.False(AnyInk(surface, bounds));
        });
    }

    [Fact]
    public void ReplayingTheHistory_PaintsWordsBackInPlace()
    {
        OnStaThread(() =>
        {
            var stroke = Text("HELLO", 100, 100, 40);
            var surface = new InkSurface();
            surface.Ensure(Surface, 1);

            surface.Replay([stroke]);
            Assert.True(AnyInk(surface, new Rect(100, 100, 120, 40)));

            surface.Replay([]);
            Assert.False(AnyInk(surface, stroke.Bounds));
        });
    }
}
