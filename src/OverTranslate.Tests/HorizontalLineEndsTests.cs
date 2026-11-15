using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A line's box grows to take in the symbols it stopped short of, and for nothing else.
/// </summary>
/// <remarks>
/// Synthetic pixels: a dark background, a line of white "glyphs" (hollow squares, which leave most
/// of their box empty as strokes do) inside a detector box padded the way the detector pads, and
/// whatever sits beside it. Each case adds exactly one thing past the box's end.
/// </remarks>
public class HorizontalLineEndsTests
{
    private const int Glyph = 40;
    private const int Top = 40;
    private const int FirstGlyph = 100;
    private const int Glyphs = 5;
    // The line's last ink, and the box around it: the detector's padding is about a quarter of a
    // glyph on every side.
    private const int LastInk = FirstGlyph + Glyphs * Glyph;
    private static readonly SKRectI Box = new(FirstGlyph - 10, Top - 10, LastInk + 10, Top + Glyph + 10);

    private static readonly SKColor Background = new(30, 28, 40);
    private static readonly SKColor White = new(250, 250, 250);

    [Fact]
    public void TrailingDotsPastTheBoxAreTakenIn()
    {
        // 「……」 the way user-4 has it: six dots on the lower middle of the line, the first 0.4 of a
        // glyph after the last word, each 0.4 of a glyph after the one before.
        using var image = Line();
        for (var i = 0; i < 6; i++) Dot(image, LastInk + 16 + i * 16, Top + 22);

        var ends = HorizontalLineEnds.Measure(image, Box, []);

        Assert.NotNull(ends);
        Assert.True(ends.Value.Right >= LastInk + 16 + 5 * 16 + 5);
        Assert.Equal(Box.Left, ends.Value.Left);
    }

    [Fact]
    public void ALineThatAlreadyEndsInsideItsBoxDoesNotMove()
    {
        using var image = Line();
        Assert.Null(HorizontalLineEnds.Measure(image, Box, []));
    }

    [Fact]
    public void ALeadingDashIsTakenIn()
    {
        // 「——」 opening a line: one thin bar two glyphs long, before the first word.
        using var image = Line();
        Fill(image, FirstGlyph - 12 - 2 * Glyph, Top + 19, FirstGlyph - 12, Top + 22);

        var ends = HorizontalLineEnds.Measure(image, Box, []);

        Assert.NotNull(ends);
        Assert.True(ends.Value.Left <= FirstGlyph - 12 - 2 * Glyph);
        Assert.Equal(Box.Right, ends.Value.Right);
    }

    [Fact]
    public void AGlyphTheBoxCutInHalfIsGivenBackWhole()
    {
        // The box stops inside the last glyph: its far side is the "cut" signal, ink against the edge.
        using var image = Line();
        var cut = new SKRectI(Box.Left, Box.Top, LastInk - Glyph / 2, Box.Bottom);

        var ends = HorizontalLineEnds.Measure(image, cut, []);

        Assert.NotNull(ends);
        Assert.True(ends.Value.Right >= LastInk);
    }

    [Fact]
    public void AStrokedGlyphTheBoxCutIsStrokesAndAClippedIconIsNot()
    {
        // 二 cut in half by the box: two thin bars run on past its edge. A solid icon the box
        // clipped is a filled shape, and a side that grew onto it and read nothing does not keep it.
        using var stroked = Line();
        Fill(stroked, LastInk - 10, Top + 4, LastInk + 2 + Glyph, Top + 6);
        Fill(stroked, LastInk - 10, Top + 35, LastInk + 2 + Glyph, Top + 37);
        var cutBox = new SKRectI(Box.Left, Box.Top, LastInk + 4, Box.Bottom);
        var two = HorizontalLineEnds.Measure(stroked, cutBox, []);

        using var icon = Line();
        Fill(icon, LastInk - 2, Top + 6, LastInk + 20, Top + 34);
        var iconBox = new SKRectI(Box.Left, Box.Top, LastInk + 2, Box.Bottom);
        var clipped = HorizontalLineEnds.Measure(icon, iconBox, []);

        Assert.NotNull(two);
        Assert.True(two.Value.RightCut && two.Value.RightStrokes);
        Assert.True(clipped is null || !clipped.Value.RightStrokes);
    }

    [Fact]
    public void ABlinkingPromptAfterTheLineIsLeftOut()
    {
        // ▼ — a solid shape the size of a glyph, which no stroke-built character is.
        using var image = Line();
        using (var canvas = new SKCanvas(image))
        using (var paint = new SKPaint { Color = White, IsAntialias = false, Style = SKPaintStyle.Fill })
        {
            using var path = new SKPath();
            path.MoveTo(LastInk + 14, Top + 10);
            path.LineTo(LastInk + 14 + 26, Top + 10);
            path.LineTo(LastInk + 14 + 13, Top + 32);
            path.Close();
            canvas.DrawPath(path, paint);
        }

        Assert.Null(HorizontalLineEnds.Measure(image, Box, []));
    }

    [Fact]
    public void AFrameRunningPastTheLineIsLeftOut()
    {
        // The edge of a dialogue box or a picture: same colour, but it runs on above and below the
        // line, which no glyph of the line does.
        using var image = Line();
        Fill(image, LastInk + 16, 0, LastInk + 19, image.Height);

        Assert.Null(HorizontalLineEnds.Measure(image, Box, []));
    }

    [Fact]
    public void InkOfAnotherColourIsNotThisLine()
    {
        // Pale pink artwork beside white text: as bright, but with a hue the text does not have.
        using var image = Line();
        for (var i = 0; i < 6; i++) Dot(image, LastInk + 16 + i * 16, Top + 22, new SKColor(255, 170, 200));

        Assert.Null(HorizontalLineEnds.Measure(image, Box, []));
    }

    [Fact]
    public void SymbolsFurtherOffThanTheGapAllowsAreSomethingElse()
    {
        // A full glyph and more of blank: the next thing along the row, not the end of this line.
        using var image = Line();
        for (var i = 0; i < 3; i++) Dot(image, LastInk + 50 + i * 16, Top + 22);

        Assert.Null(HorizontalLineEnds.Measure(image, Box, []));
    }

    [Fact]
    public void DotsBetweenTwoBoxesGoToTheOneOnTheLeft()
    {
        // The detector cut one line in two and left the 「……」 between them in neither box. Both
        // walks reach the dots; only the left one takes them, so the two crops do not overlap.
        using var image = Line(width: 900);
        for (var i = 0; i < 4; i++) Dot(image, LastInk + 16 + i * 16, Top + 22);
        var secondStart = LastInk + 16 + 3 * 16 + 20;
        Square(image, secondStart, Top);
        var second = new SKRectI(secondStart - 4, Box.Top, secondStart + Glyph + 10, Box.Bottom);

        var left = HorizontalLineEnds.Measure(image, Box, [second]);
        var right = HorizontalLineEnds.Measure(image, second, [Box]);

        Assert.NotNull(left);
        Assert.True(left.Value.Right <= second.Left);
        Assert.True(left.Value.Right >= LastInk + 16 + 3 * 16 + 5);
        Assert.Null(right);
    }

    [Fact]
    public void ApplyHandsBackTheSameListWhenNothingGrows()
    {
        using var image = Line();
        var boxes = new List<RapidOcrNet.TextBox> { Detector(Box) };

        var result = HorizontalLineEnds.Apply(image, [Rect(Box)], boxes, 1.0, 1.0, 0, out _);

        Assert.Same(boxes, result);
    }

    [Fact]
    public void ApplyGrowsOnlyTheSideThatWasCut()
    {
        using var image = Line();
        for (var i = 0; i < 6; i++) Dot(image, LastInk + 16 + i * 16, Top + 22);
        var boxes = new List<RapidOcrNet.TextBox> { Detector(Box) };

        var grown = Assert.Single(HorizontalLineEnds.Apply(image, [Rect(Box)], boxes, 1.0, 1.0, 0, out _));

        Assert.Equal(Box.Left, grown.BoxPoints.Min(p => p.X));
        Assert.Equal(Box.Top, grown.BoxPoints.Min(p => p.Y));
        Assert.Equal(Box.Bottom, grown.BoxPoints.Max(p => p.Y));
        Assert.True(grown.BoxPoints.Max(p => p.X) > Box.Right);
    }

    [Theory]
    // What the grown crop may add to what the detector's own box read.
    [InlineData("(ウソでしょ", "(ウソでしょ………", false, true, false, false, true)]        // user-4: symbols on the side that grew
    [InlineData("階堂ヒロ……ちゃん？）", "二階堂ヒロ……ちゃん？）", true, false, true, false, true)] // the glyph the box cut through
    [InlineData("ずっと待っていたのに", "ずっと待っていたのに", false, true, false, false, true)] // ―― the recogniser cannot read
    [InlineData("絡みに行く相手も", "絡みに行<相手も", true, false, false, false, false)]    // the words changed
    [InlineData("待ち続けることなんて", "X待ち続けることなんて", true, false, false, false, false)] // a letter past a gap
    [InlineData("Auto", "Auto……", true, false, false, false, false)]                  // added on a side that did not grow
    [InlineData("階堂", "二三階堂", true, false, true, false, true)]
    [InlineData("階堂", "一二三階堂", true, false, true, false, false)]                // more than a cut glyph
    [InlineData("", "……", false, true, false, false, false)]                          // nothing to agree with
    [InlineData("……！？", "……！？）", false, true, false, false, true)]                 // user-6: a box of symbols gains its ）
    [InlineData("……！？", "……！？あ", false, true, false, false, false)]
    [InlineData("-返事をしてよ。", "返事をしてよ。", true, false, false, false, false)]    // the grown side lost its dash
    public void AGrownReadingIsKeptOnlyWhenItAddsTheLinesEnds(
        string original, string grown, bool left, bool right, bool leftCut, bool rightCut, bool expected)
    {
        var growth = new HorizontalLineEnds.Growth(Detector(Box), left, right, leftCut, rightCut, RightThin: true);
        Assert.Equal(expected, HorizontalLineEnds.Accepts(original, grown, growth));
    }

    [Fact]
    public void ASideThatReadNothingMoreDoesNotGrow()
    {
        // 전설 grew onto the icon beside it and read the same: that side buys no reading, so it
        // keeps the detector's edge. The ―― put back by shape, a cut glyph read whole, and more
        // dots than the box gave are all something read.
        Assert.Equal((false, false), HorizontalLineEnds.ReadMore("전설", "전설"));
        Assert.Equal((false, true), HorizontalLineEnds.ReadMore("ずっと待っていたのに", "ずっと待っていたのに――"));
        Assert.Equal((true, false), HorizontalLineEnds.ReadMore("階堂ヒロ", "二階堂ヒロ"));
        Assert.Equal((false, true), HorizontalLineEnds.ReadMore("(ウソでしょ.", "(ウソでしょ……"));
        Assert.Equal((false, false), HorizontalLineEnds.ReadMore("（ウソでしょ", "(ウソでしょ"));
        Assert.Equal((false, true), HorizontalLineEnds.ReadMore("……！？", "……！？）"));
    }

    [Fact]
    public void OnlyTheSideThatReadMoreKeepsItsGrowth()
    {
        var original = Detector(Box);
        var grown = new RapidOcrNet.TextBox
        {
            Score = 0.9f,
            BoxPoints = [new(Box.Left - 30, Box.Top), new(Box.Right + 40, Box.Top), new(Box.Right + 40, Box.Bottom), new(Box.Left - 30, Box.Bottom)],
        };
        var growth = new HorizontalLineEnds.Growth(original, true, true, false, false);

        var kept = HorizontalLineEnds.Keep(grown, growth, left: false, right: true);

        Assert.Equal(Box.Left, kept.BoxPoints.Min(p => p.X));
        Assert.Equal(Box.Right + 40, kept.BoxPoints.Max(p => p.X));
    }

    [Fact]
    public void LatinLinesAreLeftAsTheDetectorDrewThem()
    {
        // Wrapped English paragraphs stopped grouping once one line was a few pixels wider, and no
        // Latin line was measured losing its ending this way.
        var growth = new HorizontalLineEnds.Growth(Detector(Box), false, true, false, true, RightThin: true);
        Assert.False(HorizontalLineEnds.Accepts("60 F", "60 FPS", growth));
        Assert.False(HorizontalLineEnds.Accepts("Wait", "Wait...", growth));
    }


    [Fact]
    public void AGrownReadingMayNotSinkAShortLineUnderTheRealtimeFloor()
    {
        // ……誰？ read less surely than 誰？ was: the line would be dropped where it had been kept.
        Assert.True(HorizontalLineEnds.SinksAShortLine(
            ["…", "…", "誰", "？"], [0.7f, 0.7f, 0.7f, 0.7f], ["誰", "？"], [0.9f, 0.85f]));
        // Both convincing, or both not: growth changed nothing about whether the line is kept.
        Assert.False(HorizontalLineEnds.SinksAShortLine(
            ["…", "…", "誰", "？"], [0.9f, 0.9f, 0.9f, 0.9f], ["誰", "？"], [0.9f, 0.85f]));
        Assert.False(HorizontalLineEnds.SinksAShortLine(
            ["…", "…", "誰", "？"], [0.7f, 0.7f, 0.7f, 0.7f], ["誰", "？"], [0.7f, 0.7f]));
    }

    [Theory]
    // A 40px glyph whose line is centred at y=60.
    [InlineData(0, 58, 70, 62, true)]     // ―― two glyphs long through the middle
    [InlineData(0, 58, 40, 62, false)]    // one glyph long: as likely 一 or ー
    [InlineData(0, 76, 70, 80, false)]    // along the bottom: an underline, a box's edge
    [InlineData(0, 54, 70, 66, false)]    // too thick for a stroke
    public void ADashIsKnownByItsShape(int left, int top, int right, int bottom, bool expected)
    {
        Assert.Equal(expected, HorizontalLineEnds.IsDash(new SKRectI(left, top, right, bottom), 60, 40));
    }

    [Fact]
    public void ADashTheRecogniserReadNothingOfIsPutBack()
    {
        var growth = new HorizontalLineEnds.Growth(Detector(Box), true, false, false, false, LeftDash: true);
        var put = HorizontalLineEnds.WithDashes(["そ", "の"], [0.9f, 0.9f], growth);
        Assert.NotNull(put);
        Assert.Equal("――その", string.Concat(put.Value.Chars));
        Assert.Equal(4, put.Value.Scores.Length);
        // Read already, even as a hyphen: left as the recogniser has it.
        Assert.Null(HorizontalLineEnds.WithDashes(["-", "そ", "の"], [0.5f, 0.9f, 0.9f], growth));
        // No lone dash on the side that grew: nothing to put back.
        Assert.Null(HorizontalLineEnds.WithDashes(["そ", "の"], [0.9f, 0.9f], growth with { LeftDash = false }));
    }

    [Fact]
    public void SymbolsTheWalkTookInAreScoredAsTheWords()
    {
        // ……誰？ grown on the left: the dots score low and would sink a short line under the
        // realtime floor. They take the words' own score; the ？ the detector's box already held,
        // and the right side that did not grow, keep theirs.
        string[] chars = ["…", "…", "誰", "？"];
        float[] scores = [0.4f, 0.4f, 0.9f, 0.8f];

        var judged = HorizontalLineEnds.JudgeOnTheWords(chars, scores, left: true, right: false);

        Assert.Equal(0.85, judged[0], 4);
        Assert.Equal(0.85, judged[1], 4);
        Assert.Equal(0.9, judged[2], 4);
        Assert.Equal(0.8, judged[3], 4);
        Assert.Same(scores, HorizontalLineEnds.JudgeOnTheWords(chars, scores, left: false, right: false));
        // Nothing but symbols: there are no words to judge by, so the scores stand.
        float[] dots = [0.4f, 0.4f];
        Assert.Same(dots, HorizontalLineEnds.JudgeOnTheWords(["…", "…"], dots, left: true, right: true));
    }

    [Theory]
    [InlineData("……！？）", true)]
    [InlineData("！？)", true)]
    [InlineData("——", true)]
    [InlineData("。", true)]
    [InlineData("DM", false)]
    [InlineData("回", false)]
    [InlineData("…a", false)]
    [InlineData("", false)]
    public void OnlyLineEdgeSymbolsCountAsLineEdgeSymbols(string text, bool expected)
    {
        Assert.Equal(expected, ShortReadingDetection.IsLineEdgeSymbols(text));
    }

    [Fact]
    public void AnUnsureRunOfSymbolsAgainstALineIsKeptForGrouping()
    {
        // user-6 nudged 8px: the line's end came back as its own box, read with 0.73.
        var line = new OcrTextBlock("（え、何！？フ、フクロウの化け物……", new System.Windows.Rect(181, 24, 834, 47), Confidence: 0.96);
        var tail = new OcrTextBlock("！？)", new System.Windows.Rect(1014, 31, 118, 35), Confidence: 0.73);

        var kept = OcrService.RejectUnconvincingBlocks([line, tail]);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void AnUnsureRunOfSymbolsOnItsOwnStillGoes()
    {
        var line = new OcrTextBlock("（え、何！？フ、フクロウの化け物……", new System.Windows.Rect(181, 24, 834, 47), Confidence: 0.96);
        // Two lines further down: nothing for it to belong to.
        var stray = new OcrTextBlock("！？)", new System.Windows.Rect(1014, 140, 118, 35), Confidence: 0.73);

        Assert.Single(OcrService.RejectUnconvincingBlocks([line, stray]));
    }

    [Fact]
    public void UnsureSceneryBesideALineStillGoes()
    {
        // The measured scenery readings are letters and glyphs; being next to a line saves none.
        var line = new OcrTextBlock("（え、何！？フ、フクロウの化け物……", new System.Windows.Rect(181, 24, 834, 47), Confidence: 0.96);
        var noise = new OcrTextBlock("DM", new System.Windows.Rect(1020, 28, 60, 40), Confidence: 0.70);

        Assert.Single(OcrService.RejectUnconvincingBlocks([line, noise]));
    }

    private static SKBitmap Line(int width = 640)
    {
        var image = new SKBitmap(width, 140);
        using var canvas = new SKCanvas(image);
        canvas.Clear(Background);
        for (var i = 0; i < Glyphs; i++) Square(image, FirstGlyph + i * Glyph + 3, Top);
        return image;
    }

    // A glyph: the outline of a square, 3px strokes, a few pixels of side bearing.
    private static void Square(SKBitmap image, int left, int top)
    {
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint { Color = White, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        canvas.DrawRect(left + 1.5f, top + 1.5f, Glyph - 9, Glyph - 3, paint);
    }

    private static void Dot(SKBitmap image, int left, int top, SKColor? colour = null) =>
        Fill(image, left, top, left + 5, top + 5, colour);

    private static void Fill(SKBitmap image, int left, int top, int right, int bottom, SKColor? colour = null)
    {
        using var canvas = new SKCanvas(image);
        using var paint = new SKPaint { Color = colour ?? White, IsAntialias = false };
        canvas.DrawRect(SKRect.Create(left, top, right - left, bottom - top), paint);
    }

    private static System.Windows.Rect Rect(SKRectI r) => new(r.Left, r.Top, r.Width, r.Height);

    private static RapidOcrNet.TextBox Detector(SKRectI r) => new()
    {
        Score = 0.9f,
        BoxPoints = [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)],
    };
}
