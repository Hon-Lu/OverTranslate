using System.Windows;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Views.Overlay;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What the 標記 eraser actually takes away. The claim being checked is that it is a circle: the ring
/// drawn under the pointer is a promise about what will disappear, and these say the promise is kept.
/// </summary>
/// <remarks>
/// Asked of the picture the marks are kept in rather than of a shape cut out of a stroke — see
/// <see cref="InkSurface"/> for why the marks are a picture. The questions are the same ones as
/// before ("is there still ink here?"); only where the answer lives has changed. A scale of 1 keeps
/// a pixel and a DIP the same size, so the numbers here are the coordinates a user would be pointing
/// at.
///
/// Run on an STA thread because laying a stroke down rasterises it, and WPF will not rasterise
/// anywhere else. xunit runs its tests on the thread pool, which is not one.
/// </remarks>
public class AnnotationEraserTests
{
    private static readonly Rect Surface = new(0, 0, 400, 200);

    private static AnnotationStroke Stroke(
        double thickness, AnnotationTool tool, params (double X, double Y)[] points) =>
        new()
        {
            Tool      = tool,
            Color     = Colors.Orange,
            Thickness = thickness,
            Opacity   = 1,
            Points    = [.. points.Select(p => new Point(p.X, p.Y))],
        };

    /// <summary>Runs the test body where WPF will rasterise, and brings its failure back out.</summary>
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

    /// <summary>A surface with one stroke already on it, centred on y=100.</summary>
    private static InkSurface Drawn(double thickness, AnnotationTool tool, double fromX, double toX)
    {
        var surface = new InkSurface();
        surface.Ensure(Surface, scale: 1);
        surface.Lay(Stroke(thickness, tool, (fromX, 100), (toX, 100)));
        return surface;
    }

    /// <summary>Whether ink is still showing there. The rim is a ramp, so this asks the middle of it.</summary>
    private static bool InkAt(InkSurface surface, double x, double y) =>
        surface.InkAt(new Point(x, y)) > 0.5;

    [Fact]
    public void AStrokeLaidDown_IsThereAndOnlyThere()
    {
        OnStaThread(() =>
        {
            var surface = Drawn(8, AnnotationTool.Pen, 50, 350);

            Assert.True(InkAt(surface, 200, 100));
            Assert.False(InkAt(surface, 200, 140));
            Assert.False(InkAt(surface, 20, 100));
        });
    }

    [Fact]
    public void ARubInTheMiddle_TakesTheMiddleAndLeavesTheEnds()
    {
        OnStaThread(() =>
        {
            var surface = Drawn(8, AnnotationTool.Pen, 50, 350);
            surface.Erase(new Point(200, 100), new Point(200, 100), radius: 20);

            Assert.False(InkAt(surface, 200, 100));
            Assert.True(InkAt(surface, 60, 100));
            Assert.True(InkAt(surface, 340, 100));
        });
    }

    /// <summary>
    /// The test the whole design turns on. A small eraser run down the middle of a wide highlight has
    /// to take a small bite out of it — not the entire band, which is what cutting the centre line
    /// would do, and not nothing.
    /// </summary>
    [Fact]
    public void ASmallRubOnAWideStroke_TakesOnlyWhatTheCircleCovered()
    {
        OnStaThread(() =>
        {
            var surface = Drawn(60, AnnotationTool.Highlighter, 50, 350);
            surface.Erase(new Point(200, 100), new Point(200, 100), radius: 8);

            // Gone where the circle was...
            Assert.False(InkAt(surface, 200, 100));

            // ...and still there above and below it, inside the same 60px band.
            Assert.True(InkAt(surface, 200, 120));
            Assert.True(InkAt(surface, 200, 80));
        });
    }

    /// <summary>
    /// A drag is sampled, so what one step takes has to be the circle's whole path and not a bite at
    /// each end with the middle left standing.
    /// </summary>
    [Fact]
    public void ADragBetweenTwoDistantPoints_SweepsTheWholeWay()
    {
        OnStaThread(() =>
        {
            var surface = Drawn(60, AnnotationTool.Highlighter, 20, 380);
            surface.Erase(new Point(50, 100), new Point(350, 100), radius: 10);

            Assert.False(InkAt(surface, 200, 100));
            Assert.False(InkAt(surface, 200, 106));

            // And no wider than the circle: the band is 60 across and only the middle 20 was rubbed.
            Assert.True(InkAt(surface, 200, 122));
        });
    }

    [Fact]
    public void ARubNowhereNearAnything_SaysItTookNothing()
    {
        OnStaThread(() =>
        {
            var surface = Drawn(8, AnnotationTool.Pen, 50, 350);

            Assert.True(surface.Erase(new Point(200, 100), new Point(200, 100), radius: 10));

            // Well clear of the one stroke on the surface. Nothing was showing, so nothing went —
            // which is what keeps a rub over bare screen from becoming a step of 復原 that does
            // nothing visible when it is pressed. Asked twice over the same spot it would answer
            // yes again, and rightly: the rim of a bite is a ramp, and a second pass fades it on.
            Assert.False(surface.Erase(new Point(200, 190), new Point(200, 190), radius: 5));
            Assert.False(surface.Erase(new Point(20, 20), new Point(30, 30), radius: 5));
        });
    }

    /// <summary>
    /// Repainting from the stroke list has to run in order. A mark made after a rub was never rubbed,
    /// and replaying the rub last would take it off something it never touched.
    /// </summary>
    [Fact]
    public void ReplayingAMarkMadeAfterARub_LeavesTheMarkAlone()
    {
        OnStaThread(() =>
        {
            var surface = new InkSurface();
            surface.Ensure(Surface, scale: 1);

            // The rub reaches x 180..220; the mark made afterwards covers only the middle of that,
            // so the two can be told apart.
            var first  = Stroke(8, AnnotationTool.Pen, (50, 100), (350, 100));
            var rub    = Stroke(40, AnnotationTool.Eraser, (200, 100), (200, 100));
            var second = Stroke(8, AnnotationTool.Pen, (196, 100), (204, 100));

            surface.Replay([first, rub, second]);

            Assert.True(InkAt(surface, 200, 100));    // the later mark survived the earlier rub
            Assert.False(InkAt(surface, 185, 100));   // inside the rub, and nothing drawn back over
            Assert.True(InkAt(surface, 60, 100));     // outside both, the first mark is untouched
        });
    }

    /// <summary>A rub is undone by repainting without it, which is the only way pixels go backwards.</summary>
    [Fact]
    public void ReplayingWithoutTheRub_PutsTheInkBack()
    {
        OnStaThread(() =>
        {
            var surface = new InkSurface();
            surface.Ensure(Surface, scale: 1);

            var mark = Stroke(8, AnnotationTool.Pen, (50, 100), (350, 100));
            var rub  = Stroke(40, AnnotationTool.Eraser, (200, 100), (200, 100));

            surface.Replay([mark, rub]);
            Assert.False(InkAt(surface, 200, 100));

            surface.Replay([mark]);
            Assert.True(InkAt(surface, 200, 100));
        });
    }
    /// <summary>
    /// The picture is only as big as the box, and the box can be dragged or stretched afterwards.
    /// What was drawn has to come across unmoved when it grows: the marks are placed in the window,
    /// not in the box, so a mark must not shift because the box did.
    /// </summary>
    [Fact]
    public void GrowingTheSurface_CarriesWhatWasDrawnAcrossUnmoved()
    {
        OnStaThread(() =>
        {
            var surface = new InkSurface();
            surface.Ensure(new Rect(200, 100, 200, 100), scale: 1);
            surface.Lay(Stroke(8, AnnotationTool.Pen, (250, 150), (350, 150)));

            Assert.True(InkAt(surface, 300, 150));

            // Stretched up and to the left, which moves the picture's own origin.
            surface.Ensure(new Rect(0, 0, 400, 200), scale: 1);

            Assert.True(InkAt(surface, 300, 150));    // still where it was drawn
            Assert.True(InkAt(surface, 260, 150));
            Assert.False(InkAt(surface, 100, 150));   // and nothing appeared in the new ground
        });
    }

    /// <summary>Shrinking the box must not take ink away — it is hidden by the clip, not deleted.</summary>
    [Fact]
    public void ANarrowerBox_DoesNotThrowInkAway()
    {
        OnStaThread(() =>
        {
            var surface = new InkSurface();
            surface.Ensure(new Rect(0, 0, 400, 200), scale: 1);
            surface.Lay(Stroke(8, AnnotationTool.Pen, (50, 100), (350, 100)));

            surface.Ensure(new Rect(150, 80, 100, 40), scale: 1);

            Assert.True(InkAt(surface, 60, 100));
            Assert.True(InkAt(surface, 340, 100));
        });
    }

}
