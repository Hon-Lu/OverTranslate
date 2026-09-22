using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// The live path's "the detector collapsed" test, asked only of what runs down the page.
/// </summary>
/// <remarks>
/// A box spanning the frame's width is the giveaway for a detection that swallowed the columns —
/// and it is also what a title across the top of a page looks like. Which of the two it is comes
/// from the shape of the box, not from its width alone.
/// </remarks>
public class VerticalCollapsedDetectionTests
{
    private static OcrTextBlock Block(string text, Rect bounds) =>
        new(text, bounds) { LayoutBounds = bounds };

    /// <summary>
    /// mokuro-000a.jpg: an 827 wide cover, its title read whole at 1.00 out of a box 840 wide.
    /// </summary>
    /// <remarks>
    /// Seven characters, against a bar of ten that was measured on English subtitles — where ten
    /// characters really is a collapse and in Japanese it is a sentence.
    /// </remarks>
    [Fact]
    public void A_title_across_the_top_of_the_page_is_not_a_collapse()
    {
        var groups = VerticalColumnGrouping.Group(
            [Block("うちの猫ず日記", new Rect(0, 22, 840, 192))], frameWidth: 827, realtime: true);

        Assert.Equal(["うちの猫ず日記"], groups.Select(group => group.Text));
    }

    /// <summary>A box that really did swallow the columns is as tall as they are.</summary>
    [Fact]
    public void A_box_thrown_across_the_columns_is_still_a_collapse()
    {
        var groups = VerticalColumnGrouping.Group(
            [Block("ああ", new Rect(0, 100, 840, 900))], frameWidth: 827, realtime: true);

        Assert.Empty(groups);
    }

    /// <summary>The screenshot flow never asked this, and still does not.</summary>
    [Fact]
    public void The_screenshot_flow_keeps_a_box_that_spans_the_frame()
    {
        var groups = VerticalColumnGrouping.Group(
            [Block("ああ", new Rect(0, 100, 840, 900))], frameWidth: 827);

        Assert.Single(groups);
    }
}
