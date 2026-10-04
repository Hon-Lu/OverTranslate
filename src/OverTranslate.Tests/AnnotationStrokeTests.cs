using System.Windows;
using System.Windows.Media;
using OverTranslate.Models;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What the 標記 eraser can reach. The rest of the feature is a window and is checked by eye; this
/// is the one part with arithmetic in it that can be silently wrong.
/// </summary>
public class AnnotationStrokeTests
{
    private static AnnotationStroke Stroke(double thickness, params (double X, double Y)[] points) =>
        new()
        {
            Tool      = AnnotationTool.Pen,
            Color     = Colors.Orange,
            Thickness = thickness,
            Opacity   = 1,
            Points    = [.. points.Select(p => new Point(p.X, p.Y))],
        };

    [Fact]
    public void PointerOnTheStroke_Hits()
    {
        var stroke = Stroke(4, (0, 0), (100, 0));
        Assert.True(stroke.IsWithin(new Point(50, 0), radius: 1));
    }

    [Fact]
    public void PointerWellClearOfTheStroke_Misses()
    {
        var stroke = Stroke(4, (0, 0), (100, 0));
        Assert.False(stroke.IsWithin(new Point(50, 40), radius: 6));
    }

    /// <summary>
    /// The reason the hit test walks segments rather than recorded points: a fast stroke is a handful
    /// of points a long way apart, and most of the line the user can see is between them.
    /// </summary>
    [Fact]
    public void PointerBetweenTwoDistantRecordedPoints_Hits()
    {
        var stroke = Stroke(2, (0, 0), (400, 0));
        Assert.True(stroke.IsWithin(new Point(200, 0), radius: 3));
    }

    /// <summary>A thick stroke is easier to hit, because the user is aiming at what they can see.</summary>
    [Fact]
    public void ThicknessWidensTheReach()
    {
        var target = new Point(50, 9);
        Assert.False(Stroke(4,  (0, 0), (100, 0)).IsWithin(target, radius: 5));
        Assert.True(Stroke(10, (0, 0), (100, 0)).IsWithin(target, radius: 5));
    }

    /// <summary>Past the end of a segment the distance is to its endpoint, not to its infinite line.</summary>
    [Fact]
    public void PointerBeyondTheEnd_MeasuresFromTheEndpoint()
    {
        var stroke = Stroke(2, (0, 0), (100, 0));
        Assert.True(stroke.IsWithin(new Point(103, 0), radius: 5));
        Assert.False(stroke.IsWithin(new Point(130, 0), radius: 5));
    }

    /// <summary>A tap is one point and still has to be erasable.</summary>
    [Fact]
    public void SinglePointStroke_IsHitTestable()
    {
        var stroke = Stroke(6, (20, 20));
        Assert.True(stroke.IsWithin(new Point(22, 20), radius: 2));
        Assert.False(stroke.IsWithin(new Point(60, 20), radius: 2));
    }

    /// <summary>
    /// Two pointer events at the same spot make a zero-length segment, which has no direction to
    /// project onto. It must not divide by zero, and it must still be reachable.
    /// </summary>
    [Fact]
    public void RepeatedPoint_DoesNotBreakTheProjection()
    {
        var stroke = Stroke(2, (10, 10), (10, 10), (10, 10));
        Assert.True(stroke.IsWithin(new Point(10, 10), radius: 1));
        Assert.False(stroke.IsWithin(new Point(40, 10), radius: 1));
    }
    /// <summary>
    /// A speck of hand-shake at the moment the button goes down decides which way a flat end is
    /// squared off, and squares it against a direction the stroke never went in. It is left out.
    /// </summary>
    [Fact]
    public void AShakeAtTheStart_DoesNotGetToPointTheEndCap()
    {
        // Pressed at (0,0), twitched a pixel sideways, then drawn away to the right.
        var drawn = new[] { new Point(0, 0), new Point(0, 1), new Point(60, 0), new Point(120, 0) };

        var kept = AnnotationStroke.WithoutEndJitter(drawn, thickness: 20);

        Assert.Equal(new Point(0, 0), kept[0]);        // the stroke still starts where it started
        Assert.Equal(new Point(60, 0), kept[1]);       // but the twitch no longer aims the cap
        Assert.Equal(new Point(120, 0), kept[^1]);
    }

    [Fact]
    public void AShakeAtTheEnd_GoesToo()
    {
        var drawn = new[] { new Point(0, 0), new Point(60, 0), new Point(120, 0), new Point(120, 1) };

        var kept = AnnotationStroke.WithoutEndJitter(drawn, thickness: 20);

        Assert.Equal(new Point(120, 1), kept[^1]);     // still ends where the hand let go
        Assert.Equal(new Point(60, 0), kept[^2]);      // squared against real travel
    }

    /// <summary>Travel is not shake, however short the stroke. A drawn line keeps its shape.</summary>
    [Fact]
    public void APathThatIsAllTravel_IsLeftAlone()
    {
        var drawn = new[] { new Point(0, 0), new Point(40, 0), new Point(80, 40), new Point(120, 0) };

        Assert.Equal(drawn, AnnotationStroke.WithoutEndJitter(drawn, thickness: 20));
    }

    /// <summary>A stroke shorter than its own nib has nothing to spare, and keeps both ends.</summary>
    [Fact]
    public void AStrokeShorterThanItsNib_KeepsItsEnds()
    {
        var drawn = new[] { new Point(0, 0), new Point(1, 0), new Point(2, 0) };

        var kept = AnnotationStroke.WithoutEndJitter(drawn, thickness: 40);

        Assert.Equal(new Point(0, 0), kept[0]);
        Assert.Equal(new Point(2, 0), kept[^1]);
    }

}
