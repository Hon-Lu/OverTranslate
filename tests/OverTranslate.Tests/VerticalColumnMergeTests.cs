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
    /// The first column of a balloon starts lower than the rest and is short: 改めて runs
    /// alongside 助けてくださり for 0.42 of itself, which the old 0.5 floor refused.
    /// </summary>
    [Fact]
    public void A_short_first_column_set_low_in_the_balloon_joins_the_rest()
    {
        // 改めて助けてくださりありがとうございました！ on 2026-09-20 19 15 01.png.
        var groups = VerticalColumnGrouping.MergeColumns(
        [
            Column("改めて", new Rect(1093, 128, 38, 103)),
            Column("助けてくださり", new Rect(1054, 187, 32, 179)),
            Column("ありがとうございました！", new Rect(1031, 192, 31, 278)),
        ]);

        Assert.Equal(["改めて助けてくださりありがとうございました！"], groups.Select(g => g.Text));
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
}
