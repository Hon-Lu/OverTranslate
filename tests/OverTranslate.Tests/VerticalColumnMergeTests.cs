using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// Which columns are one balloon, on real rectangles off the 15 comic pages.
/// </summary>
/// <remarks>
/// A balloon is an oval, so its columns are ragged at both ends: the ones at the edges are shorter
/// and sit further down. How much of the shorter one has to run alongside its neighbour is the
/// thing measured here, and the figures below are the two sides of where that bar now sits.
/// </remarks>
public class VerticalColumnMergeTests
{
    private static OcrTextBlock Column(string text, Rect bounds) =>
        new(text, bounds)
        {
            LayoutBounds = bounds,
            LayoutGlyphHeight = VerticalOcrGeometry.GlyphPitch(new OcrTextBlock(text, bounds)
            {
                LayoutBounds = bounds,
            }),
        };

    /// <summary>
    /// A short column in a lobe of its own, above and to the right of the rest: 改めて runs
    /// alongside 助けてくださり for 0.42 of itself, enough for the pair to join, and the two runs
    /// either side of it share 0.42 of the shorter one's height, so the reading-block cut parts
    /// them again — the way the user reads ただ、 beside それだけのことだ。
    /// </summary>
    [Fact]
    public void A_short_column_stepped_up_in_its_own_lobe_is_its_own_block()
    {
        // 改めて助けてくださりありがとうございました！ on 2026-09-20 19 15 01.png.
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("改めて", new Rect(1093, 128, 38, 103)),
            Column("助けてくださり", new Rect(1054, 187, 32, 179)),
            Column("ありがとうございました！", new Rect(1031, 192, 31, 278)),
        ]);

        Assert.Equal(["改めて", "助けてくださりありがとうございました！"], groups.Select(g => g.Text));
    }

    /// <summary>
    /// A balloon stacked above another still shares no length with it, which is what this bar was
    /// always for.
    /// </summary>
    [Fact]
    public void A_balloon_above_another_is_not_joined_to_it()
    {
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("それで", new Rect(111, 1033, 37, 90)),
            // Directly below the first, in the same column of the page: no shared length at all.
            Column("オルンさん！", new Rect(108, 1140, 37, 161)),
        ]);

        Assert.Equal(2, groups.Count);
    }

    /// <summary>Side by side and level: the ordinary case, and it stays joined.</summary>
    [Fact]
    public void Columns_running_the_same_length_are_one_balloon()
    {
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("それで", new Rect(111, 1033, 37, 90)),
            Column("オルンさん！", new Rect(79, 1033, 37, 161)),
        ]);

        Assert.Single(groups);
    }

    /// <summary>
    /// Big lettering beside a balloon is its own group: its type must not stretch the distance bar
    /// over to the balloon's small columns.
    /// </summary>
    [Fact]
    public void Big_lettering_beside_a_balloon_is_not_joined_to_it()
    {
        // ja3 432/002: 火鉢の炭 lettered 134px wide, ご心配いただき in the balloon beside it, 26px type.
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("火鉢の炭", new Rect(656, 93, 134, 349)),
            Column("ご心配いただき", new Rect(593, 180, 48, 180)),
        ]);

        Assert.Equal(["火鉢の炭", "ご心配いただき"], groups.Select(g => g.Text));
    }

    /// <summary>
    /// A short balloon set corner to corner with another shares too little of its length to join.
    /// </summary>
    [Fact]
    public void A_balloon_set_corner_to_corner_with_another_is_not_joined_to_it()
    {
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("はあ", new Rect(200, 100, 36, 80)),
            // Left and below: alongside the first for a quarter of its length.
            Column("何を言うの", new Rect(160, 160, 36, 180)),
        ]);

        Assert.Equal(2, groups.Count);
    }

    /// <summary>
    /// Three columns of one balloon, each set lower than the last, are still one balloon.
    /// </summary>
    [Fact]
    public void Columns_stepping_down_in_one_balloon_are_one_group()
    {
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("俺の生まれた", new Rect(905, 963, 34, 200)),
            Column("南側諸国は", new Rect(871, 973, 34, 167)),
            Column("魔族の勢力圏である", new Rect(837, 981, 34, 189)),
        ]);

        Assert.Equal(["俺の生まれた南側諸国は魔族の勢力圏である"], groups.Select(g => g.Text));
    }

    /// <summary>
    /// Two remarks in one balloon, the second stepped down past the foot of the first: the column
    /// pairs join, the two runs hardly overlap, and they are cut apart.
    /// </summary>
    [Fact]
    public void A_remark_stepped_down_past_the_one_before_it_is_its_own_block()
    {
        // zang 19 08 57 (3): 最後だからこそだ。 and, to its left and lower, 俺はロベルーア領総督の“レーヴェ”で、
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("最後", new Rect(771, 728, 36, 65)),
            Column("だから", new Rect(730, 721, 42, 102)),
            Column("こそだ。", new Rect(696, 723, 41, 112)),
            Column("俺は", new Rect(643, 784, 48, 65)),
            Column("ロベルーア領", new Rect(610, 780, 31, 189)),
            Column("総督の", new Rect(559, 775, 51, 111)),
            Column("レーヴェで", new Rect(529, 782, 40, 187)),
        ]);

        Assert.Equal(["最後だからこそだ。", "俺はロベルーア領総督のレーヴェで"], groups.Select(g => g.Text));
    }

    /// <summary>
    /// Three remarks stepping down one after the other are three blocks, each cut where its run
    /// leaves the one before.
    /// </summary>
    [Fact]
    public void Three_remarks_stepping_down_are_three_blocks()
    {
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("人類も", new Rect(700, 500, 36, 120)),
            Column("魔族も", new Rect(660, 500, 36, 120)),
            Column("魔力を", new Rect(620, 560, 36, 120)),
            Column("失い、", new Rect(580, 560, 36, 120)),
            Column("この世", new Rect(540, 620, 36, 120)),
            Column("界から", new Rect(500, 620, 36, 120)),
        ]);

        Assert.Equal(["人類も魔族も", "魔力を失い、", "この世界から"], groups.Select(g => g.Text));
    }

    /// <summary>
    /// The first column of a remark set well above the second — the page's ragged head, not a
    /// second block. They share 0.72 of the shorter one's height.
    /// </summary>
    [Fact]
    public void A_first_column_set_higher_than_the_next_stays_in_its_block()
    {
        // vertical-manga-web/bt-original2.jpg.
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("ちょっと悪いんだけど", new Rect(795, 309, 100, 651)),
            Column("そこの魔法陣に入って頂戴", new Rect(726, 490, 96, 779)),
        ]);

        Assert.Single(groups);
    }

    /// <summary>
    /// A word whose column came back as two pieces, one over the other, is not cut in two: the
    /// pieces' centres are apart only because of the reading beside them.
    /// </summary>
    [Fact]
    public void A_column_read_in_two_pieces_is_not_cut_between_them()
    {
        // ja3 432/002: 霊麻？, read as 重 over 麻？.
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("重", new Rect(206, 1321, 76, 68)),
            Column("麻？", new Rect(205, 1359, 65, 107)),
        ]);

        Assert.Equal(["重麻？"], groups.Select(g => g.Text));
    }

    /// <summary>
    /// The columns of an oval balloon, ragged at both ends, are one block.
    /// </summary>
    [Fact]
    public void The_ragged_columns_of_an_oval_balloon_are_one_block()
    {
        // これまで苦楽を共にしてきた仲間に対する態度か？ on 2026-09-20 19 14 56 (3).png.
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("これまで苦楽を", new Rect(465, 299, 50, 198)),
            Column("共にしてきた", new Rect(412, 293, 70, 183)),
            Column("仲間に対する態度か？", new Rect(386, 292, 61, 278)),
        ]);

        Assert.Single(groups);
    }

    /// <summary>
    /// One remark of several columns, the last of them longer than the rest, is one block.
    /// </summary>
    [Fact]
    public void Several_columns_of_one_remark_are_one_block()
    {
        // そして他のSランクパーティの付与術士に比べて on 2026-09-20 19 14 56.png.
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("そして", new Rect(749, 811, 44, 88)),
            Column("他のSランクパーティの", new Rect(713, 809, 42, 286)),
            Column("付与術士に比べて", new Rect(679, 811, 36, 209)),
        ]);

        Assert.Single(groups);
    }
}
