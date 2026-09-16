using OverTranslate.Services.Ocr;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The repair fires on a broken row of text and on nothing else.
/// </summary>
/// <remarks>
/// Written against synthetic pixels rather than captures, because what has to hold is the shape of
/// the conditions, not one page's luck: a corpus run says this changed two captures out of 497, but
/// not which condition kept it quiet on the other 495. Each case below removes exactly one. The two
/// at the end read real captures instead, one for each half of the symptom.
/// </remarks>
public class ChromaticBoxRepairTests
{
    private const int Height = 24;
    private const int Top = 20;
    private const int SecondRow = 60;

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
    public void AGreyRowBreaksTheSameWayAndIsRejoinedToo()
    {
        // Dark-mode body text is grey, and it breaks exactly as the coloured titles do. The rule
        // used to ask the whole row to be 60% coloured ink, which body text never is — on the
        // search results page this came from, those rows measured 28-30% — so the symptom was out
        // of reach by construction. What still has to be coloured is the evidence: the glyphs no
        // box covers. On a real capture that costs nothing, because subpixel rendering leaves a
        // hue on every stroke edge; measured on the two captures a user sent, 40% of their ink
        // reads as coloured against 8-18% for the same pages rendered headless.
        using var page = GreyRowWithHuedGlyphsInTheGap();

        var repair = Assert.Single(ChromaticBoxRepair.Find(page, [Box(20, 120), Box(200, 120)]));
        Assert.Equal(new[] { 0, 1 }, repair.Owners.ToArray().AsEnumerable());
    }

    [Fact]
    public void TwoRowsBridgedByAnIconAreNotOneRow()
    {
        // What the row-wide chroma test was really buying. A search result puts a site icon down
        // the left of the site name and the URL beneath it; the icon joins both rows into one
        // connected band of ink, and a repair spanning the band hands recognition a box holding two
        // lines, which came back garbled or empty. The owners here are on different lines and share
        // no height, which is the thing that says so without asking what colour anything is — worth
        // 12 captures of the corpus's 497, all of them a title and its URL going missing.
        using var page = TwoRowsBridgedByAnIcon();

        Assert.Empty(ChromaticBoxRepair.Find(page, [Row(Top), Row(SecondRow)]));
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

    [ScreenshotFact("region-web-ja-dark/wiki-hatnote-narrow-x15.png")]
    public void AGreyRowKeepsItsLeadingGlyph()
    {
        // A dark-mode Japanese Wikipedia page at 150% scale, framed the way a user frames a
        // paragraph. The detector starts this hatnote one glyph late and the leading は is never
        // recognised — the other half of the same symptom as the broken title, on a row that is
        // grey rather than coloured, which is why the rule could not reach it while it asked the
        // whole row to be coloured ink.
        using var engine = new OnnxOcrEngine();
        using var capture = ExternalScreenshot.Load("region-web-ja-dark/wiki-hatnote-narrow-x15.png");

        var text = string.Concat(
            engine.RecognizeAsync(capture, "JA").GetAwaiter().GetResult().Select(block => block.Text));

        Assert.Contains("は「バンドリ", text);
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

    // Two rows of text with a solid block down their left, the shape a favicon makes beside a site
    // name and its URL. The block is what joins the two rows into one band of ink; without it the
    // rows are two bands and never meet.
    private static SKBitmap TwoRowsBridgedByAnIcon()
    {
        var page = new SKBitmap(640, 120, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(page))
            canvas.Clear(new SKColor(24, 24, 32));

        var ink = new SKColor(120, 160, 220);
        for (var x = 20; x < 44; x++)
        for (var y = Top; y < SecondRow + Height; y++)
            page.SetPixel(x, y, ink);

        foreach (var row in new[] { Top, SecondRow })
        for (var x = 60; x < 560; x += 6)
        {
            for (var stroke = 0; stroke < 2; stroke++)
            for (var y = row; y < row + 20; y++)
                page.SetPixel(x + stroke, y, ink);
        }
        return page;
    }

    private static SKRect Row(int top) => new(60, top, 260, top + 20);

    // Neutral grey strokes across the row, except in the stretch between the two boxes, which
    // carries the faint hue a screen capture puts on a glyph edge. One fifth of the row's ink reads
    // as coloured, well under the 60% the rule used to demand of the whole row.
    private static SKBitmap GreyRowWithHuedGlyphsInTheGap()
    {
        var page = new SKBitmap(400, 80, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(page))
            canvas.Clear(new SKColor(24, 24, 32));

        for (var x = 20; x < 320; x += 6)
        {
            var ink = x is >= 140 and < 200 ? new SKColor(150, 170, 190) : new SKColor(176, 176, 178);
            for (var stroke = 0; stroke < 2; stroke++)
            for (var y = Top; y < Top + 20; y++)
                page.SetPixel(x + stroke, y, ink);
        }
        return page;
    }

    private static SKRect Box(int left, int width) =>
        new(left, Top, left + width, Top + Height);
}
