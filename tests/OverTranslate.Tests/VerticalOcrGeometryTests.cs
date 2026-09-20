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
        Assert.Equal("一二三四左右", Assert.Single(OcrService.GroupVertical(blocks, 200)).Text);
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
        var groups = OcrService.GroupVertical(blocks, 700);
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

    [Fact]
    public void NarrowUnassignedInk_KeepsOriginalRatherThanLosingPunctuation()
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
        var box = Box(8, 10, 76, 180);
        Assert.Same(box, Assert.Single(VerticalColumnDetection.Split(pixels, [box])));
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
        Assert.Single(OcrService.GroupVertical(blocks, 100, realtime: true));
    }

    private static TextBox Box(int x, int y, int width, int height) => new()
    {
        Score = 0.9f,
        BoxPoints = [new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height)],
    };
}
