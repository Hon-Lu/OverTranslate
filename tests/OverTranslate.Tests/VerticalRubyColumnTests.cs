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

        Assert.Equal(["正しい判断を"], VerticalRubyColumns.Drop([writing, reading]).Select(b => b.Text));
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

        Assert.Equal(2, VerticalRubyColumns.Drop([writing, candidate]).Count);
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

        Assert.Equal(2, VerticalRubyColumns.Drop([writing, candidate]).Count);
    }

    /// <summary>A reading spells out a pronunciation, so one that came back holding a kanji is kept.</summary>
    [Fact]
    public void A_candidate_holding_a_kanji_is_kept()
    {
        var writing = Column("の本職は", new Rect(1129, 203, 33, 156));
        // ほんしょく, mis-read with a 礼 in front of it.
        var candidate = Column("礼ほんしょく", new Rect(1162, 203, 13, 156));

        Assert.Equal(2, VerticalRubyColumns.Drop([writing, candidate]).Count);
    }

    /// <summary>To the RIGHT of the writing, which is the side ruby is set on in vertical text.</summary>
    [Fact]
    public void A_narrow_column_on_the_left_is_kept()
    {
        var writing = Column("正しい判断を", new Rect(1024, 986, 27, 177));
        var onTheLeft = Column("ただはんだん", new Rect(998, 986, 13, 177));

        Assert.Equal(2, VerticalRubyColumns.Drop([writing, onTheLeft]).Count);
    }

    /// <summary>A column standing a column's width away belongs to another balloon.</summary>
    [Fact]
    public void A_narrow_column_standing_away_from_the_writing_is_kept()
    {
        var writing = Column("そのおかげで", new Rect(100, 50, 37, 220));
        var away = Column("まゅおな", new Rect(600, 50, 11, 220));

        Assert.Equal(2, VerticalRubyColumns.Drop([writing, away]).Count);
    }

    /// <summary>Alongside the writing, not merely near it.</summary>
    [Fact]
    public void A_narrow_column_that_runs_past_the_writing_is_kept()
    {
        var writing = Column("剣士…？", new Rect(100, 50, 44, 90));
        var past = Column("けんし", new Rect(144, 50, 16, 209));

        Assert.Equal(2, VerticalRubyColumns.Drop([writing, past]).Count);
    }
}
