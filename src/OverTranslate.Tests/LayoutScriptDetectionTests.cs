using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The layout side's view of a block's script, which must come from the text and nothing else.
/// </summary>
public class LayoutScriptDetectionTests
{
    [Theory]
    [InlineData("Shots deal more damage", OcrLayoutScript.Latin)]
    [InlineData("Lv100", OcrLayoutScript.Latin)]
    [InlineData("2026/07/24 23:29", OcrLayoutScript.Latin)]
    [InlineData("彈匣內每發子彈", OcrLayoutScript.Cjk)]
    [InlineData("カタカナ", OcrLayoutScript.Cjk)]
    // One Han character is a full-width glyph like any other. Two was only ever needed to tell
    // Chinese from Japanese, which the layout side never asks. See #161.
    [InlineData("攻", OcrLayoutScript.Cjk)]
    [InlineData("→", OcrLayoutScript.Unknown)]
    [InlineData("", OcrLayoutScript.Unknown)]
    public void Script_IsReadOffTheTextItself(string text, OcrLayoutScript expected)
    {
        Assert.Equal(expected, LayoutScriptDetection.For(text));
    }

    [Theory]
    // Not an edge case: a line of Japanese technical prose carrying Western terms, a game panel
    // labelling a stat, and a wiki heading are all ordinary content.
    [InlineData("甲Glossaries")]
    [InlineData("文A日本語")]
    [InlineData("本Wikiについて")]
    [InlineData("2005 年から CSS, HTML, JavaScript のドキュメントを作成しています。")]
    public void MixedScriptBlock_IsClassifiedAsMixed(string text)
    {
        Assert.Equal(OcrLayoutScript.Mixed, LayoutScriptDetection.For(text));
    }

    [Fact]
    public void LayoutScript_IsChosenPerBlock_RegardlessOfSourceLanguage()
    {
        var bounds = new System.Windows.Rect(0, 0, 240, 30);
        List<OcrTextBlock> Blocks() =>
        [
            new("OPTIONS", bounds),
            new("ゲーム設定", bounds),
            new("甲Glossaries", bounds),
            new("文A日本語", bounds),
            new("攻", bounds),
        ];

        // The three normalisation paths the source language routes to today.
        var latin = OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false);
        var cjk = OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true);
        var automatic = OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks());

        OcrLayoutScript[] expected =
        [
            OcrLayoutScript.Latin,
            OcrLayoutScript.Cjk,
            OcrLayoutScript.Mixed,
            OcrLayoutScript.Mixed,
            OcrLayoutScript.Cjk,
        ];

        Assert.Equal(expected, latin.Select(block => block.LayoutScript));
        Assert.Equal(expected, cjk.Select(block => block.LayoutScript));
        Assert.Equal(expected, automatic.Select(block => block.LayoutScript));
    }

    [Fact]
    public void MixedScriptClassification_IsIndependentOfSourceLanguage()
    {
        var bounds = new System.Windows.Rect(0, 0, 240, 30);
        List<OcrTextBlock> Blocks() => [new("本Wikiについて", bounds)];

        Assert.Equal(OcrLayoutScript.Mixed, OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false)[0].LayoutScript);
        Assert.Equal(OcrLayoutScript.Mixed, OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true)[0].LayoutScript);
        Assert.Equal(OcrLayoutScript.Mixed, OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks())[0].LayoutScript);
    }

    /// <summary>
    /// Normalisation classifies the text it is given and never rewrites it, so whatever reaches it
    /// reaches every source language identically.
    /// </summary>
    /// <remarks>
    /// The lone-ideograph cleanup used to run on the Latin and automatic paths and not on the CJK
    /// one, which took the leading Han off each of these under 英文 and 自動 and left it under 日文.
    /// For 甲Glossaries that changed the answer outright — Mixed became Latin. It now runs once,
    /// earlier, for all four alike, and it will not touch 文A日本語 or 本Wikiについて at all — the
    /// latter is a heading, not noise. 甲Glossaries is stripped before it gets here; what this
    /// guards is that normalisation itself is not a second place where text can change.
    /// </remarks>
    [Theory]
    [InlineData("甲Glossaries")]
    [InlineData("文A日本語")]
    [InlineData("本Wikiについて")]
    public void MixedScriptText_IsKeptVerbatim_OnEverySourceLanguage(string text)
    {
        var bounds = new System.Windows.Rect(0, 0, 240, 30);
        List<OcrTextBlock> Blocks() => [new(text, bounds)];

        foreach (var normalized in new[]
        {
            OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false),
            OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true),
            OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks()),
        })
        {
            var block = Assert.Single(normalized);
            Assert.Equal(text, block.Text);
            Assert.Equal(OcrLayoutScript.Mixed, block.LayoutScript);
        }
    }

    /// <summary>
    /// The lone-ideograph cleanup runs once, ahead of normalisation, and asks no question that
    /// could be answered differently per source language.
    /// </summary>
    /// <remarks>
    /// It used to run twice — inside the automatic normaliser and again beside it for explicit
    /// Latin — which is how the same picture came back with different text under 自動 and under
    /// 日文. Normalising is not where text is decided any more, and these three calls prove it: all
    /// three leave the string exactly as handed to them.
    /// </remarks>
    [Fact]
    public void TheLoneIdeographRule_RunsBeforeNormalisation_NotInsideIt()
    {
        var bounds = new System.Windows.Rect(0, 0, 240, 30);
        List<OcrTextBlock> Blocks() => [new("甲Glossaries", bounds)];

        Assert.Equal("甲Glossaries", OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: false)[0].Text);
        Assert.Equal("甲Glossaries", OnnxOcrEngine.NormalizeBlocks(Blocks(), useCjkRenderMetrics: true)[0].Text);
        Assert.Equal("甲Glossaries", OnnxOcrEngine.NormalizeAutomaticBlocks(Blocks())[0].Text);

        // And the stage that does decide it takes no language, so it cannot answer differently.
        Assert.Equal("Glossaries", OnnxOcrEngine.StripIconIdeographs(Blocks())[0].Text);
    }
}
