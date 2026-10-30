using System.Windows;
using System.Windows.Media;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The capture overlay's bubbles settled before the translation: the same boxes the answer is
/// drawn in, and the answer drawn in the colours sampled for them.
/// </summary>
public class CaptureBubbleShapesTests
{
    private static readonly OcrTextBlock Line = new("Hello there", new Rect(10, 10, 200, 28));

    private static readonly OcrTextBlock Paragraph = new(
        "you would actually like this one",
        new Rect(10, 50, 220, 60),
        [new Rect(10, 50, 220, 28), new Rect(10, 82, 160, 28)]);

    private static readonly Color Paper = Color.FromRgb(0xF0, 0xE0, 0xD0);
    private static readonly Color Ink = Color.FromRgb(0x10, 0x20, 0x30);

    private static CaptureBubbleShapes Coloured(CaptureLayoutMode mode)
    {
        var bubbles = new CaptureBubbleShapes([Line, Paragraph], mode, verticalText: false, "ZH-HANT");
        bubbles.Colour([.. bubbles.Shapes.Select(shape => shape with { BackgroundColor = Paper, TextColor = Ink })]);
        return bubbles;
    }

    private static TranslatedBlock Answer(OcrTextBlock block, string text) =>
        new(block.Text, text, block.Bounds, block.Lines, block.RenderGlyphHeight);

    /// <summary>
    /// The boxes known before any translation are the boxes the finished overlay draws — which is
    /// what makes it right to sample colours and repair the backdrop under them early.
    /// </summary>
    [Theory]
    [InlineData(CaptureLayoutMode.General)]
    [InlineData(CaptureLayoutMode.Interface)]
    public void TheShapesAreTheBoxesTheAnswerIsDrawnIn(CaptureLayoutMode mode)
    {
        var bubbles = new CaptureBubbleShapes([Line, Paragraph], mode, verticalText: false, "ZH-HANT");

        var answered = OverlayPlacement.Place(
            [Answer(Line, "你好"), Answer(Paragraph, "你其實會喜歡這一個的")], mode, verticalText: false, "ZH-HANT");

        Assert.Equal(answered.Select(block => block.Bounds), bubbles.Shapes.Select(block => block.Bounds));
        Assert.Equal(answered.Select(block => block.LayoutIntent), bubbles.Shapes.Select(block => block.LayoutIntent));
    }

    /// <summary>A block that failed comes back as its source text, and is drawn like any other.</summary>
    [Fact]
    public void TheAnswerIsDrawnInTheSampledColours()
    {
        var bubbles = Coloured(CaptureLayoutMode.Interface);

        var blocks = Assert.IsType<List<TranslatedBlock>>(bubbles.Final(
            [Answer(Line, "你好"), Answer(Paragraph, Paragraph.Text) with { Untranslated = true }]));

        // The paragraph, back on its two lines.
        Assert.Equal(3, blocks.Count);
        Assert.Equal("你好", blocks[0].TranslatedText);
        Assert.All(blocks.Skip(1), block => Assert.True(block.Untranslated));
        Assert.All(blocks, block =>
        {
            Assert.Equal(Paper, block.BackgroundColor);
            Assert.Equal(Ink, block.TextColor);
        });
    }

    [Fact]
    public void AnAnswerThatDoesNotLineUpIsRefused()
    {
        var bubbles = Coloured(CaptureLayoutMode.General);

        Assert.Null(bubbles.Final([Answer(Line, "你好")]));
    }

    [Fact]
    public void ColoursMustComeOnePerPiece()
    {
        var bubbles = new CaptureBubbleShapes([Line, Paragraph], CaptureLayoutMode.Interface, false, "ZH-HANT");

        Assert.Throws<ArgumentException>(() => bubbles.Colour([.. bubbles.Shapes.Take(2)]));
    }
}
