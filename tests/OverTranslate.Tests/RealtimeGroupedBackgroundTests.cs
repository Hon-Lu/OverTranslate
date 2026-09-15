using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Services;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeGroupedBackgroundTests
{
    private static TranslatedBlock Lisa(string translation = "好，Lisa。從入口再來一次。") => new(
        "Okay, Lisa. Do it again, from the entrance.", translation,
        new Rect(571, 17, 610, 157),
        [new Rect(571, 17, 610, 98), new Rect(627, 101, 504, 73)], 40.95);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void EnglishGroupCoversCompactLinesAndTheirGap(double dpi) => OnSta(() =>
    {
        var (background, _) = Build(Lisa(), dpi);
        // Centers are 66 and 137.5; each compact line is 40.95 * 1.22 high.
        Assert.Equal((71.5 + 40.95 * 1.22) / dpi + 6, background.Height, 6);
        Assert.Equal((66 - 40.95 * 1.22 / 2) / dpi - 3, Canvas.GetTop(background), 6);
    });

    [Fact]
    public void CjkNormalizedGroupKeepsItsCoverage() => OnSta(() =>
    {
        var line = Lisa() with { Bounds = new Rect(571, 48, 610, 108.1),
            SourceLineBounds = [new Rect(571, 48, 610, 36), new Rect(627, 118.9, 504, 37.2)],
            RenderGlyphHeight = null };
        var (background, _) = Build(line);
        Assert.Equal(114.1, background.Height, 6);
    });

    [Fact]
    public void SingleLineStillUsesItsExistingCompactBand() => OnSta(() =>
    {
        var line = Lisa("好的。") with { Bounds = new Rect(571, 17, 610, 98), SourceLineBounds = null };
        var (background, _) = Build(line);
        Assert.InRange(background.Height, 49, 65);
    });

    [Fact]
    public void ThreeRowsKeepTheirSpacingWithoutReusingGroupHeightAsFontHeight() => OnSta(() =>
    {
        var line = Lisa("三行來源。") with { Bounds = new Rect(571, 0, 610, 170),
            SourceLineBounds = [new Rect(571, 0, 610, 50), new Rect(571, 60, 610, 50), new Rect(571, 120, 610, 50)],
            RenderGlyphHeight = 20 };
        var (background, _) = Build(line);
        Assert.Equal(120 + 20 * 1.22 + 6, background.Height, 6);
        Assert.Equal(25 - 20 * 1.22 / 2 - 3, Canvas.GetTop(background), 6);
    });

    [Fact]
    public void LongTranslationCanGrowPastCompactSourceCoverage() => OnSta(() =>
    {
        var line = Lisa(string.Concat(Enumerable.Repeat("這是一段需要保留全部內容的較長翻譯。", 30)));
        var (background, text) = Build(line);
        Assert.True(text.Height > 71.5 + 40.95 * 1.22);
        Assert.Equal(background.Height, text.Height);
        var child = Assert.IsType<TextBlock>(text.Child);
        child.Measure(new Size(text.Width - text.Padding.Left - text.Padding.Right, double.PositiveInfinity));
        Assert.True(background.Height >= child.DesiredSize.Height + 6);
        Assert.Equal(TextTrimming.None, child.TextTrimming);
        Assert.Equal(line.TranslatedText, child.Text);
    });

    private static (Border Background, Border Text) Build(TranslatedBlock line, double dpi = 1)
    {
        var window = new RealtimeBlockWindow(0, new System.Drawing.Rectangle(0, 0, 1770, 210),
            _ => null, "EN", "ZH-HANT", "#FFAA55", "#000000", 70);
        try
        {
            var type = typeof(RealtimeBlockWindow);
            type.GetField("_dpiY", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, dpi);
            type.GetField("_dpiX", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, dpi);
            var visual = type.GetMethod("BuildLine", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [line, 1770 / dpi, 210 / dpi, null, null])!;
            return ((Border)visual.GetType().GetProperty("Background")!.GetValue(visual)!,
                (Border)visual.GetType().GetProperty("Text")!.GetValue(visual)!);
        }
        finally { window.Close(); }
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
