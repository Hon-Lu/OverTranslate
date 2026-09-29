using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

public class MangaPageLayoutTests
{
    private static MangaBlock Block(float x, float y, float w, float h, string text, double confidence = 0.9) =>
        new(new RectangleF(x, y, w, h), text, confidence);

    [Theory]
    [InlineData(79, 738, true)]     // the recap box that came back as an invented sentence (9.3)
    [InlineData(69, 1098, true)]    // a single 1098px column (15.9)
    [InlineData(100, 590, false)]   // the longest balloon manga-ocr read correctly (5.9)
    [InlineData(700, 90, true)]     // a page-top title, wide
    [InlineData(100, 699, false)]
    [InlineData(100, 700, true)]
    public void LongBlocks_AreTheOnesSevenTimesLongerThanWide(float w, float h, bool expected) =>
        Assert.Equal(expected, MangaPageLayout.IsLong(new RectangleF(0, 0, w, h)));

    [Fact]
    public void LongBlockCrop_AddsSixPixelsInsideThePage()
    {
        Assert.Equal(Rectangle.FromLTRB(94, 14, 185, 820),
            MangaPageLayout.LongBlockCrop(new RectangleF(100.7f, 20.2f, 79.5f, 794f), 1000, 1000));
        Assert.Equal(Rectangle.FromLTRB(0, 0, 88, 500),
            MangaPageLayout.LongBlockCrop(new RectangleF(2, 3, 80, 700), 90, 500));
    }

    [Fact]
    public void RecognitionCrop_RoundsHalfToEvenLikePillow()
    {
        Assert.Equal(Rectangle.FromLTRB(10, 12, 42, 60),
            MangaPageLayout.RecognitionCrop(RectangleF.FromLTRB(10.5f, 11.5f, 41.5f, 60.4f), 100, 100));
    }

    [Fact]
    public void BlocksOfOverlappingBubbles_AreOneSentence_RightToLeft()
    {
        // Two bubbles that touch; each holds one block. The right-hand one is read first.
        var left = Block(100, 100, 40, 120, "取り乱さず");
        var right = Block(200, 90, 40, 100, "よいか冬雪");
        RectangleF[] bubbles = [new(90, 90, 70, 150), new(150, 80, 100, 130)];

        var result = MangaPageLayout.Assemble([left, right], bubbles, []);

        var joined = Assert.Single(result);
        Assert.Equal("よいか冬雪取り乱さず", joined.Text);
        Assert.Equal(new System.Windows.Rect(100, 90, 140, 130), joined.Bounds);
        Assert.Equal(2, joined.Lines.Count);
        Assert.False(joined.RunsAcross);
    }

    [Fact]
    public void BubblesThatDoNotTouch_StayApart()
    {
        var a = Block(100, 100, 40, 120, "一つ目");
        var b = Block(300, 100, 40, 120, "二つ目");
        RectangleF[] bubbles = [new(90, 90, 60, 140), new(290, 90, 60, 140)];

        var result = MangaPageLayout.Assemble([a, b], bubbles, []);

        Assert.Equal(["一つ目", "二つ目"], result.Select(block => block.Text));
    }

    [Fact]
    public void ANamePlate_IsARow_AndIsNeverJoinedIntoTheBubbleUnderIt()
    {
        var speech = Block(200, 100, 60, 200, "話している");
        var plate = Block(150, 280, 200, 36, "オリヴァー・カーディフ");   // wider than 2.5x its height
        RectangleF[] bubbles = [new(140, 90, 220, 240)];

        var result = MangaPageLayout.Assemble([speech, plate], bubbles, []);

        Assert.Equal(2, result.Count);
        Assert.False(result[0].RunsAcross);
        Assert.True(result[1].RunsAcross);
        Assert.Equal("オリヴァー・カーディフ", result[1].Text);
    }

    [Fact]
    public void BlocksStackedInOneColumnBand_ReadTopToBottom()
    {
        var lower = Block(200, 220, 40, 80, "下");
        var upper = Block(202, 100, 38, 100, "上");
        var leftColumn = Block(120, 100, 40, 150, "左");
        RectangleF[] bubbles = [new(110, 90, 140, 220)];

        var result = MangaPageLayout.Assemble([lower, leftColumn, upper], bubbles, []);

        Assert.Equal("上下左", Assert.Single(result).Text);
    }

    [Fact]
    public void ABlockMostlyOutsideEveryBubble_IsLeftAlone()
    {
        var inside = Block(100, 100, 40, 100, "中");
        var outside = Block(150, 100, 40, 100, "外");   // only a quarter of it inside the bubble
        RectangleF[] bubbles = [new(90, 90, 70, 120)];

        var result = MangaPageLayout.Assemble([inside, outside], bubbles, []);

        Assert.Equal(["中", "外"], result.Select(block => block.Text));
    }

    [Fact]
    public void WhatTheColumnPipelineRead_ComesBackAsItWas()
    {
        var passed = new OcrTextBlock("前号まで", new System.Windows.Rect(10, 10, 30, 300));

        var result = MangaPageLayout.Assemble([Block(100, 100, 40, 100, "中")], [], [passed]);

        Assert.Same(passed, result[^1]);
    }

    [Fact]
    public void GlyphSize_IsTheAreaPerCharacter_CappedByTheColumnWidth()
    {
        // Three columns of five: √(90·150/15) = 30.
        Assert.Equal(30, MangaPageLayout.GlyphSize(new System.Windows.Rect(0, 0, 90, 150), new string('あ', 15), across: false), 6);
        // One column read as two characters would claim 100; the column is only 40 wide.
        Assert.Equal(40, MangaPageLayout.GlyphSize(new System.Windows.Rect(0, 0, 40, 250), "ああ", across: false), 6);
        // A row is capped by its height instead.
        Assert.Equal(36, MangaPageLayout.GlyphSize(new System.Windows.Rect(0, 0, 200, 36), "オル", across: true), 6);
    }

    [Fact]
    public void AJoinedGroup_WeighsConfidenceByLength()
    {
        var a = Block(200, 100, 40, 100, "ああああ", 1.0);
        var b = Block(150, 100, 40, 100, "い", 0.5);
        RectangleF[] bubbles = [new(140, 90, 60, 120), new(190, 90, 60, 120)];

        var joined = Assert.Single(MangaPageLayout.Assemble([a, b], bubbles, []));

        Assert.Equal(0.9, joined.Confidence!.Value, 6);
    }
}
