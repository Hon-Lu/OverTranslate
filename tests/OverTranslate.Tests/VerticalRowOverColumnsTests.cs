using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// A row drawn across a balloon is the head of that balloon misread, and must not be set over it.
/// </summary>
/// <remarks>
/// The tops of two neighbouring columns sit side by side, and with a reading between them they make
/// a short wide patch the detector frames as one box running across. Every figure below is a real
/// pair off the two vertical corpora, read at the capture scales where this happens.
/// </remarks>
public class VerticalRowOverColumnsTests
{
    private static OcrTextBlock Block(string text, Rect bounds, bool across) =>
        new(text, bounds) { LayoutBounds = bounds, RunsAcross = across };

    /// <summary>俺 and 剣 come back as 剣俺 laid over the balloon they were taken out of.</summary>
    [Fact]
    public void A_row_lying_on_a_balloon_is_dropped()
    {
        var balloon = Block("の本職は士なんだから", new Rect(1130, 214, 100, 218), across: false);
        var heads = Block("剣俺", new Rect(1130, 188, 110, 49), across: true);

        var kept = VerticalColumnGrouping.WithoutRowsOverColumns([balloon, heads]);

        Assert.Equal(["の本職は士なんだから"], kept.Select(block => block.Text));
    }

    /// <summary>
    /// A name plate keeps its name although the title above it arrives as a column.
    /// </summary>
    /// <remarks>
    /// 剣聖 is two characters at 47x34, inside what IsColumnCandidate calls a column, and it
    /// lies on 0.44 of the name. A row may only be overruled by writing that actually runs down the
    /// page.
    /// </remarks>
    [Fact]
    public void A_row_under_a_short_wide_box_is_kept()
    {
        var title = Block("剣聖", new Rect(1112, 397, 47, 34), across: false);
        var name = Block("オリヴァー・カーディフ", new Rect(1021, 416, 228, 44), across: true);

        Assert.Equal(2, VerticalColumnGrouping.WithoutRowsOverColumns([title, name]).Count);
    }

    /// <summary>A caption standing in its own space is writing of its own.</summary>
    [Fact]
    public void A_row_that_touches_no_column_is_kept()
    {
        var balloon = Block("恨んでくれて構わない", new Rect(1055, 769, 102, 191), across: false);
        var caption = Block("ギルドカードに記憶させると", new Rect(1028, 609, 263, 36), across: true);

        Assert.Equal(2, VerticalColumnGrouping.WithoutRowsOverColumns([balloon, caption]).Count);
    }

    /// <summary>A page with no columns on it at all is left alone.</summary>
    [Fact]
    public void Rows_on_a_page_of_rows_are_kept()
    {
        var one = Block("ディフェンダー", new Rect(1070, 1100, 168, 35), across: true);
        var other = Block("デリック・モーズレイ", new Rect(1036, 1129, 236, 34), across: true);

        Assert.Equal(2, VerticalColumnGrouping.WithoutRowsOverColumns([one, other]).Count);
    }
}
