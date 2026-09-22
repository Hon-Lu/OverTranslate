using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// Two readings of the same page at different detector sizes, reconciled balloon by balloon.
/// </summary>
/// <remarks>
/// The figures are off the frames the app captured while the user read a comic, under
/// <c>%AppData%\OverTranslate\logs\frames</c>.
/// </remarks>
public class VerticalSecondLookTests
{
    private static OcrTextBlock Block(string text, Rect bounds) =>
        new(text, bounds) { LayoutBounds = bounds };

    /// <summary>A balloon only the second reading found is taken.</summary>
    [Fact]
    public void A_balloon_the_first_reading_missed_is_added()
    {
        var first = new List<OcrTextBlock> { Block("文句言える立場じゃねえんだよ！", new Rect(573, 179, 107, 145)) };
        var second = new[] { Block("パーティに付与術士が必要になったから", new Rect(639, 380, 180, 284)) };

        var merged = VerticalSecondLook.Merge(first, second);

        Assert.Equal(2, merged.Count);
        Assert.Contains("パーティに付与術士が必要になったから", merged.Select(block => block.Text));
    }

    /// <summary>
    /// The same balloon read whole beats the same balloon read in pieces, at equal length.
    /// </summary>
    /// <remarks>
    /// region0-084148-301: the native reading splits this balloon into パーティに付与術士が and
    /// 必要になったから, eighteen characters in two groups, and translating two halves of a sentence
    /// is what the user reported. At 0.77 of native it comes back as one group of the same eighteen.
    /// </remarks>
    [Fact]
    public void A_balloon_read_whole_replaces_the_same_balloon_read_in_pieces()
    {
        var first = new List<OcrTextBlock>
        {
            Block("パーティに付与術士が", new Rect(715, 380, 94, 284)),
            Block("必要になったから", new Rect(639, 380, 76, 284)),
        };
        var second = new[] { Block("パーティに付与術士が必要になったから", new Rect(639, 380, 180, 284)) };

        var merged = VerticalSecondLook.Merge(first, second);

        Assert.Equal(["パーティに付与術士が必要になったから"], merged.Select(block => block.Text));
    }

    /// <summary>And the other way round: a piece does not displace the sentence.</summary>
    [Fact]
    public void Pieces_do_not_replace_a_balloon_the_first_reading_got_whole()
    {
        var first = new List<OcrTextBlock>
        {
            Block("パーティに付与術士が必要になったから", new Rect(639, 380, 180, 284)),
        };
        var second = new[]
        {
            Block("パーティに付与術士が", new Rect(715, 380, 94, 284)),
            Block("必要になったから", new Rect(639, 380, 76, 284)),
        };

        var merged = VerticalSecondLook.Merge(first, second);

        Assert.Equal(["パーティに付与術士が必要になったから"], merged.Select(block => block.Text));
    }

    /// <summary>A reading that found another column wins on the characters.</summary>
    [Fact]
    public void The_reading_that_found_more_of_the_balloon_wins()
    {
        var first = new List<OcrTextBlock> { Block("必要になったから", new Rect(639, 380, 76, 284)) };
        var second = new[] { Block("パーティに付与術士が必要になったから", new Rect(639, 380, 180, 284)) };

        var merged = VerticalSecondLook.Merge(first, second);

        Assert.Equal(["パーティに付与術士が必要になったから"], merged.Select(block => block.Text));
    }

    /// <summary>Two balloons far apart are two places, and both survive.</summary>
    [Fact]
    public void Balloons_that_do_not_overlap_are_kept_apart()
    {
        var first = new List<OcrTextBlock> { Block("俺の本職は剣士なんだから", new Rect(1083, 168, 96, 252)) };
        var second = new[] { Block("ゴチャゴチャうるせぇ！", new Rect(1503, 679, 84, 194)) };

        Assert.Equal(2, VerticalSecondLook.Merge(first, second).Count);
    }

    /// <summary>
    /// Two sizes that land on the same step of the detector's alignment are the same read.
    /// </summary>
    [Fact]
    public void A_second_size_too_close_to_the_first_is_not_worth_a_read()
    {
        Assert.Null(VerticalSecondLook.OtherSize(1832, 1298, primary: 1440));
        Assert.NotNull(VerticalSecondLook.OtherSize(1832, 1298, primary: 1832));
    }

    /// <summary>A region small enough to be inside the detector's range is read once.</summary>
    [Fact]
    public void A_small_region_is_read_once()
    {
        Assert.Null(VerticalSecondLook.OtherSize(700, 400, primary: 700));
    }
}
