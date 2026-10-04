using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class InlineProseGroupingTests
{
    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void WrappedProseRejoinsFragmentsAcrossAnUnrecognisedDash(double scale)
    {
        var blocks = Input(scale);
        var trace = new GroupingTrace();
        OcrTextBlockGrouper.Group(blocks, GroupingProfile.General, null, trace);
        Assert.Contains(trace.Lines, line => line.SourceIds.SequenceEqual(new[] { "b0", "b1" }));
        Assert.Single(trace.Groups);
    }

    [Fact]
    public void SeparateColumnsWithoutASpanningContinuationStaySeparate()
    {
        var blocks = Input(1).Take(2).ToArray();
        Assert.Equal(2, OcrTextBlockGrouper.Group(blocks, GroupingProfile.General).Count);
    }

    private static OcrTextBlock[] Input(double scale)
    {
        OcrTextBlock B(string text, double x, double y, double w, double h)
        {
            var box = new Rect(x * scale, y * scale, w * scale, h * scale);
            var script = LayoutScriptDetection.For(text);
            return new(text, box, LayoutScript: script, LayoutBounds: box,
                LayoutGlyphHeight: OnnxOcrEngine.LayoutGlyphHeightFor(script, box, text));
        }
        return [B("Screen translator for Windows", 32, 67, 242, 23),
            B("screenshot, real-time, text selection & quick", 291, 70, 345, 18),
            B("translation, with results displayed directly on screen. | Windows 螢幕翻譯工", 31, 92, 600, 26)];
    }
}
