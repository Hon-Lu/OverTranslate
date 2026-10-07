using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A tilted group whose slope runs it into another group's text is drawn level, and nothing else
/// changes — see Ocr.TiltedOverlap.
/// </summary>
/// <remarks>
/// The first case is photo-ko-tilted's left tin (#273), rebuilt: a paragraph found tilted 3.1°
/// between two lines found level, the label's line pitch tighter than the paragraph's turned box.
/// </remarks>
public class TiltedOverlapTests
{
    [Fact]
    public void ATiltedParagraphThatRunsIntoTheLevelLineAboveIsDrawnLevel()
    {
        var above = Level("above", 348, 326, 390, 30);
        var paragraph = Tilted("paragraph", 3.1, sheared: true, new Rect(350, 346, 390, 54), new Rect(345, 351, 395, 43));
        var below = Level("below", 339, 407, 398, 50);

        var groups = TiltedOverlap.Level([above, paragraph, below]);

        Assert.Null(groups[1].Tilt);
        Assert.Equal(paragraph.Bounds, groups[1].Bounds);
        Assert.Same(above, groups[0]);
        Assert.Same(below, groups[2]);
    }

    [Fact]
    public void TiltedGroupsThatMeetNothingAreLeftAsTheyAre()
    {
        // Two comments on one card turned 30°, a line and a half apart across it: their upright
        // boxes overlap, their turned boxes do not.
        var first = Tilted("first", 30, new Point(250, 120), 300, 40);
        var second = Tilted("second", 30, new Point(250 - 60 * Math.Sin(Math.PI / 6), 120 + 60 * Math.Cos(Math.PI / 6)), 300, 40);
        var groups = new List<OcrTextBlock> { first, second };

        Assert.True(first.Bounds.IntersectsWith(second.Bounds));
        Assert.Same(groups, TiltedOverlap.Level(groups));
    }

    [Fact]
    public void ATiltedLineThatTouchesANeighbourLessThanItsUprightBoxWouldKeepsItsTilt()
    {
        // Region-comic-en-3's 9.4° card: the lone tilted line under the paragraph clips the
        // sound effect beside it, and would clip it more drawn level.
        var line = Tilted("line", 7.2, sheared: false, new Rect(48, 919, 501, 52), new Rect(47, 888, 503, 115));
        var neighbour = Level("neighbour", 470, 860, 120, 85);
        var groups = new List<OcrTextBlock> { line, neighbour };

        Assert.Same(groups, TiltedOverlap.Level(groups));
    }

    [Fact]
    public void ALevelPageComesBackAsItWent()
    {
        var groups = new List<OcrTextBlock> { Level("a", 0, 0, 100, 20), Level("b", 0, 10, 100, 20) };

        Assert.Same(groups, TiltedOverlap.Level(groups));
    }

    [Fact]
    public void AGroupPutBackLevelIsMeasuredAgainstInItsUprightBox()
    {
        // The first group's slope runs its left end into a label above, so it goes back level; its
        // upright box reaches lower than its turned one, onto the second group, which until then
        // met nothing.
        var label = Level("label", 0, 20, 80, 20);
        var first = Tilted("first", 4, sheared: true, new Rect(0, 30, 400, 30), new Rect(100, 30, 300, 60));
        var second = Tilted("second", 4, sheared: false, new Rect(100, 85, 300, 20), new Rect(100, 95, 300, 10));

        Assert.Same(second, TiltedOverlap.Level([label, second])[1]);

        var groups = TiltedOverlap.Level([label, first, second]);

        Assert.Null(groups[1].Tilt);
        Assert.Null(groups[2].Tilt);
    }

    private static OcrTextBlock Level(string text, double x, double y, double width, double height) =>
        new(text, new Rect(x, y, width, height), LayoutBounds: new Rect(x, y, width, height));

    /// <summary>A tilted group: <paramref name="box"/> in its level frame, drawn about its middle.</summary>
    private static OcrTextBlock Tilted(string text, double degrees, bool sheared, Rect box, Rect bounds)
    {
        var centre = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        return new OcrTextBlock(text, bounds, LayoutBounds: bounds)
        {
            Tilt = new TiltedText(degrees, sheared, centre, box, []),
        };
    }

    /// <summary>A group turned <paramref name="degrees"/> about its middle, upright box and all.</summary>
    private static OcrTextBlock Tilted(string text, double degrees, Point centre, double width, double height)
    {
        var box = new Rect(centre.X - width / 2, centre.Y - height / 2, width, height);
        var tilt = new TiltedText(degrees, false, centre, box, []);
        var bounds = TiltedText.Enclosing(tilt.Outline);
        return new OcrTextBlock(text, bounds, LayoutBounds: bounds) { Tilt = tilt };
    }
}
