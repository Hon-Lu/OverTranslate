using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using RapidOcrNet;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A Latin line set at a slant is sized for the overlay font by its letters, not by the upright
/// box around the slant — while the upright figure stays what the realtime overlay covers with.
/// </summary>
/// <remarks>
/// The measured case is region-comic-en-3: a chat card at 30°, where "I THINK THAT GUY'S A PLAYER
/// TOO." came back as a 357x33 quadrilateral inside a 211px-tall upright box, and was drawn at a
/// 147px font over 17px capitals.
/// </remarks>
public class TiltedLineGlyphHeightTests
{
    private const string Line = "I THINK THAT GUY'S A PLAYER TOO.";

    [Fact]
    public void ALevelQuadIsMeasuredAsItsOwnWidthAndHeight()
    {
        var geometry = OcrLineGeometry.FromQuad(Quad(357, 33, degrees: 0));

        Assert.NotNull(geometry);
        Assert.Equal(357, geometry.Value.Length, tolerance: 1.0);
        Assert.Equal(33, geometry.Value.Thickness, tolerance: 1.0);
        Assert.Equal(0, geometry.Value.AngleDegrees, tolerance: 0.5);
        Assert.False(geometry.Value.IsTilted);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-30)]
    public void ARotatedQuadKeepsItsLengthAndThicknessWhicheverWayItLeans(double degrees)
    {
        var geometry = OcrLineGeometry.FromQuad(Quad(357, 33, degrees))!.Value;

        Assert.Equal(357, geometry.Length, tolerance: 1.0);
        Assert.Equal(33, geometry.Thickness, tolerance: 1.0);
        Assert.Equal(degrees, geometry.AngleDegrees, tolerance: 0.5);
        Assert.True(geometry.IsTilted);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(20, true)]
    [InlineData(40, true)]
    [InlineData(60, false)]
    public void OnlyAModerateSlantCountsAsTilted(double degrees, bool tilted)
    {
        // Under 15° the upright box is kept, so the slightly tilted lines that were already sized
        // right do not move; past 45° the long side runs down the page, which on this pipeline is a
        // narrow box rather than a line leaning that far.
        Assert.Equal(tilted, OcrLineGeometry.FromQuad(Quad(357, 33, degrees))!.Value.IsTilted);
    }

    [Fact]
    public void SomethingOtherThanFourCornersHasNoGeometry()
    {
        Assert.Null(OcrLineGeometry.FromQuad(null));
        Assert.Null(OcrLineGeometry.FromQuad([new SKPointI(0, 0), new SKPointI(10, 0), new SKPointI(10, 5)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATiltedLineIsGivenTheFontHeightOfTheSameLineSetLevel(bool automatic)
    {
        var level = Normalize(Detected(Line, Quad(357, 33, degrees: 0)), automatic);
        var tilted = Normalize(Detected(Line, Quad(357, 33, degrees: 30)), automatic);

        Assert.NotNull(level.FontGlyphHeight);
        Assert.NotNull(tilted.FontGlyphHeight);
        Assert.Equal(level.FontGlyphHeight!.Value, tilted.FontGlyphHeight!.Value, tolerance: 1.0);

        // Far below the box, which is what it used to be sized from.
        Assert.True(tilted.FontGlyphHeight < tilted.Bounds.Height / 5,
            $"font glyph {tilted.FontGlyphHeight:0.#} against a {tilted.Bounds.Height:0.#}px box");
    }

    [Fact]
    public void ATiltedLineKeepsTheUprightGlyphHeightForCoverage()
    {
        // What the realtime overlay erases and covers with. The slanted letters do reach the height
        // of the upright box, so this must not shrink with the font.
        var tilted = Normalize(Detected(Line, Quad(357, 33, degrees: 30)), automatic: false);

        Assert.True(tilted.RenderGlyphHeight > 100, $"render glyph {tilted.RenderGlyphHeight:0.#}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void BelowTheTiltTheFontHeightIsExactlyTheRenderHeight(double degrees)
    {
        var block = Normalize(Detected(Line, Quad(357, 33, degrees)), automatic: false);

        Assert.Equal(block.RenderGlyphHeight, block.FontGlyphHeight);
    }

    [Fact]
    public void ABoxNotClearlyLongerThanThickKeepsItsUprightEstimate()
    {
        // Measured on a level web page (screen-web-en/serp.png): the detector fitted a two-character
        // 免費 with a 35x21 box turned -17°. A blob that short has no direction worth believing.
        var block = Normalize(Detected("OK", Quad(35, 21, degrees: -17)), automatic: false);

        Assert.Equal(block.RenderGlyphHeight, block.FontGlyphHeight);
    }

    [Fact]
    public void AGroupCarriesTheMedianFontHeightOfItsLines()
    {
        var group = OcrTextBlockGrouper.BuildGroup(
        [
            new OcrTextBlock("A", new Rect(0, 0, 300, 180), RenderGlyphHeight: 150) { FontGlyphHeight = 16 },
            new OcrTextBlock("B", new Rect(0, 40, 300, 180), RenderGlyphHeight: 148) { FontGlyphHeight = 18 },
            new OcrTextBlock("C", new Rect(0, 80, 300, 180), RenderGlyphHeight: 152) { FontGlyphHeight = 17 },
        ]);

        Assert.Equal(17, group.FontGlyphHeight);
        Assert.Equal(150, group.RenderGlyphHeight);
    }

    private static OcrTextBlock Normalize(TextBlock detected, bool automatic) =>
        Assert.Single(OnnxOcrEngine.ApplyBlockFilters(
            [detected], automatic ? "AUTO" : "EN", useCjkRenderMetrics: false, usesAutomaticLayout: automatic));

    /// <summary>A length-by-thickness box turned about its centre, corners in detector order.</summary>
    private static SKPointI[] Quad(double length, double thickness, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        const double cx = 400, cy = 300;
        (double X, double Y)[] corners =
        [
            (-length / 2, -thickness / 2), (length / 2, -thickness / 2),
            (length / 2, thickness / 2), (-length / 2, thickness / 2),
        ];
        return
        [
            .. corners.Select(c => new SKPointI(
                (int)Math.Round(cx + c.X * cos - c.Y * sin),
                (int)Math.Round(cy + c.X * sin + c.Y * cos))),
        ];
    }

    private static TextBlock Detected(string text, SKPointI[] quad) =>
        new()
        {
            BoxPoints = quad,
            BoxScore = 0.9f,
            Text = text,
            Chars = [.. text.Select(c => c.ToString())],
            CharScores = [.. text.Select(_ => 0.99f)],
        };
}
