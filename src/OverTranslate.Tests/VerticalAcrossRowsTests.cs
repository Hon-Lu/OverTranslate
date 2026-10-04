using System.Drawing;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// What a vertical capture writes across — a caption, a name plate, a card turned at an angle — is
/// told from its columns by the detector's quadrilateral, and is then set the way a horizontal
/// capture sets its lines, without moving a single column.
/// </summary>
/// <remarks>
/// The boxes are region-comic-en-3 (14 34 35.png) read in vertical mode, as the probe printed them:
/// upright box, then the quadrilateral's length, thickness and angle.
/// </remarks>
public class VerticalAcrossRowsTests
{
    private static OcrTextBlock Read(string text, Rect box, double length, double thickness, double degrees) =>
        new(text, box, Confidence: 0.98)
        {
            LayoutBounds = box,
            LineGeometry = new OcrLineGeometry(length, thickness, degrees),
        };

    // The card turned 25°: two long lines, and the last word on a line of its own.
    private static readonly OcrTextBlock When = Read(
        "WHEN HE SCREAMED WHILE STEPPING ON THAT", new Rect(81, 437, 356, 188), 381, 27, 25.5);
    private static readonly OcrTextBlock Abyss = Read(
        "ABYSS GOBLIN TRAP, IT GOT MY BLOOD PUMPING", new Rect(80, 455, 372, 196), 397, 30, 25.2);
    private static readonly OcrTextBlock Too = Read("TOO.", new Rect(84, 481, 55, 41), 49, 25, 21.4);

    // Further down the same capture, a caption of three level lines.
    private static readonly OcrTextBlock[] Caption =
    [
        Read("IF YOU'D SEEN HIM CRAWLING FORWARD AFTER", new Rect(204, 1134, 492, 33), 491, 31, 0.2),
        Read("GETTING COMPLETELY WRECKED AT THE END,", new Rect(205, 1164, 471, 34), 470, 31, 0.4),
        Read("YOU'D NEVER CALL HIM A PLAYER.", new Rect(205, 1195, 358, 33), 357, 31, 0.3),
    ];

    /// <summary>A column of a comic page, its quadrilateral standing upright.</summary>
    private static OcrTextBlock Column(string text, double x, double y, double width, double height) =>
        Read(text, new Rect(x, y, width, height), height, width, 90);

    [Fact]
    public void ALineTurnedSteeplyEnoughToHaveATallUprightBoxStillRunsAcross()
    {
        // 35°: the upright box is taller than wide, which the upright test alone calls a column.
        var steep = Read("IF YOU'D SEEN HIM IN PERSON EVEN", new Rect(79, 183, 300, 240), 359, 30, 35);

        Assert.True(VerticalColumnGrouping.RunsAcross(steep));
    }

    [Fact]
    public void TheWordOnALineOfItsOwnIsALineAndNotAColumn()
    {
        // Upright 55x41 is under the 1.4 the upright test asks for; along itself it is 49x25.
        Assert.True(VerticalColumnGrouping.RunsAcross(Too));
    }

    [Theory]
    [InlineData("忘れ物は", 40, 200)]   // a column of a balloon
    [InlineData("あに", 21, 31)]        // two characters, squarer than any line
    [InlineData("の", 30, 30)]          // one character runs neither way
    public void AColumnStaysAColumn(string text, double width, double height)
    {
        Assert.False(VerticalColumnGrouping.RunsAcross(Column(text, 100, 100, width, height)));
    }

    [Fact]
    public void ARowTheUprightBoxAlreadyCalledARowStaysOne()
    {
        // A page number, 1.48 along itself: under the quadrilateral's bar, over the upright one.
        var pageNumber = Read("184", new Rect(500, 1500, 43, 29), 43, 29, 0);

        Assert.True(VerticalColumnGrouping.RunsAcross(pageNumber));
    }

    [Fact]
    public void TheWordOnALineOfItsOwnIsNotTakenForASecondReadingOfTheLineAbove()
    {
        // Its upright box lies wholly inside the line above's, whose letters hold T and O twice.
        var groups = VerticalColumnGrouping.Group([When, Abyss, Too], frameWidth: 812);

        Assert.Contains(groups, group => group.Text.Contains("TOO."));
    }

    [Fact]
    public void ATiltedCardIsSetAsOneSentenceAlongTheCard()
    {
        var groups = VerticalColumnGrouping.Group([When, Abyss, Too], frameWidth: 812, language: "EN");

        var sentence = Assert.Single(groups);
        Assert.True(sentence.RunsAcross);
        Assert.EndsWith("PUMPING TOO.", sentence.Text);
        Assert.NotNull(sentence.Tilt);
        Assert.InRange(sentence.Tilt!.Degrees, 20, 30);
        // The capitals are 17px; the upright box's area made the line's glyph 45px.
        Assert.InRange(sentence.FontGlyphHeight!.Value, 12, 20);
    }

    [Fact]
    public void TheLivePathSetsTheTiltedLinesAlongTheCardToo()
    {
        var groups = VerticalColumnGrouping.Group([When, Abyss], frameWidth: 812, realtime: true, language: "EN");

        var sentence = Assert.Single(groups);
        Assert.True(sentence.RunsAcross);
        Assert.NotNull(sentence.Tilt);
        Assert.InRange(sentence.FontGlyphHeight!.Value, 12, 20);
    }

    [Fact]
    public void ALevelCaptionOfSeveralLinesIsOneSentence()
    {
        var groups = VerticalColumnGrouping.Group([.. Caption], frameWidth: 812, language: "EN");

        var caption = Assert.Single(groups);
        Assert.True(caption.RunsAcross);
        Assert.Null(caption.Tilt);
        Assert.Equal(3, caption.Lines.Count);
        Assert.InRange(caption.FontGlyphHeight!.Value, 14, 24);
    }

    [Fact]
    public void WithoutALanguageTheRowsAreHandedOnLineByLineAsBefore()
    {
        var groups = VerticalColumnGrouping.Group([.. Caption], frameWidth: 812);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, group => Assert.True(group.RunsAcross));
    }

    /// <summary>
    /// Setting the rows across changes nothing about the columns: the same groups, in the same
    /// order, with every field the same.
    /// </summary>
    [Fact]
    public void TheColumnsComeOutExactlyAsTheyDidWithoutIt()
    {
        OcrTextBlock[] page =
        [
            .. VerticalOcrGeometry.PrepareBlocks(
            [
                Column("忘れ物はないか", 300, 100, 30, 210),
                Column("確認してから", 262, 104, 30, 180),
                Column("出発しよう", 120, 400, 30, 150),
            ]),
            .. Caption,
        ];

        var before = VerticalColumnGrouping.Group([.. page], frameWidth: 812);
        var after = VerticalColumnGrouping.Group([.. page], frameWidth: 812, language: "JA");

        Assert.Equal(
            before.Where(group => !group.RunsAcross).Select(Describe),
            after.Where(group => !group.RunsAcross).Select(Describe));
        Assert.Single(after, group => group.RunsAcross);
    }

    private static string Describe(OcrTextBlock group) =>
        $"{group.Text}|{group.Bounds}|{group.LayoutBounds}|{string.Join(";", group.Lines)}|" +
        $"{group.RenderGlyphHeight}|{group.LayoutGlyphHeight}|{group.FontGlyphHeight}|{group.Confidence}";

    /// <summary>
    /// A line read off the turned frame comes back with its angle on the upright one — checked on
    /// real pixels, because a quarter turn has two directions and both are easy to write.
    /// </summary>
    [Fact]
    public void ALineReadOffTheTurnedFrameKeepsItsAngleOnTheUprightOne()
    {
        using var source = new Bitmap(80, 200);
        // Two marks 40 across and 10 down: a line at atan(10/40) = 14.04°.
        source.SetPixel(10, 30, Color.Red);
        source.SetPixel(50, 40, Color.Blue);

        using var turned = TurnedFrameDetection.Turn(source);
        var red = Find(turned, Color.Red);
        var blue = Find(turned, Color.Blue);
        double onTurned = Math.Atan2(blue.Y - red.Y, blue.X - red.X) * 180 / Math.PI;
        var read = new OcrTextBlock("line", new Rect(0, 0, 10, 40)) { LineGeometry = new OcrLineGeometry(41, 5, Fold(onTurned)) };

        var upright = TurnedFrameDetection.ToUpright(read, source.Width).LineGeometry!.Value;

        Assert.Equal(14.04, upright.AngleDegrees, tolerance: 0.1);
        Assert.Equal(41, upright.Length);
        Assert.Equal(5, upright.Thickness);
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(10, -80)]
    [InlineData(-10, 80)]
    [InlineData(90, 0)]
    public void TurningBackFoldsTheAngleIntoItsRange(double turned, double upright)
    {
        Assert.Equal(upright, new OcrLineGeometry(100, 20, turned).TurnedBack().AngleDegrees, tolerance: 1e-9);
    }

    [Fact]
    public void AGroupWrittenAcrossAVerticalPageIsReSetLikeAGeneralOne()
    {
        var caption = new TranslatedBlock("A B", "譯文", new Rect(10, 10, 200, 60),
            SourceLineBounds: [new Rect(10, 10, 200, 28), new Rect(10, 40, 180, 28)]) { RunsAcross = true };
        var column = new TranslatedBlock("縦書き", "直排譯文", new Rect(300, 10, 20, 60),
            SourceLineBounds: [new Rect(300, 10, 20, 30), new Rect(300, 40, 20, 30)]);

        var placed = OverlayPlacement.Place([caption, column], CaptureLayoutMode.General, verticalText: true);

        Assert.Equal(OverlayLayoutIntent.GroupReflow, placed[0].LayoutIntent);
        Assert.Equal(column, placed[1]);
    }

    private static double Fold(double angle) => angle > 90 ? angle - 180 : angle <= -90 ? angle + 180 : angle;

    private static Point Find(Bitmap bitmap, Color color)
    {
        for (var x = 0; x < bitmap.Width; x++)
            for (var y = 0; y < bitmap.Height; y++)
                if (bitmap.GetPixel(x, y).ToArgb() == color.ToArgb())
                    return new Point(x, y);
        throw new InvalidOperationException("mark not found");
    }
}
