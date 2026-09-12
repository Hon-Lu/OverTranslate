using OverTranslate.Services.Ocr;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The repair fires on a broken row of coloured text and on nothing else.
/// </summary>
/// <remarks>
/// Written against synthetic pixels rather than captures, because what has to hold is the shape of
/// the conditions, not one page's luck: a corpus run says the rule fired twice in 413 captures, but
/// not which condition kept it quiet on the other 411. Each case below removes exactly one.
/// </remarks>
public class ChromaticBoxRepairTests
{
    private const int Height = 24;
    private const int Top = 20;

    [Fact]
    public void ABrokenRowOfColouredTextIsRejoined()
    {
        using var page = Page();
        var repairs = ChromaticBoxRepair.Find(page, [Box(20, 120), Box(200, 120)]);

        var repair = Assert.Single(repairs);
        Assert.Equal(new[] { 0, 1 }, repair.Owners.ToArray().AsEnumerable());
        // Outside both pieces and the ink between them, which is the gap that had no box at all.
        Assert.True(repair.Bounds.Left <= 20 && repair.Bounds.Right >= 320);
    }

    [Fact]
    public void APaleBackgroundIsNotThisCase()
    {
        // The symptom is a detector fading out on thin colour against flat dark. On a light page
        // the boxes are not broken, so there is nothing here to rejoin and no reason to look.
        using var page = Page(background: new SKColor(240, 240, 244));

        Assert.Empty(ChromaticBoxRepair.Find(page, [Box(20, 120), Box(200, 120)]));
    }

    [Fact]
    public void SeparateItemsWithRealSpaceBetweenThemAreNotOneRow()
    {
        // A nav bar or a row of labels: the same colour, the same line, genuinely separate. The ink
        // stops for longer than a line height, and that is what tells it apart from a broken word.
        using var page = Page(gapFrom: 150, gapTo: 150 + Height * 2);

        Assert.Empty(ChromaticBoxRepair.Find(page, [Box(20, 120), Box(200, 120)]));
    }

    [Fact]
    public void ARowTheDetectorAlreadyCoveredIsLeftAlone()
    {
        // Nothing is missing, so nothing is repaired — a complete reading must never be reboxed.
        using var page = Page();

        Assert.Empty(ChromaticBoxRepair.Find(page, [Box(18, 304)]));
    }

    [Fact]
    public void ColouredInkWithoutASubstantialBoxIsNotTreatedAsText()
    {
        // Scenery, a logo, a progress bar: ink the detector refused. Without a real box on the row
        // to anchor it, a repair would be handing recognition something never detected as text.
        using var page = Page();

        Assert.Empty(ChromaticBoxRepair.Find(page, [Box(20, 40), Box(200, 40)]));
    }

    [ScreenshotFact("web-v4/3.png")]
    public void TheColouredTitleIsReadWhole()
    {
        // The capture that started this. The detector returns the title as four pieces and the
        // trailing glyphs sit in the gaps between them, so before the repair the reading stopped at
        // 公式サイ. Runs the real screenshot entry point, not the seam directly.
        using var engine = new OnnxOcrEngine();
        using var capture = ExternalScreenshot.Load("web-v4/3.png");

        var text = string.Concat(
            engine.RecognizeAsync(capture, "JA").GetAwaiter().GetResult().Select(block => block.Text));

        Assert.Contains("公式サイト", text);
    }

    // A dark page carrying one row of thin coloured strokes: ink enough to be a line of text, gaps
    // narrow enough to be the spaces inside one, and nowhere near solid enough to be a filled panel.
    private static SKBitmap Page(SKColor? background = null, int gapFrom = 0, int gapTo = 0)
    {
        var bg = background ?? new SKColor(24, 24, 32);
        var page = new SKBitmap(400, 80, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(page))
            canvas.Clear(bg);

        var ink = new SKColor(120, 160, 220);
        for (var x = 20; x < 320; x += 6)
        {
            if (x >= gapFrom && x < gapTo) continue;
            for (var stroke = 0; stroke < 2; stroke++)
            for (var y = Top; y < Top + 20; y++)
                page.SetPixel(x + stroke, y, ink);
        }
        return page;
    }

    private static SKRect Box(int left, int width) =>
        new(left, Top, left + width, Top + Height);
}
