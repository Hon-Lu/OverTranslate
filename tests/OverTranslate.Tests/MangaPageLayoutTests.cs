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
        // Two bubbles that touch, side by side; each holds one block. The right-hand one is read first.
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
    public void ABlockBelowAnother_InOneBalloon_IsNeverJoinedToIt()
    {
        // The column beside the upper block runs alongside it and is joined; the block under it is not,
        // though all three are in one balloon.
        var lower = Block(200, 220, 40, 80, "下");
        var upper = Block(202, 100, 38, 100, "上");
        var leftColumn = Block(120, 100, 40, 150, "左");
        RectangleF[] bubbles = [new(110, 90, 140, 220)];

        var result = MangaPageLayout.Assemble([lower, leftColumn, upper], bubbles, []);

        Assert.Equal(["下", "上左"], result.Select(block => block.Text));
    }

    [Fact]
    public void TwoBlocksSteppedDownInOneBalloon_AreTwo()
    {
        // ja3 41/007: えっと…「撃竜砲」の援護のために戦ってもらう, and under it, moved down past its foot,
        // そういうことであれば大丈夫かと… — one outline, two blocks.
        var first = Block(739, 59.2f, 127, 148.8f, "えっと…「撃竜砲」の援護のために戦ってもらう");
        var second = Block(746, 236.5f, 115.5f, 166, "そういうことであれば大丈夫かと…");
        RectangleF balloon = new(711.5f, 17.5f, 172.5f, 415);

        var result = MangaPageLayout.Assemble([first, second], [balloon], [], Page(960, 1365, (Whole(balloon), 255)));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ThreeBlocksSteppingDownOneShape_AreThree()
    {
        // zang 19 08 58: 人類も魔族も平等に、／魔力を失い、／この世界から魔法が無くなる。 stepping down and
        // to the left through three lobes of one white shape.
        RectangleF[] lobes = [new(595, 506.5f, 199, 143), new(623.5f, 642.5f, 140.5f, 111.5f), new(520.5f, 694, 228, 246)];
        MangaBlock[] blocks =
        [
            Block(622.5f, 518, 149.5f, 116, "人類も魔族も平等に、"),
            Block(645.5f, 648, 98, 99, "魔力を失い、"),
            Block(543, 737.5f, 176, 167.5f, "この世界から魔法が無くなる。"),
        ];
        var page = Page(1126, 1280, [.. lobes.Select(lobe => (Whole(lobe), (byte)255))]);

        Assert.Equal(3, MangaPageLayout.Assemble(blocks, lobes, [], page).Count);
    }

    [Fact]
    public void AFirstColumnSetALittleHigher_IsPartOfTheBlock()
    {
        // ja2 19 14 55 (2): 俺たち幼なじみで starts 43px above ずっと一緒にやってきた仲間だろ and the detector
        // boxes it, and its balloon, on its own. They share 0.91 of the shorter one's height.
        RectangleF[] lobes = [new(1165, 555, 97, 248.5f), new(1018, 580, 160, 260)];
        var first = Block(1180, 582, 55, 207.5f, "俺たち幼なじみで");
        var rest = Block(1040, 625, 125, 181, "ずっと一緒にやってきた仲間だろ");
        var page = Page(1822, 1298, [.. lobes.Select(lobe => (Whole(lobe), (byte)255))]);

        var joined = Assert.Single(MangaPageLayout.Assemble([rest, first], lobes, [], page));

        Assert.Equal("俺たち幼なじみでずっと一緒にやってきた仲間だろ", joined.Text);
    }

    [Fact]
    public void OneRemarkInTwoBoxes_ItsLeftHalfALittleLower_IsOneBlock()
    {
        // ja2 19 14 56: そして他のSランクパーティの付与術士に比べて／お前の支援魔術がひどく劣っていることも,
        // the left half 37px lower; 0.88 shared.
        RectangleF[] lobes = [new(666, 744, 146, 400), new(516, 807, 177.5f, 397)];
        var right = Block(676, 794, 115, 314, "そして他のSランクパーティの付与術士に比べて");
        var left = Block(546.5f, 831, 122.5f, 348, "お前の支援魔術がひどく劣っていることも");
        var page = Page(1825, 1301, [.. lobes.Select(lobe => (Whole(lobe), (byte)255))]);

        Assert.Single(MangaPageLayout.Assemble([left, right], lobes, [], page));
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

    // A page of dark artwork with the given areas painted as paper (255) or ink (0).
    private static LumaPage Page(int width, int height, params (Rectangle Area, byte Value)[] paint)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)40);
        foreach (var (area, value) in paint)
            for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                pixels[y * width + x] = value;
        return new LumaPage(pixels, width, height);
    }

    private static Rectangle Whole(RectangleF r) =>
        Rectangle.FromLTRB((int)r.Left, (int)r.Top, (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));

    [Fact]
    public void TwoLobesSetCornerToCorner_StayApart_EvenAsOneShape()
    {
        // ch50/008: 後宮内のどこか… right above, 泥水が冷たくて… left below, two lobes of one white
        // shape with no outline between them. Their boxes overlap; the writing sits corner to corner.
        RectangleF upper = new(333, 746, 168, 281), lower = new(272, 979, 171, 266);
        var a = Block(364, 774, 121, 234, "後宮内のどこか古井戸に放り込まれてしまいましたの");
        var b = Block(303, 1013, 109, 197, "泥水が冷たくて少し意識が");
        var page = Page(600, 1300, (Whole(upper), 255), (Whole(lower), 255));

        var result = MangaPageLayout.Assemble([a, b], [upper, lower], [], page);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void TwoBalloonsSideBySide_AsOneShape_AreOneGroup()
    {
        RectangleF right = new(200, 80, 100, 160), left = new(120, 90, 100, 150);
        var a = Block(220, 100, 60, 120, "よいか冬雪");
        var b = Block(140, 110, 60, 110, "取り乱さず");
        var page = Page(400, 300, (Whole(right), 255), (Whole(left), 255));

        var joined = Assert.Single(MangaPageLayout.Assemble([a, b], [right, left], [], page));

        Assert.Equal("よいか冬雪取り乱さず", joined.Text);
    }

    [Fact]
    public void ABalloonLaidOverAnother_KeepsItsOwnOutline_AndStaysApart()
    {
        // The same two balloons, but the left one is drawn over the right with its outline across it:
        // a speaker cutting in.
        RectangleF right = new(200, 80, 100, 160), left = new(120, 90, 100, 150);
        var a = Block(220, 100, 60, 120, "開発して");
        var b = Block(140, 110, 60, 110, "うるせぇ");
        var page = Page(400, 300, (Whole(right), 255), (Whole(left), 255), (new Rectangle(210, 80, 3, 160), 0));

        var result = MangaPageLayout.Assemble([a, b], [right, left], [], page);

        Assert.Equal(["開発して", "うるせぇ"], result.Select(block => block.Text));
    }

    [Fact]
    public void BalloonsOneAboveTheOther_AsOneShape_StayApart()
    {
        // zang 19 08 58: いや、これから起こるのは、 and, right under it, 偉業ですらないな。 — one white shape.
        // Joined, the columns would be read right to left across both and the two remarks shuffled.
        RectangleF upper = new(416.2f, 0.2f, 179.2f, 192.5f), lower = new(456.5f, 186.8f, 138.5f, 202.8f);
        var a = Block(441, 7.1f, 133, 172.9f, "いや、これから起こるのは、");
        var b = Block(484.5f, 201.5f, 95.5f, 165.8f, "偉業ですらないな。");
        var page = Page(1126, 1280, (Whole(upper), 255), (Whole(lower), 255));

        Assert.Equal(2, MangaPageLayout.Assemble([a, b], [upper, lower], [], page).Count);
    }

    [Fact]
    public void BalloonsAcrossAPanelBorder_StayApart()
    {
        // Two balloons side by side that touch across the border of two panels; the border runs
        // between the two blocks.
        RectangleF right = new(200, 80, 100, 160), left = new(120, 90, 100, 150);
        var a = Block(220, 100, 60, 120, "最後になる");
        var b = Block(140, 110, 60, 110, "俺達の");
        var page = Page(400, 300, (Whole(right), 255), (Whole(left), 255), (new Rectangle(208, 60, 4, 200), 0));

        Assert.Equal(2, MangaPageLayout.Assemble([a, b], [right, left], [], page).Count);
    }

    [Fact]
    public void AStaircaseCaption_IsOneGroup()
    {
        // zang 19 08 58 (2): 俺の生まれた南側諸国は、 and the columns stepping down to its left.
        var first = Block(868, 965.5f, 103.5f, 206.5f, "俺の生まれた南側諸国は、");
        var rest = Block(624.5f, 964.5f, 234.5f, 230.5f, "魔族の勢力圏である大陸北部から遠く離れている代わりに、");

        var joined = Assert.Single(MangaPageLayout.Assemble([rest, first], [], [], Page(1100, 1300)));

        Assert.StartsWith("俺の生まれた", joined.Text);
    }

    [Fact]
    public void CaptionsOneAboveTheOther_StayApart()
    {
        // zang 19 08 58 (2): 一度目の“魔法”は、 over 物心が付いたばかりの頃だ。, 39px apart — one sentence,
        // which the user reads as two blocks.
        var upper = Block(840, 432, 120, 153, "一度目の魔法は");
        var lower = Block(838, 624, 118, 211, "物心が付いたばかりの頃だ");

        Assert.Equal(2, MangaPageLayout.Assemble([upper, lower], [], [], Page(1100, 1300)).Count);
    }

    [Fact]
    public void CaptionsSteppedDownAndToTheLeft_StayApart()
    {
        // zang 19 08 58 (2), the user's example: 空から降り注いだ光の矢の内のたった一本が、 and 兄の命をあっさり奪った。
        // stepped down to its left. They share 0.59 of the shorter one's height.
        var first = Block(235.8f, 645, 193.8f, 204, "空から降り注いだ光の矢の内のたった一本が、");
        var second = Block(87.6f, 759, 149.9f, 153, "兄の命をあっさり奪った。");

        Assert.Equal(2, MangaPageLayout.Assemble([first, second], [], [], Page(1127, 1273)).Count);
    }

    [Fact]
    public void CaptionsWithAPanelBorderBetween_StayApart()
    {
        var first = Block(868, 965.5f, 103.5f, 206.5f, "俺の生まれた南側諸国は、");
        var rest = Block(624.5f, 964.5f, 234.5f, 230.5f, "魔族の勢力圏である大陸北部から遠く離れている代わりに、");
        // On paper the border is ink; on black it would be a white rule.
        var page = Page(1100, 1300, (new Rectangle(600, 940, 400, 280), 255), (new Rectangle(861, 940, 4, 280), 0));

        Assert.Equal(2, MangaPageLayout.Assemble([first, rest], [], [], page).Count);
    }

    [Fact]
    public void CaptionsSideBySideWithAClearSpace_StayApart()
    {
        // ja3 432/007: わかさぎ釣りなんてどうでしょう and あっ柚子の木！, two people, two characters apart.
        var a = Block(334, 515, 115, 169, "わかさぎ釣りなんてどうでしょう");
        var b = Block(186, 610, 84, 133, "あっ柚子の木");

        Assert.Equal(2, MangaPageLayout.Assemble([a, b], [], [], Page(600, 800)).Count);
    }

    [Fact]
    public void BigLetteringBesideACaption_StaysApart()
    {
        // ja3 432/010: 遠っ lettered big, a sliver of its own characters from the writing beside it.
        var big = Block(500, 100, 130, 150, "遠");
        var small = Block(650, 100, 60, 200, "ならば芳春様や清佳様に");

        Assert.Equal(2, MangaPageLayout.Assemble([big, small], [], [], Page(800, 400)).Count);
    }

    [Fact]
    public void ACaptionIsNeverJoinedToABalloon()
    {
        RectangleF balloon = new(90, 90, 70, 150);
        var inside = Block(100, 100, 40, 120, "中");
        var outside = Block(142, 100, 40, 120, "外");   // touching, but in no balloon

        var result = MangaPageLayout.Assemble([inside, outside], [balloon], [], Page(300, 300, (Whole(balloon), 255)));

        Assert.Equal(2, result.Count);
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
        Assert.Equal(30 / 1.45, MangaPageLayout.GlyphSize(new System.Windows.Rect(0, 0, 90, 150), new string('あ', 15), across: false), 6);
        // One column read as two characters would claim 100; the column is only 40 wide.
        Assert.Equal(40, MangaPageLayout.GlyphSize(new System.Windows.Rect(0, 0, 40, 250), "ああ", across: false), 6);
        // A row is capped by its height instead.
        Assert.Equal(36, MangaPageLayout.GlyphSize(new System.Windows.Rect(0, 0, 200, 36), "オ", across: true), 6);
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
