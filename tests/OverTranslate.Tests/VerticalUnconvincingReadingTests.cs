using OverTranslate.Services;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// The live path's "short and unsure, so it is scenery" test, asked of the sentence rather than of
/// the columns it is built from.
/// </summary>
/// <remarks>
/// The figures come from a frame captured out of a live session: a balloon set in white on a black
/// panel, where the recogniser scores everything lower.
/// </remarks>
public class VerticalUnconvincingReadingTests
{
    private static OcrTextBlock Column(string text, Rect bounds, double confidence) =>
        new(text, bounds, Confidence: confidence) { LayoutBounds = bounds };

    /// <summary>
    /// A column is a handful of characters by construction, so the length half of the test is true
    /// of nearly every one — asked of columns, the test is just "drop anything under 0.80".
    /// </summary>
    [Fact]
    public void A_balloon_whose_columns_all_score_low_survives_as_a_sentence()
    {
        var blocks = new List<OcrTextBlock>
        {
            Column("とはいえ", new Rect(268, 330, 55, 124), 0.74),
            Column("そいつらにとって", new Rect(243, 335, 45, 209), 0.68),
        };

        var groups = OcrService.GroupVertical(blocks, frameWidth: 1824, realtime: true);

        Assert.Equal(["とはいえそいつらにとって"], groups.Select(group => group.Text));
    }

    /// <summary>
    /// What the test exists for still goes: a couple of characters off the scenery has nothing
    /// beside it to group with, so it is still a couple of characters when it is asked.
    /// </summary>
    [Fact]
    public void A_short_unsure_reading_standing_on_its_own_is_still_dropped()
    {
        var blocks = new List<OcrTextBlock>
        {
            Column("DM", new Rect(600, 400, 40, 60), 0.62),
        };

        Assert.Empty(OcrService.GroupVertical(blocks, frameWidth: 1824, realtime: true));
    }

    /// <summary>The screenshot flow never ran this test and still does not.</summary>
    [Fact]
    public void The_screenshot_flow_keeps_a_short_unsure_reading()
    {
        var blocks = new List<OcrTextBlock>
        {
            Column("DM", new Rect(600, 400, 40, 60), 0.62),
        };

        Assert.Single(OcrService.GroupVertical(blocks, frameWidth: 1824, realtime: false));
    }
}
