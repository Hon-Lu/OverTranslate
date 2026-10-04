using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The geometry grouping measures with, which must not depend on the source language the user
/// picked. Bounds may still be normalised per script — that is the overlay's business.
/// </summary>
public class OcrLayoutGeometryTests
{
    [Fact]
    public void LayoutBounds_IsIdenticalAcrossSourceLanguages_ForSameRawDetection()
    {
        var detected = new System.Windows.Rect(40, 120, 240, 30);
        List<OcrTextBlock> Blocks() =>
        [
            new("Shots deal more damage", detected),
            new("彈匣內每發子彈的傷害", detected),
        ];

        var latin = OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false);
        var cjk = OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true);
        var automatic = OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks());

        foreach (var normalized in new[] { latin, cjk, automatic })
        {
            Assert.Equal(detected, normalized[0].LayoutBounds);
            Assert.Equal(detected, normalized[1].LayoutBounds);
        }

        // The 0.820 that this exists to get away from: Bounds still differs by script, and still
        // differs between the paths the source language routes to. Grouping must stop reading it.
        Assert.NotEqual(latin[1].Bounds.Height, cjk[1].Bounds.Height);
    }

    [Fact]
    public void LayoutBounds_IsTheDetectionBox_NotTheNormalisedOne()
    {
        var detected = new System.Windows.Rect(0, 0, 120, 30);
        var normalized = OnnxOcrEngine.NormalizeBlocks([new("設定設定", detected)], useCjkRenderMetrics: true);

        Assert.Equal(detected, normalized[0].LayoutBounds);
        Assert.True(normalized[0].Bounds.Height < detected.Height);
    }

    [Fact]
    public void LayoutGlyphHeight_IsIdenticalAcrossSourceLanguages_ForSameRawDetection()
    {
        var detected = new System.Windows.Rect(40, 120, 240, 30);
        List<OcrTextBlock> Blocks() =>
        [
            new("Shots deal more damage", detected),
            new("彈匣內每發子彈的傷害", detected),
        ];

        var latin = OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false);
        var cjk = OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true);
        var automatic = OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks());

        Assert.Equal(latin.Select(b => b.LayoutGlyphHeight), cjk.Select(b => b.LayoutGlyphHeight));
        Assert.Equal(latin.Select(b => b.LayoutGlyphHeight), automatic.Select(b => b.LayoutGlyphHeight));

        // The render metric is what still differs: it is keyed on the language route, on purpose.
        Assert.NotEqual(latin[0].RenderGlyphHeight, automatic[0].RenderGlyphHeight);
    }

    [Fact]
    public void CjkLayoutGlyphHeight_IsAnEstimate_NotTheDetectionBox()
    {
        var detected = new System.Windows.Rect(0, 0, 120, 30);
        var normalized = OnnxOcrEngine.NormalizeBlocks([new("設定設定", detected)], useCjkRenderMetrics: true);

        var height = Assert.IsType<double>(normalized[0].LayoutGlyphHeight);
        Assert.True(height < normalized[0].LayoutBounds.Height);
        // Same 0.82 shrink and pitch clamp the overlay's own CJK box carries.
        Assert.Equal(normalized[0].Bounds.Height, height, precision: 9);
    }

    [Theory]
    [InlineData("甲Glossaries")]
    [InlineData("→")]
    public void MixedAndUnknownBlocks_CarryNoLayoutGlyphHeight(string text)
    {
        var detected = new System.Windows.Rect(0, 0, 240, 30);
        var normalized = OnnxOcrEngine.NormalizeBlocks([new(text, detected)], useCjkRenderMetrics: false);

        Assert.Null(normalized[0].LayoutGlyphHeight);
    }

    /// <summary>
    /// The shape filter must reach the same verdict whatever the user picked, which is only true
    /// while it measures the detection box rather than the normalised one.
    /// </summary>
    /// <remarks>
    /// The fixture is the case this was found on: a 74x40 detection holding one full-width glyph.
    /// Normalised for CJK the box becomes 74x32.8, and 74 / 1 / 32.8 = 2.26 crosses the 2.0 bar
    /// while the detection's own 1.85 does not. Under 日文 the label was deleted and under 英文 it
    /// survived — the same picture, the same reading, a different menu setting. Eighteen blocks of
    /// the corpus went that way, among them 闇, 三, 決定 and 결정.
    /// </remarks>
    [Fact]
    public void ShapeFilter_ReachesTheSameVerdict_WhateverTheSourceLanguage()
    {
        var detected = new System.Windows.Rect(10, 10, 74, 40);
        List<OcrTextBlock> Blocks() => [new("闇", detected)];

        var latin = OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false);
        var cjk = OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true);
        var automatic = OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks());

        // The fixture is only worth anything if the two rectangles disagree across the threshold.
        Assert.NotEqual(cjk[0].Bounds, cjk[0].LayoutBounds);
        Assert.True(cjk[0].Bounds.Width / cjk[0].Bounds.Height >= BoxShapeNoise.MaxWidthPerGlyph);
        Assert.True(cjk[0].LayoutBounds.Width / cjk[0].LayoutBounds.Height < BoxShapeNoise.MaxWidthPerGlyph);

        foreach (var normalized in new[] { latin, cjk, automatic })
        {
            Assert.False(BoxShapeNoise.IsTooWideForItsText(normalized[0]));
            Assert.Single(OnnxOcrEngine.RemoveMisshapenBlocks(normalized, "JA"));
        }
    }

    /// <summary>
    /// And the same in the other direction: a box that really is too wide goes, on every language.
    /// </summary>
    [Fact]
    public void ShapeFilter_StillDropsAWidelyMisshapenBox_WhateverTheSourceLanguage()
    {
        var detected = new System.Windows.Rect(10, 10, 300, 40);
        List<OcrTextBlock> Blocks() => [new("□", detected)];

        foreach (var normalized in new[]
        {
            OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false),
            OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true),
            OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks()),
        })
        {
            Assert.True(BoxShapeNoise.IsTooWideForItsText(normalized[0]));
            Assert.Empty(OnnxOcrEngine.RemoveMisshapenBlocks(normalized, "JA"));
        }
    }
}
