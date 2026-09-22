using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// The reading beside a column, dropped before grouping can merge it into the sentence.
/// </summary>
/// <remarks>
/// The other half of the problem <see cref="VerticalRubyTests"/> covers, and a different stage: that
/// one stops a reading being DRAWN over a balloon, this one stops it being READ as part of the
/// sentence. Every figure below is a real pair off the 15 comic pages in
/// <c>.ai/test-images/vertical-image-ja2</c>.
/// </remarks>
public class VerticalRubyColumnTests
{
    private static OcrTextBlock Column(string text, Rect bounds) =>
        new(text, bounds) { LayoutBounds = bounds };

    [Fact]
    public void A_reading_beside_its_column_is_dropped()
    {
        // ただ / はんだん over 正しい判断を, on 2026-09-20 19 14 57.png.
        var writing = Column("正しい判断を", new Rect(1024, 986, 27, 177));
        var reading = Column("ただはんだん", new Rect(1051, 986, 13, 177));

        Assert.Equal(["正しい判断を"], VerticalRubyColumns.Separate([writing, reading]).Writing.Select(b => b.Text));
    }

    /// <summary>The reading's room goes to the column it annotates, not away with the reading.</summary>
    /// <remarks>
    /// Grouping asks how far apart two columns are, so a column measured without the reading that
    /// was set beside it sits further from its neighbour than the detector ever framed it. Keeping
    /// the room also leaves the bubble drawn over the reading rather than beside it.
    /// </remarks>
    [Fact]
    public void The_room_a_reading_took_up_is_left_to_the_writing()
    {
        var writing = Column("正しい判断を", new Rect(1024, 986, 27, 177));
        var reading = Column("ただはんだん", new Rect(1051, 986, 13, 177));

        var kept = VerticalRubyColumns.Separate([writing, reading]).Writing;

        Assert.Equal(new Rect(1024, 986, 40, 177), Assert.Single(kept).Bounds);
        Assert.Equal(new Rect(1024, 986, 40, 177), kept[0].LayoutBounds);
    }

    /// <summary>
    /// A column of dialogue that happens to be narrow is kept, and the size of its glyphs is what
    /// says so — not the width of the box around them.
    /// </summary>
    /// <remarks>
    /// <c>そして</c> measures 0.72 of the column beside it by box width and 0.72 by glyph size;
    /// <c>そのうえ</c> measures 0.47 by width and 0.99 by glyph size. Width alone loses the second
    /// one, because the detector's expansion adds the same few pixels to a narrow box as to a wide
    /// one and that is a far larger share of the narrow one — and 囮にされて死ぬかもしれない is
    /// framed 78px wide around 25px glyphs, so そのうえ beside it is only half as wide.
    /// </remarks>
    [Theory]
    [InlineData("そして", 18, 305, "他のSランクパーティの", 25, 305)]
    [InlineData("そのうえ", 37, 101, "死ぬかもしれない", 78, 204)]
    [InlineData("それに", 34, 76, "今日初めて", 41, 126)]
    public void A_narrow_column_of_dialogue_is_kept(
        string text, int width, int height, string next, int nextWidth, int nextHeight)
    {
        var writing = Column(next, new Rect(100, 50, nextWidth, nextHeight));
        var candidate = Column(text, new Rect(100 + nextWidth, 50, width, height));

        Assert.Equal(2, VerticalRubyColumns.Separate([writing, candidate]).Writing.Count);
    }

    /// <summary>
    /// Ruby annotates kanji, so a column of kana beside another column of kana is two pieces of
    /// writing. This is the one test that keeps <c>いっん</c> beside <c>やっでる</c>.
    /// </summary>
    [Fact]
    public void A_kana_column_beside_kana_is_kept()
    {
        var writing = Column("やっでる", new Rect(300, 100, 120, 55));
        var candidate = Column("いっん", new Rect(414, 100, 22, 55));

        Assert.Equal(2, VerticalRubyColumns.Separate([writing, candidate]).Writing.Count);
    }

    /// <summary>
    /// A reading spells out a pronunciation, so one that came back holding a kanji is not thrown
    /// away — but it is still kept out of the sentence.
    /// </summary>
    /// <remarks>
    /// The recogniser breaks the kana-only test often enough to be the largest hole in this stage:
    /// 15 of the 22 readings that escape over the 15 comic pages escape on it alone. What stops the
    /// test simply being dropped is that the material behind it is not all ruby — <c>仲間だろ</c> at
    /// 0.49 of the column beside it is dialogue, and <c>いち見んてきせい</c> at 0.51 is a reading —
    /// so the size cannot tell them apart and neither can anything else here. Setting the column
    /// aside is the answer to that: the reading stays out of the sentence, and the piece of dialogue
    /// is drawn on its own instead of vanishing.
    /// </remarks>
    [Fact]
    public void A_candidate_holding_a_kanji_is_set_aside_rather_than_dropped()
    {
        var writing = Column("の本職は", new Rect(1129, 203, 33, 156));
        // ほんしょく, mis-read with a 礼 in front of it.
        var candidate = Column("礼ほんしょく", new Rect(1162, 203, 13, 156));

        var separated = VerticalRubyColumns.Separate([writing, candidate]);

        Assert.Equal(["の本職は"], separated.Writing.Select(b => b.Text));
        Assert.Equal(["礼ほんしょく"], separated.Readings.Select(b => b.Text));
    }

    /// <summary>
    /// Ruby annotates kanji, so a mis-read column beside kana is left in the writing even by the
    /// looser test — which is what spares <c>仲間だろ</c> beside <c>やっだる</c>.
    /// </summary>
    [Fact]
    public void A_candidate_holding_a_kanji_beside_kana_is_left_alone()
    {
        var writing = Column("やっだる", new Rect(300, 100, 120, 186));
        var candidate = Column("仲間だろ", new Rect(420, 100, 37, 91));

        var separated = VerticalRubyColumns.Separate([writing, candidate]);

        Assert.Equal(2, separated.Writing.Count);
        Assert.Empty(separated.Readings);
    }

    /// <summary>
    /// Writing that runs ACROSS the page is never set aside, whichever side of the pair it is on.
    /// </summary>
    /// <remarks>
    /// Most of what escapes the kana-only test is not ruby at all but a caption, a window title or a
    /// taskbar button — <c>各階層の入り口に設置されている</c> at 305x43 beside a balloon. Every one
    /// of them is wider than it is tall, and the looser test asks that of both sides.
    /// </remarks>
    [Fact]
    public void A_row_beside_a_column_is_not_a_reading()
    {
        var writing = Column("迷宮", new Rect(100, 50, 44, 90));
        var caption = Column("各階層の入り口に設置されている", new Rect(144, 50, 305, 43));

        Assert.Empty(VerticalRubyColumns.Separate([writing, caption]).Readings);
    }

    /// <summary>The room a set-aside column took up still goes to the writing, for grouping.</summary>
    /// <remarks>
    /// Only the LayoutBounds, which is what IsSameVerticalTextGroup measures: unlike a dropped
    /// reading, a column set aside is still drawn, and stretching the sentence's own rectangle over
    /// it would lay the balloon's bubble across it.
    /// </remarks>
    [Fact]
    public void A_set_aside_column_leaves_its_room_to_the_writing_for_grouping_only()
    {
        var writing = Column("の本職は", new Rect(1129, 203, 33, 156));
        var candidate = Column("礼ほんしょく", new Rect(1162, 203, 13, 156));

        var kept = Assert.Single(VerticalRubyColumns.Separate([writing, candidate]).Writing);

        Assert.Equal(new Rect(1129, 203, 46, 156), kept.LayoutBounds);
        Assert.Equal(new Rect(1129, 203, 33, 156), kept.Bounds);
    }

    /// <summary>To the RIGHT of the writing, which is the side ruby is set on in vertical text.</summary>
    [Fact]
    public void A_narrow_column_on_the_left_is_kept()
    {
        var writing = Column("正しい判断を", new Rect(1024, 986, 27, 177));
        var onTheLeft = Column("ただはんだん", new Rect(998, 986, 13, 177));

        Assert.Equal(2, VerticalRubyColumns.Separate([writing, onTheLeft]).Writing.Count);
    }

    /// <summary>A column standing a column's width away belongs to another balloon.</summary>
    [Fact]
    public void A_narrow_column_standing_away_from_the_writing_is_kept()
    {
        var writing = Column("そのおかげで", new Rect(100, 50, 37, 220));
        var away = Column("まゅおな", new Rect(600, 50, 11, 220));

        Assert.Equal(2, VerticalRubyColumns.Separate([writing, away]).Writing.Count);
    }

    /// <summary>Alongside the writing, not merely near it.</summary>
    [Fact]
    public void A_narrow_column_that_runs_past_the_writing_is_kept()
    {
        var writing = Column("剣士…？", new Rect(100, 50, 44, 90));
        var past = Column("けんし", new Rect(144, 50, 16, 209));

        Assert.Equal(2, VerticalRubyColumns.Separate([writing, past]).Writing.Count);
    }

    /// <summary>
    /// A column set aside with one character in it is not drawn: there is no sentence to lose.
    /// </summary>
    /// <remarks>
    /// This is what keeps the two flows saying the same thing. The group-level ruby test takes these
    /// on the live path and misses them on the screenshot path — the two read at different detector
    /// sizes, so the boxes land differently against its shared-area bar — and the same page showed a
    /// stray 一 beside the balloon in one flow and not the other. Measured, every single-character
    /// aside on the three corpora is a mis-read reading: 一, L, 上, 大.
    /// </remarks>
    [Fact]
    public void A_single_character_set_aside_is_not_drawn()
    {
        // 一, mis-read out of the reading いちばん beside メンバの中で.
        var writing = Column("メンバの中で", new Rect(100, 50, 30, 150));
        var candidate = Column("一", new Rect(130, 50, 14, 59));

        var groups = OcrService.GroupVertical([writing, candidate], frameWidth: 900);

        Assert.Equal(["メンバの中で"], groups.Select(group => group.Text));
    }

    /// <summary>And two characters are, because two characters can be a line.</summary>
    [Fact]
    public void A_column_set_aside_that_says_more_than_one_character_is_drawn()
    {
        var writing = Column("メンバの中で", new Rect(100, 50, 30, 150));
        var candidate = Column("礼ほ", new Rect(130, 50, 14, 59));

        var groups = OcrService.GroupVertical([writing, candidate], frameWidth: 900);

        Assert.Equal(2, groups.Count);
    }
}
