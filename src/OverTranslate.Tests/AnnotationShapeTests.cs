using System.Windows;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Views.Overlay;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The 標記 shapes: that Shift squares them up the way it promises, and that what is laid into the
/// picture is the outline and only the outline.
/// </summary>
/// <remarks>
/// Run on an STA thread where a stroke is rasterised, for the same reason as
/// <see cref="AnnotationEraserTests"/>.
/// </remarks>
public class AnnotationShapeTests
{
    private static AnnotationStroke Shape(AnnotationTool tool, double thickness, Point from, Point to) =>
        new()
        {
            Tool      = tool,
            Color     = Colors.Orange,
            Thickness = thickness,
            Opacity   = 1,
            Points    = [from, to],
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

    private static InkSurface Laid(AnnotationStroke stroke)
    {
        var surface = new InkSurface();
        surface.Ensure(new Rect(0, 0, 400, 300), scale: 1);
        surface.Lay(stroke);
        return surface;
    }

    private static bool InkAt(InkSurface surface, double x, double y) =>
        surface.InkAt(new Point(x, y)) > 0.5;

    [Theory]
    [InlineData(100, 10, 100, 0)]    // nearly flat: snaps level
    [InlineData(10, 100, 0, 100)]    // nearly upright: snaps vertical
    [InlineData(90, 110, 100, 100)]  // nearly diagonal: snaps to 45°
    [InlineData(-90, -110, -100, -100)]
    public void ShiftedLine_SnapsToTheNearest45Degrees(double dx, double dy, double expectX, double expectY)
    {
        var start = new Point(50, 50);
        var end = AnnotationStroke.ConstrainShape(AnnotationTool.Line, start, new Point(50 + dx, 50 + dy));

        // The length is kept, so compare directions rather than exact ends.
        double angle  = Math.Atan2(end.Y - start.Y, end.X - start.X);
        double wanted = Math.Atan2(expectY, expectX);
        Assert.Equal(wanted, angle, precision: 6);
        Assert.Equal(Math.Sqrt(dx * dx + dy * dy), (end - start).Length, precision: 6);
    }

    [Theory]
    [InlineData(AnnotationTool.Rectangle, 80, 30, 80, 80)]
    [InlineData(AnnotationTool.Ellipse, -20, 60, -60, 60)]
    [InlineData(AnnotationTool.Rectangle, -50, -10, -50, -50)]
    public void ShiftedBox_TakesTheLongerSideTowardsThePointer(
        AnnotationTool tool, double dx, double dy, double expectDx, double expectDy)
    {
        var start = new Point(100, 100);
        var end = AnnotationStroke.ConstrainShape(tool, start, new Point(100 + dx, 100 + dy));

        Assert.Equal(100 + expectDx, end.X, precision: 6);
        Assert.Equal(100 + expectDy, end.Y, precision: 6);
    }

    [Fact]
    public void Rectangle_IsAnOutlineWithSquareCorners()
    {
        OnStaThread(() =>
        {
            var surface = Laid(Shape(AnnotationTool.Rectangle, 6, new Point(100, 100), new Point(300, 200)));

            Assert.True(InkAt(surface, 200, 100));   // top edge
            Assert.True(InkAt(surface, 300, 150));   // right edge
            Assert.True(InkAt(surface, 98, 98));     // the corner is filled out, not rounded off
            Assert.False(InkAt(surface, 200, 150));  // hollow
            Assert.False(InkAt(surface, 200, 90));   // and nothing past the stroke
        });
    }

    [Fact]
    public void Ellipse_IsTheOutlineInscribedInTheDrag()
    {
        OnStaThread(() =>
        {
            var surface = Laid(Shape(AnnotationTool.Ellipse, 6, new Point(100, 100), new Point(300, 200)));

            Assert.True(InkAt(surface, 200, 100));   // top of the ellipse
            Assert.True(InkAt(surface, 100, 150));   // left of it
            Assert.False(InkAt(surface, 100, 100));  // the box's corner is outside the ellipse
            Assert.False(InkAt(surface, 200, 150));  // hollow
        });
    }

    [Fact]
    public void Line_IsDrawnBetweenTheTwoEndsOnly()
    {
        OnStaThread(() =>
        {
            var surface = Laid(Shape(AnnotationTool.Line, 6, new Point(100, 100), new Point(300, 200)));

            Assert.True(InkAt(surface, 200, 150));   // midpoint
            Assert.False(InkAt(surface, 300, 100));  // not a box
            Assert.False(InkAt(surface, 100, 200));
        });
    }

    [Fact]
    public void ShapeBounds_CoverTheWholeOutline()
    {
        var stroke = Shape(AnnotationTool.Rectangle, 10, new Point(300, 200), new Point(100, 100));

        // Dragged up and to the left: the box is the same one, whichever corner it started from.
        Assert.True(stroke.Bounds.Contains(new Rect(95, 95, 210, 110)));
    }

    [Fact]
    public void RectangleReach_FollowsTheEdgesNotTheDiagonal()
    {
        OnStaThread(() =>
        {
            var stroke = Shape(AnnotationTool.Rectangle, 4, new Point(0, 0), new Point(100, 100));

            Assert.True(stroke.IsWithin(new Point(50, 0), radius: 1));
            Assert.False(stroke.IsWithin(new Point(50, 50), radius: 1));
        });
    }
}
