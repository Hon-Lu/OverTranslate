using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// Two boxes on one row, close enough for the same-row test to join, with and without a panel's
/// frame drawn between them — the JRPG party menu whose 「Lv 54 HP2015/2277」 was joined to the next
/// panel's 「月齢 上弦の月」, reduced to the geometry that matters.
/// </summary>
public class PanelEdgesTests
{
    private const int FrameWidth = 900;
    private const int FrameHeight = 300;

    // 12px apart at a height of 30: 0.4 of a line, under the 0.45 a lone gap is judged by, so the
    // boxes alone say "one line".
    private static readonly Rect LeftBox = new(200, 130, 380, 30);
    private static readonly Rect RightBox = new(592, 131, 200, 30);

    [Fact]
    public void FrameBetweenRowNeighbours_KeepsThemApart()
    {
        using var frame = Panels(frameTop: 0, frameBottom: FrameHeight);

        Assert.Equal(2, Group(frame).Count);
    }

    [Fact]
    public void WithoutTheFrameToAsk_TheSameBoxesJoin()
    {
        // What the rule changes and nothing else: the same boxes, no pixels, one line.
        Assert.Single(OcrTextBlockGrouper.Group(Row(), GroupingProfile.Realtime, null, null));
    }

    [Fact]
    public void PlainBackgroundBetweenRowNeighbours_StillJoins()
    {
        using var frame = Panels(frameTop: 0, frameBottom: 0);

        Assert.Single(Group(frame));
    }

    [Fact]
    public void SeparatorNoTallerThanTheRow_StillJoins()
    {
        // A "|" between two words of a bar is drawn to the row's height; only a frame runs past it.
        using var frame = Panels(frameTop: 130, frameBottom: 160);

        Assert.Single(Group(frame));
    }

    [Fact]
    public void RowAtTheTopOfTheBlock_SeesTheFrameBelowIt()
    {
        // Nothing above the row to look at: the frame running on below it is enough.
        using var frame = Panels(frameTop: 0, frameBottom: FrameHeight);

        Assert.Equal(2, OcrTextBlockGrouper.Group(Row(top: 4), GroupingProfile.Realtime, null, null, new PanelEdges(frame)).Count);
    }

    [Fact]
    public void LiveGrouping_PassesTheFrameToEveryPanelGrouper()
    {
        using var frame = Panels(frameTop: 0, frameBottom: FrameHeight);
        var edges = new PanelEdges(frame);

        // 逐項, 不合併 and 自動 (whose panel side is the same grouper).
        Assert.Equal(2, OcrService.GroupRealtime(Row(), FrameHeight, ResolvedProfile.Interface, edges: edges).Count);
        Assert.Equal(2, OcrService.GroupRealtime(
            Row(), FrameHeight, ResolvedProfile.Interface with { Merge = RealtimeMerge.Lines }, edges: edges).Count);
        Assert.Equal(2, OcrService.GroupRealtime(Row(), FrameHeight, ResolvedProfile.Standard, edges: edges).Count);
    }

    private static List<OcrTextBlock> Group(Bitmap frame) =>
        OcrTextBlockGrouper.Group(Row(), GroupingProfile.Realtime, null, null, new PanelEdges(frame));

    private static List<OcrTextBlock> Row(double top = 130) =>
    [
        Box("Lv 54 HP2015/2277", LeftBox with { Y = top }),
        Box("Moon Phase", RightBox with { Y = top + 1 }),
    ];

    private static OcrTextBlock Box(string text, Rect bounds) =>
        new(text, bounds, Confidence: 0.99, LayoutScript: OcrLayoutScript.Latin, LayoutBounds: bounds,
            LayoutGlyphHeight: bounds.Height * 0.7);

    // Two blue panels and, from frameTop to frameBottom, the bevelled frame between them, in the
    // 12px gap. Nothing is drawn when the two are equal.
    private static Bitmap Panels(int frameTop, int frameBottom)
    {
        var bitmap = new Bitmap(FrameWidth, FrameHeight);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(30, 40, 120));
        if (frameBottom > frameTop)
        {
            using var light = new SolidBrush(Color.FromArgb(200, 200, 200));
            using var mid = new SolidBrush(Color.FromArgb(120, 120, 130));
            g.FillRectangle(mid, 582, frameTop, 2, frameBottom - frameTop);
            g.FillRectangle(light, 584, frameTop, 3, frameBottom - frameTop);
            g.FillRectangle(mid, 587, frameTop, 2, frameBottom - frameTop);
        }

        return bitmap;
    }
}
