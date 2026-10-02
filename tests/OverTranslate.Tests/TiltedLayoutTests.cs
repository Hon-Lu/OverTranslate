using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Tilted cards are grouped as their level counterparts would be, and level pages exactly as before.
/// </summary>
/// <remarks>
/// The geometry is region-comic-en-3's, rebuilt from the measured quadrilaterals: a card turned
/// 9.4° (案例 (4)) and a card seen in perspective, lines sloping 30° with the letters and the margin
/// upright. See Ocr.TiltedLayout.
/// </remarks>
public class TiltedLayoutTests
{
    private static readonly string[] Paragraph =
    [
        "BLING0_0*** NOPE, NOT LEAVING. THE ONE",
        "LEAVING WILL BE YOUR FUTURE WIFE. NO MATTER",
        "HOW NICE SHE IS, SHE WON'T ENDURE THAT AND",
        "SHE'LL RUN AWAY. SO EVEN AS A NOBLE, YOU",
        "WON'T BE ABLE TO KEEP A SINGLE MISTRESS.",
    ];

    /// <summary>
    /// 案例 (4): five lines of one comment on a card turned 9.4°, and a sound effect read as a
    /// confident "E" above them. Unstraightened, the five became one 259px "row" whose height let
    /// the 297px sound effect in, and the group was sized from it at three times the source.
    /// </summary>
    [Theory]
    [InlineData(RealtimeBlockMode.Subtitle)]
    [InlineData(RealtimeBlockMode.Panel)]
    public void ARotatedParagraphIsOneGroupOfItsLinesAndKeepsTheSoundEffectOut(RealtimeBlockMode mode)
    {
        var lengths = new[] { 545.0, 630, 594, 563, 565 };
        // Left-aligned on the card, 33px apart across the lines, the whole card turned about the
        // first line's left end.
        var turn = -9.4 * Math.PI / 180;
        var origin = new Point(125, 440);
        var lines = Enumerable.Range(0, 5)
            .Select(i =>
            {
                double x = lengths[i] / 2, y = i * 33;
                return Rotated(Paragraph[i],
                    origin.X + x * Math.Cos(turn) - y * Math.Sin(turn),
                    origin.Y + x * Math.Sin(turn) + y * Math.Cos(turn),
                    lengths[i], 41, -9.4);
            })
            .ToList();
        // Its bottom a few pixels into the card's upright box, as in the frame that went wrong.
        var soundEffect = Upright("E", 0, 40, 371, 297);
        var blocks = lines.Prepend(soundEffect).ToList();

        var groups = OcrService.GroupRealtime(blocks, 1298, mode);

        var paragraph = Assert.Single(groups, group => group.Text.Contains("SINGLE MISTRESS"));
        Assert.Equal(5, paragraph.Lines.Count);
        Assert.DoesNotContain("E BLING", paragraph.Text);
        Assert.StartsWith("BLING0_0", paragraph.Text);
        Assert.Contains(groups, group => group.Text == "E");

        // The level boxes were only for the rules: what comes out is the box the detector drew.
        var upright = lines.Select(line => line.LayoutBounds).Aggregate(Rect.Union);
        Assert.Equal(upright, paragraph.LayoutBounds);
        Assert.All(groups, group => Assert.Null(group.UprightLayoutBounds));
    }

    /// <summary>
    /// Two comments on a card in perspective: lines sloping 30°, letters and margin upright, set
    /// so tight that neighbouring lines are 0.55 of their own thickness apart. Unstraightened, the
    /// dialogue grouper paired the lines up across each other; level, each comment is one group.
    /// </summary>
    [Fact]
    public void ASheardCardSeparatesItsCommentsAndKeepsEachWhole()
    {
        const double slope = 30, thickness = 32;
        var pitch = 0.55 * thickness / Math.Cos(slope * Math.PI / 180);
        var gap = 1.15 * thickness / Math.Cos(slope * Math.PI / 180);

        var y = 100.0;
        var blocks = new List<OcrTextBlock>();
        void Add(string text, double length, double advance)
        {
            y += advance;
            blocks.Add(Sloped(text, 80, y, length, thickness, slope));
        }

        Add("arolf52'5", 126, 0);
        Add("IF YOU'D SEEN HIM IN PERSON EVEN", 363, pitch * 1.2);
        Add("ONCE, YOU'D NEVER SAY THAT LOL", 340, pitch);
        Add("Ykbell", 150, gap);
        Add("WHEN HE SCREAMED WHILE STEPPING ON THAT", 387, pitch * 1.2);
        Add("ABYSS GOBLIN TRAP, IT GOT MY BLOOD PUMPING", 399, pitch);

        var groups = OcrService.GroupRealtime(blocks, 1296, RealtimeBlockMode.Subtitle);

        Assert.Equal(
            [
                "arolf52'5 IF YOU'D SEEN HIM IN PERSON EVEN ONCE, YOU'D NEVER SAY THAT LOL",
                "Ykbell WHEN HE SCREAMED WHILE STEPPING ON THAT ABYSS GOBLIN TRAP, IT GOT MY BLOOD PUMPING",
            ],
            groups.Select(group => group.Text));
        Assert.All(groups, group => Assert.Equal(3, group.Lines.Count));
    }

    /// <summary>
    /// A cluster needs two long lines at one angle whose boxes meet. A lone slanted caption, or two
    /// lines turned different ways, keep their upright boxes.
    /// </summary>
    [Theory]
    [InlineData(20, 20, false)] // one line only: the second is level
    [InlineData(12, 24, true)]  // two tilted lines, 12° apart
    public void LinesThatAreNotOneTiltedCardAreLeftAlone(double first, double second, bool secondTilted)
    {
        var blocks = new List<OcrTextBlock>
        {
            Rotated("A SLANTED CAPTION ON THE WALL", 300, 300, 400, 36, first),
            secondTilted
                ? Rotated("ANOTHER ONE TURNED THE OTHER WAY", 300, 330, 400, 36, second)
                : Upright("A LEVEL LINE BESIDE IT", 120, 310, 400, 36),
        };

        Assert.Same(blocks, TiltedLayout.Straighten(blocks));
    }

    /// <summary>A level page goes through untouched, down to the list instance.</summary>
    [Fact]
    public void ALevelPageIsNotTouched()
    {
        var blocks = new List<OcrTextBlock>
        {
            Rotated("IF YOU'D SEEN HIM CRAWLING FORWARD AFTER", 450, 150, 495, 36, 0.3),
            Rotated("GETTING COMPLETELY WRECKED AT THE END,", 440, 181, 472, 33, 0.2),
            Rotated("YOU'D NEVER CALL HIM A PLAYER.", 384, 211, 358, 33, 0.3),
        };

        Assert.Same(blocks, TiltedLayout.Straighten(blocks));

        var groups = OcrService.GroupRealtime(blocks, 1296, RealtimeBlockMode.Subtitle);
        Assert.Equal(3, Assert.Single(groups).Lines.Count);
    }

    /// <summary>A line turned about its centre, as a rotated card draws it.</summary>
    private static OcrTextBlock Rotated(
        string text, double centreX, double centreY, double length, double thickness, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var along = new Vector(Math.Cos(radians), Math.Sin(radians)) * (length / 2);
        var across = new Vector(-Math.Sin(radians), Math.Cos(radians)) * (thickness / 2);
        var centre = new Point(centreX, centreY);
        return Detected(text, [centre + along + across, centre + along - across, centre - along + across, centre - along - across],
            new OcrLineGeometry(length, thickness, degrees));
    }

    /// <summary>
    /// A line sloping from a left end on an upright margin, letters upright: its quadrilateral is
    /// still the rectangle around the slope, which is what the detector returns.
    /// </summary>
    private static OcrTextBlock Sloped(
        string text, double leftX, double leftY, double length, double thickness, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var direction = new Vector(Math.Cos(radians), Math.Sin(radians));
        var centre = new Point(leftX, leftY) + direction * (length / 2);
        return Rotated(text, centre.X, centre.Y, length, thickness, degrees);
    }

    private static OcrTextBlock Upright(string text, double x, double y, double width, double height) =>
        Detected(text, [new Point(x, y), new Point(x + width, y), new Point(x, y + height), new Point(x + width, y + height)],
            new OcrLineGeometry(Math.Max(width, height), Math.Min(width, height), width >= height ? 0 : 90));

    /// <summary>The block as the engine hands it to grouping, Latin, from its quadrilateral.</summary>
    private static OcrTextBlock Detected(string text, Point[] corners, OcrLineGeometry geometry)
    {
        double left = corners.Min(p => p.X), top = corners.Min(p => p.Y);
        var box = new Rect(left, top, corners.Max(p => p.X) - left, corners.Max(p => p.Y) - top);
        var glyph = OnnxOcrEngine.LayoutGlyphHeightFor(OcrLayoutScript.Latin, box, text);
        return new OcrTextBlock(text, box, RenderGlyphHeight: glyph, Confidence: 0.98,
            LayoutScript: OcrLayoutScript.Latin, LayoutBounds: box, LayoutGlyphHeight: glyph)
        {
            LineGeometry = geometry,
            FontGlyphHeight = glyph,
        };
    }
}
