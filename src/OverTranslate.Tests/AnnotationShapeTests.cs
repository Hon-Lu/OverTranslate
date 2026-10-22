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
    public void Arrow_HasAnOpenHeadAtTheEndTheDragFinishedOn()
    {
        OnStaThread(() =>
        {
            // 4px: the head is 4.4 × 4 = 17.6 long, each wing 38° off the shaft and stroked 5.2 wide.
            var surface = Laid(Shape(AnnotationTool.Arrow, 4, new Point(100, 150), new Point(300, 150)));

            Assert.True(InkAt(surface, 200, 150));    // the shaft
            Assert.True(InkAt(surface, 293, 144.6));  // halfway along the upper wing
            Assert.True(InkAt(surface, 293, 155.4));  // and the lower one
            Assert.False(InkAt(surface, 107, 144.6)); // no head at the start
            Assert.False(InkAt(surface, 284, 145.5)); // and the head is open, not filled in
        });
    }

    [Fact]
    public void ArrowBounds_TakeInTheHeadsReachPastTheShaft()
    {
        OnStaThread(() =>
        {
            var stroke = Shape(AnnotationTool.Arrow, 4, new Point(100, 150), new Point(300, 150));

            // A level shaft spans no height; the wings reach 17.6 × sin 38° ≈ 10.8 above and below,
            // and half of their 5.2 stroke past that.
            Assert.True(stroke.Bounds.Top < 150 - 10.8 - 2.6);
            Assert.True(stroke.Bounds.Bottom > 150 + 10.8 + 2.6);
        });
    }

    [Theory]
    [InlineData(2, 3.2)]    // the thinnest line: the head half as heavy again
    [InlineData(4, 5.2)]
    [InlineData(6, 6)]      // the default: head and shaft one width
    [InlineData(13, 13)]    // and so on up
    public void ArrowHead_IsHeavierOnlyOnLinesThinnerThanTheDefault(double thickness, double head)
    {
        var (_, weight) = AnnotationStroke.ArrowHead(new Point(0, 0), new Point(200, 0), thickness);
        Assert.Equal(head, weight, precision: 6);
    }

    [Fact]
    public void ShiftedArrow_SnapsLikeALine()
    {
        var start = new Point(50, 50);
        var raw = new Point(140, 160);

        Assert.Equal(
            AnnotationStroke.ConstrainShape(AnnotationTool.Line, start, raw),
            AnnotationStroke.ConstrainShape(AnnotationTool.Arrow, start, raw));
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
