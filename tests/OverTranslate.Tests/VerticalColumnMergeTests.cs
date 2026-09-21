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
        var groups = OcrService.MergeVerticalColumns(
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
        var groups = OcrService.MergeVerticalColumns(
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
        var groups = OcrService.MergeVerticalColumns(
        [
            Column("それで", new Rect(111, 1033, 37, 90)),
            Column("オルンさん！", new Rect(79, 1033, 37, 161)),
        ]);

        Assert.Single(groups);
    }
}
