using OverTranslate.Services.Ocr;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A ―― between two words of a line is found by its shape and put back only where the recogniser
/// read nothing there (#398).
/// </summary>
/// <remarks>
/// Synthetic pixels in the style of <see cref="HorizontalLineEndsTests"/>: a dark background and a
/// line of white hollow squares for glyphs, inside a box padded the way the detector pads.
/// </remarks>
public class HorizontalInnerDashesTests
{
    private const int Glyph = 40;
    private const int Top = 40;
    private const int FirstGlyph = 100;

    private static readonly SKColor Background = new(30, 28, 40);
    private static readonly SKColor White = new(250, 250, 250);

    [Fact]
    public void ADashBetweenTwoWordsIsFound()
    {
        // は――い: three glyphs, a bar two glyphs long through the middle of the line, three more.
        using var image = Line(out var box, out var gapLeft);
        Fill(image, gapLeft + 4, Top + 19, gapLeft + 2 * Glyph - 4, Top + 22);

        var dashes = HorizontalInnerDashes.Find(image, box);

        var dash = Assert.Single(dashes);
        Assert.Equal(gapLeft + 4, dash.Left);
        Assert.Equal(gapLeft + 2 * Glyph - 4, dash.Right);
    }

    [Fact]
    public void ALineWithoutOneHasNone()
    {
        using var image = Line(out var box, out _);
        Assert.Empty(HorizontalInnerDashes.Find(image, box));
    }

    [Fact]
    public void ABarAGlyphLongIsNotTaken()
    {
        // As likely 一 or ー, which the recogniser reads.
        using var image = Line(out var box, out var gapLeft);
        Fill(image, gapLeft + 4, Top + 19, gapLeft + Glyph - 4, Top + 22);
        Assert.Empty(HorizontalInnerDashes.Find(image, box));
    }

    [Fact]
    public void AStrikeThroughIsNotTaken()
    {
        // Through the glyphs rather than between them: it is joined to their strokes.
        using var image = Line(out var box, out _);
        Fill(image, FirstGlyph, Top + 19, FirstGlyph + 3 * Glyph, Top + 22);
        Assert.Empty(HorizontalInnerDashes.Find(image, box));
    }

    [Fact]
    public void ABarOffTheMiddleOfTheLineIsNotTaken()
    {
        // An underline under the gap, not a dash in it.
        using var image = Line(out var box, out var gapLeft);
        Fill(image, gapLeft + 4, Top + 34, gapLeft + 2 * Glyph - 4, Top + 37);
        Assert.Empty(HorizontalInnerDashes.Find(image, box));
    }

    [Fact]
    public void ADashThatEndsTheLineIsLeftToTheLineEnds()
    {
        // Ink on one side only: HorizontalLineEnds answers for the ends of a line.
        using var image = new SKBitmap(640, 140);
        using (var canvas = new SKCanvas(image)) canvas.Clear(Background);
        for (var i = 0; i < 4; i++) Square(image, FirstGlyph + i * Glyph + 3, Top);
        var end = FirstGlyph + 4 * Glyph;
        Fill(image, end + 4, Top + 19, end + 2 * Glyph, Top + 22);
        var box = new SKRectI(FirstGlyph - 10, Top - 10, end + 2 * Glyph + 10, Top + Glyph + 10);

        Assert.Empty(HorizontalInnerDashes.Find(image, box));
    }

    [Fact]
    public void TheDashGoesInWhereTheHalvesMeet()
    {
        string[] chars = ["も", "あ", "ん", "た", "は", "い", "や"];
        float[] scores = [0.9f, 0.9f, 0.9f, 0.9f, 0.9f, 0.9f, 0.9f];

        var put = HorizontalInnerDashes.WithDashes(chars, scores, [("もあんたは", "いや")]);

        Assert.NotNull(put);
        Assert.Equal("もあんたは――いや", string.Concat(put.Value.Chars));
        Assert.Equal(put.Value.Chars.Length, put.Value.Scores.Length);
    }

    [Theory]
    // A half that reads differently from the line: the cut changed what was read.
    [InlineData("もあんたば", "いや")]
    // The halves do not add up to the line: something was read where the bar is.
    [InlineData("もあんた", "いや")]
    // A half that read nothing.
    [InlineData("", "もあんたはいや")]
    public void NothingGoesInUnlessTheHalvesAreTheLine(string before, string after)
    {
        string[] chars = ["も", "あ", "ん", "た", "は", "い", "や"];
        float[] scores = [0.9f, 0.9f, 0.9f, 0.9f, 0.9f, 0.9f, 0.9f];
        Assert.Null(HorizontalInnerDashes.WithDashes(chars, scores, [(before, after)]));
    }

    [Fact]
    public void ADashTheRecogniserReadIsNotDoubled()
    {
        string[] chars = ["は", "ー", "い"];
        float[] scores = [0.9f, 0.9f, 0.9f];
        Assert.Null(HorizontalInnerDashes.WithDashes(chars, scores, [("はー", "い")]));
    }

    [Fact]
    public void TheHalvesAreCutAtTheDash()
    {
        var box = new RapidOcrNet.TextBox
        {
            Score = 0.9f,
            BoxPoints = [new(10, 20), new(310, 20), new(310, 60), new(10, 60)],
        };

        var before = HorizontalInnerDashes.Cut(box, 150, keepLeft: true).BoxPoints;
        var after = HorizontalInnerDashes.Cut(box, 190, keepLeft: false).BoxPoints;

        Assert.Equal([new(10, 20), new(150, 20), new(150, 60), new(10, 60)], before);
        Assert.Equal([new(190, 20), new(310, 20), new(310, 60), new(190, 60)], after);
    }

    // Three glyphs, a gap two glyphs wide, three glyphs; the box padded a quarter glyph all round.
    private static SKBitmap Line(out SKRectI box, out int gapLeft)
    {
        var image = new SKBitmap(640, 140);
        using (var canvas = new SKCanvas(image)) canvas.Clear(Background);
        for (var i = 0; i < 3; i++) Square(image, FirstGlyph + i * Glyph + 3, Top);
        gapLeft = FirstGlyph + 3 * Glyph;
        for (var i = 0; i < 3; i++) Square(image, gapLeft + 2 * Glyph + i * Glyph + 3, Top);
        box = new SKRectI(FirstGlyph - 10, Top - 10, gapLeft + 5 * Glyph + 10, Top + Glyph + 10);
        return image;
    }

    // A glyph: the outline of a square, 3px strokes, a few pixels of side bearing.
    private static void Square(SKBitmap image, int left, int top)
    {
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint { Color = White, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        canvas.DrawRect(left + 1.5f, top + 1.5f, Glyph - 9, Glyph - 3, paint);
    }

    private static void Fill(SKBitmap image, int left, int top, int right, int bottom)
    {
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint { Color = White, IsAntialias = false };
        canvas.DrawRect(SKRect.Create(left, top, right - left, bottom - top), paint);
    }
}
