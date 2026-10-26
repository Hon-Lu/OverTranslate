using System.Windows;
using System.Windows.Media;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The capture overlay's bubbles while the translations come in one block at a time: settled before
/// the text, filled in as it arrives, and the finished answer drawn in the same boxes and colours.
/// </summary>
public class StreamedCaptureBubblesTests
{
    private static readonly OcrTextBlock Line = new("Hello there", new Rect(10, 10, 200, 28));

    private static readonly OcrTextBlock Paragraph = new(
        "you would actually like this one",
        new Rect(10, 50, 220, 60),
        [new Rect(10, 50, 220, 28), new Rect(10, 82, 160, 28)]);

    private static readonly Color Paper = Color.FromRgb(0xF0, 0xE0, 0xD0);
    private static readonly Color Ink = Color.FromRgb(0x10, 0x20, 0x30);

    private static StreamedCaptureBubbles Coloured(CaptureLayoutMode mode)
    {
        var bubbles = new StreamedCaptureBubbles([Line, Paragraph], mode, verticalText: false, "ZH-HANT");
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
        var bubbles = new StreamedCaptureBubbles([Line, Paragraph], mode, verticalText: false, "ZH-HANT");

        var answered = OverlayPlacement.Place(
            [Answer(Line, "你好"), Answer(Paragraph, "你其實會喜歡這一個的")], mode, verticalText: false, "ZH-HANT");

        Assert.Equal(answered.Select(block => block.Bounds), bubbles.Shapes.Select(block => block.Bounds));
        Assert.Equal(answered.Select(block => block.LayoutIntent), bubbles.Shapes.Select(block => block.LayoutIntent));
    }

    [Fact]
    public void NothingHasArrived_EveryPieceIsPendingAndStillInTheList()
    {
        var bubbles = Coloured(CaptureLayoutMode.Interface);

        var (blocks, pending, arriving) = bubbles.Current([]);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(3, pending.Count);
        Assert.Empty(arriving);
    }

    [Fact]
    public void AnArrivalIsPlacedInItsColoursAndFadesInOnce()
    {
        var bubbles = Coloured(CaptureLayoutMode.Interface);

        bubbles.Arrive(1, "你其實會喜歡這一個的");
        var (changed, firstShown) = bubbles.TakeArrivals();
        var (blocks, pending, arriving) = bubbles.Current(firstShown);

        Assert.True(changed);
        Assert.Equal([1], firstShown);
        Assert.Single(pending);
        Assert.Contains(blocks[0], pending);

        // The paragraph, back on its two lines, holding the translation between them.
        Assert.Equal(2, arriving.Count);
        Assert.Equal("你其實會喜歡這一個的", string.Concat(blocks.Skip(1).Select(block => block.TranslatedText)));
        Assert.All(blocks.Skip(1), block =>
        {
            Assert.Equal(Paper, block.BackgroundColor);
            Assert.Equal(Ink, block.TextColor);
        });
    }

    [Fact]
    public void TheSameTextAgainChangesNothing_ADifferentOneIsRedrawnWithoutFading()
    {
        var bubbles = Coloured(CaptureLayoutMode.General);
        bubbles.Arrive(0, "你好");
        bubbles.TakeArrivals();

        bubbles.Arrive(0, "你好");
        Assert.False(bubbles.TakeArrivals().Changed);

        bubbles.Arrive(0, "哈囉");
        var (changed, firstShown) = bubbles.TakeArrivals();
        Assert.True(changed);
        Assert.Empty(firstShown);
        Assert.Equal("哈囉", bubbles.Current(firstShown).Blocks[0].TranslatedText);
    }

    [Fact]
    public void OnlyTheLastOfSeveralWaitingArrivalsCounts()
    {
        var bubbles = Coloured(CaptureLayoutMode.General);

        bubbles.Arrive(0, "你");
        bubbles.Arrive(0, "你好");
        bubbles.TakeArrivals();

        Assert.Equal("你好", bubbles.Current([]).Blocks[0].TranslatedText);
    }

    [Fact]
    public void AnArrivalOutOfRangeOrBlankIsIgnored()
    {
        var bubbles = Coloured(CaptureLayoutMode.General);

        bubbles.Arrive(5, "x");
        bubbles.Arrive(0, "  ");

        Assert.False(bubbles.TakeArrivals().Changed);
    }

    /// <summary>
    /// The answer is drawn, not what streamed — a block that failed is its source text — and only
    /// what never streamed fades in at the end.
    /// </summary>
    [Fact]
    public void TheAnswerIsDrawnInTheSampledColours_AndOnlyWhatNeverStreamedFadesIn()
    {
        var bubbles = Coloured(CaptureLayoutMode.General);
        bubbles.Arrive(0, "你好");
        bubbles.TakeArrivals();

        var final = bubbles.Final(
            [Answer(Line, "哈囉"), Answer(Paragraph, Paragraph.Text) with { Untranslated = true }]);

        var (blocks, arriving) = Assert.NotNull(final);
        Assert.Equal("哈囉", blocks[0].TranslatedText);
        Assert.True(blocks[1].Untranslated);
        Assert.All(blocks, block => Assert.Equal(Paper, block.BackgroundColor));
        Assert.DoesNotContain(blocks[0], arriving);
        Assert.Contains(blocks[1], arriving);
    }

    /// <summary>
    /// An engine that answers all at once streamed nothing, and its bubbles appear as they did
    /// before anything streamed: all at once, none fading in.
    /// </summary>
    [Fact]
    public void WhenNothingStreamed_NothingFadesIn()
    {
        var bubbles = Coloured(CaptureLayoutMode.General);

        var final = bubbles.Final([Answer(Line, "你好"), Answer(Paragraph, "你其實會喜歡這一個的")]);

        var (blocks, arriving) = Assert.NotNull(final);
        Assert.False(bubbles.Streamed);
        Assert.NotEmpty(blocks);
        Assert.Empty(arriving);
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
        var bubbles = new StreamedCaptureBubbles([Line, Paragraph], CaptureLayoutMode.Interface, false, "ZH-HANT");

        Assert.Throws<ArgumentException>(() => bubbles.Colour([.. bubbles.Shapes.Take(2)]));
    }
}
