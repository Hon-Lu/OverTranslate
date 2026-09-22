using System.Reflection;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using RapidOcrNet;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

public class VerticalOcrGeometryTests
{
    [Fact]
    public void RecognitionCrop_ReadsTopToBottomLeftToRightWithoutChangingDetectionQuad()
    {
        using var source = new SKBitmap(30, 90);
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(5, 10, 20, 30, paint);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(5, 40, 20, 30, paint);
        }
        var detection = Box(5, 10, 20, 60);
        var points = detection.BoxPoints.ToArray();
        var cropBox = VerticalOcrGeometry.ForRecognition(detection);
        var method = typeof(RapidOcr).Assembly.GetType("RapidOcrNet.OcrUtils")!
            .GetMethod("GetPartImages", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var crops = (SKBitmap[])method.Invoke(null, [source, new List<TextBox> { cropBox }])!;
        try
        {
            var crop = Assert.Single(crops);
            Assert.Equal(60, crop.Width);
            Assert.Equal(20, crop.Height);
            Assert.Equal(SKColors.Red, crop.GetPixel(10, 10));
            Assert.Equal(SKColors.Blue, crop.GetPixel(45, 10));
            Assert.Equal(points, detection.BoxPoints);
        }
        finally { foreach (var crop in crops) crop.Dispose(); }
    }

    [Fact]
    public void ShortCrop_DoesNotTurnUprightSingleGlyph()
    {
        var box = Box(20, 30, 20, 20);
        Assert.Same(box, VerticalOcrGeometry.ForRecognition(box));
    }

    [Fact]
    public void VerticalMetrics_KeepCoverageAndUseCharacterPitch()
    {
        var bounds = new Rect(17, 31, 30, 100);
        var block = Assert.Single(VerticalOcrGeometry.PrepareBlocks(
            [new OcrTextBlock("直排文字列", bounds, Confidence: 0.95)]));
        Assert.Equal(bounds, block.Bounds);
        Assert.Equal(bounds, block.LayoutBounds);
        Assert.Equal(20, block.LayoutGlyphHeight);
        Assert.Equal(20, block.RenderGlyphHeight);
        Assert.Equal(OcrLayoutScript.Cjk, block.LayoutScript);
    }

    [Fact]
    public void Fragments_JoinDownColumnBeforeReadingNextColumn()
    {
        var blocks = VerticalOcrGeometry.PrepareBlocks([
            new("三四", new Rect(80, 42, 20, 40), Confidence: 0.8),
            new("左右", new Rect(50, 0, 20, 40), Confidence: 0.9),
            new("一二", new Rect(80, 0, 20, 40), Confidence: 1),
        ]);
        var joined = VerticalOcrGeometry.JoinColumnFragments(blocks);
        Assert.Equal(2, joined.Count);
        var column = Assert.Single(joined.Where(b => b.Text == "一二三四"));
        Assert.Equal(new Rect(80, 0, 20, 82), column.Bounds);
        Assert.Equal(0.9, column.Confidence!.Value, 6);
        Assert.Equal("一二三四左右", Assert.Single(VerticalColumnGrouping.Group(blocks, 200)).Text);
    }

    [Fact]
    public void Divider_PreventsJoiningAcrossBalloonBoundary()
    {
        using var pixels = new SKBitmap(120, 140);
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 2 };
            canvas.DrawLine(20, 53, 90, 53, paint);
        }
        var blocks = VerticalOcrGeometry.PrepareBlocks([
            new("前文", new Rect(40, 10, 20, 40)),
            new("後文", new Rect(40, 55, 20, 40)),
        ]);
        Assert.Single(VerticalOcrGeometry.JoinColumnFragments(blocks));
        Assert.Equal(2, VerticalOcrGeometry.JoinColumnFragments(blocks, pixels).Count);
    }

    [Fact]
    public void WidePadding_DoesNotBridgeSeparateSpeakers()
    {
        var blocks = VerticalOcrGeometry.PrepareBlocks([
            new("どうしたんですか？", new Rect(422, 15, 30, 156)),
            new("うちで働くスタッフに", new Rect(383, 16, 25, 165)),
            new("日本語が伝わらなくてね", new Rect(364, 17, 25, 186)),
        ]);
        var groups = VerticalColumnGrouping.Group(blocks, 700);
        Assert.Equal(2, groups.Count);
        Assert.Equal("どうしたんですか？", groups[0].Text);
        Assert.Equal("うちで働くスタッフに日本語が伝わらなくてね", groups[1].Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitMergedColumn_UsesBlankGutterOnEitherPolarity(bool darkBackground)
    {
        using var pixels = new SKBitmap(80, 180);
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(darkBackground ? SKColors.Black : SKColors.White);
            using var paint = new SKPaint { Color = darkBackground ? SKColors.White : SKColors.Black };
            for (var y = 20; y < 140; y += 24)
            {
                canvas.DrawRect(16, y, 12, 12, paint);
                canvas.DrawRect(44, y, 12, 12, paint);
            }
        }
        var box = Box(10, 10, 54, 150);
        var result = VerticalColumnDetection.Split(pixels, [box]);
        Assert.Equal(2, result.Count);
        Assert.True(result[0].BoxPoints.Max(p => p.X) < result[1].BoxPoints.Min(p => p.X));
        Assert.All(result, column => Assert.Equal(box.Score, column.Score));
        Assert.Equal(64, box.BoxPoints[1].X);
    }

    /// <summary>
    /// A narrow piece of ink beside the columns becomes a part of its own, and the cut still
    /// happens.
    /// </summary>
    /// <remarks>
    /// <para>This used to assert that the box was kept WHOLE — anything under eight columns of ink
    /// abandoned the cut, so as not to silently discard a punctuation or ruby fragment. The intent
    /// was right and the means were not: what abandoning the cut discards is the SENTENCE.</para>
    ///
    /// <para>MEASURED on a frame the app captured while the user read a comic
    /// (logs/frames/region0-084148-301-primaryok-p1832.png). The detector draws one box 180 wide
    /// around the whole balloon パーティに付与術士が必要になったから; the background is flat and
    /// every gutter is there, and one 11-pixel ruby column between the columns took the cut away.
    /// A 180-wide crop holding three columns at once reads as nothing, so the balloon was lost —
    /// every pass, because the shape of the box does not change. The user reported that balloon.
    /// </para>
    ///
    /// <para>The narrow piece is still not discarded. It comes out as its own box, which is what
    /// the readings machinery downstream is for.</para>
    /// </remarks>
    [Fact]
    public void NarrowUnassignedInk_BecomesItsOwnPartRatherThanTakingTheCutAway()
    {
        using var pixels = new SKBitmap(100, 220);
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            for (var y = 20; y < 170; y += 24)
            {
                canvas.DrawRect(14, y, 12, 12, paint);
                canvas.DrawRect(42, y, 12, 12, paint);
            }
            canvas.DrawRect(70, 40, 4, 20, paint);
        }

        var parts = VerticalColumnDetection.Split(pixels, [Box(8, 10, 76, 180)]);

        Assert.Equal(3, parts.Count);
        // The two columns, and the narrow piece — which is kept rather than dropped.
        var widths = parts
            .Select(part => part.BoxPoints.Max(p => p.X) - part.BoxPoints.Min(p => p.X))
            .OrderBy(width => width)
            .ToArray();
        Assert.True(widths[0] < 14, $"the narrow piece came out {widths[0]} wide");
        Assert.All(widths.Skip(1), width => Assert.InRange(width, 14, 30));
    }

    [Fact]
    public void ContinuousInkAndSlantedQuads_AreNotSplit()
    {
        using var pixels = new SKBitmap(80, 180);
        pixels.Erase(SKColors.Black);
        var box = Box(10, 10, 54, 150);
        Assert.Same(box, Assert.Single(VerticalColumnDetection.Split(pixels, [box])));
        pixels.Erase(SKColors.White);
        box.BoxPoints[3] = new SKPointI(20, 160);
        Assert.Same(box, Assert.Single(VerticalColumnDetection.Split(pixels, [box])));
    }

    [Fact]
    public void RealtimeCollapse_IsMeasuredAcrossColumnsNotDownTheirHeight()
    {
        var blocks = VerticalOcrGeometry.PrepareBlocks([
            new("正しい縦書きの文章です", new Rect(80, 0, 20, 240), Confidence: 0.99),
        ]);
        Assert.Single(VerticalColumnGrouping.Group(blocks, 100, realtime: true));
    }

    private static TextBox Box(int x, int y, int width, int height) => new()
    {
        Score = 0.9f,
        BoxPoints = [new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)],
    };
}
