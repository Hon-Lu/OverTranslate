using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The identities a grouping diagnostic refers to its lines by, and the guarantee that asking for
/// them changes nothing.
/// </summary>
public class GroupingTraceTests
{
    [Fact]
    public void AMergedLineCanBeFollowedBackToTheBoxesItWasBuiltFrom()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("寧夏", new Rect(10, 10, 42, 24)),
            new("夜市", new Rect(58, 10, 42, 24)),
            new("攻略", new Rect(106, 10, 42, 24)),
        }.AsDetected();

        var trace = new GroupingTrace();
        OcrTextBlockGrouper.Group(blocks, GroupingProfile.Interface, null, trace);

        // Three boxes in, one line out, and the line says which three. Nothing on a merged block
        // itself points back at them — it is a new record built from a union of rectangles — so
        // without this the two halves of a report cannot be read against each other.
        Assert.Equal(["b0", "b1", "b2"], Assert.Single(trace.Lines).SourceIds);
        Assert.Equal("L0", trace.Lines[0].Id);
        Assert.Equal(["L0"], Assert.Single(trace.Groups));
    }

    [Fact]
    public void LinesAreNumberedInTheOrderTheNextLineRulesSeeThem()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("BOTTOM", new Rect(10, 200, 120, 24)),
            new("TOP", new Rect(10, 10, 120, 24)),
        }.AsDetected();

        var trace = new GroupingTrace();
        OcrTextBlockGrouper.Group(blocks, GroupingProfile.Interface, null, trace);

        // Block ids follow the detector's order and line ids follow the page's, which is why they
        // are two namespaces rather than one: b0 is the bottom line and L0 is the top one.
        Assert.Equal("BOTTOM", trace.Blocks[0].Block.Text);
        Assert.Equal("TOP", trace.Lines[0].Block.Text);
        Assert.Equal(["b1"], trace.Lines[0].SourceIds);
    }

    [Fact]
    public void ThreeFragmentsCombineOneMergeAtATimeAndNotAsAMedianOfTheThree()
    {
        // Estimates 10, 20 and 30 on one row. Each merge keeps the upper median of the two heights
        // in hand — on two values, the larger — so the fragments combine 10,20 -> 20 and then
        // 20,30 -> 30. The median of the three members is 20, and the line does not carry it.
        //
        // Pinned because the trace called this "MedianOfMembers" for one round. No corpus verdict
        // turns on the difference today; a diagnostic that describes it wrongly is what the next
        // round would have been reasoned from.
        var blocks = new List<OcrTextBlock>
        {
            Fragment("aaa", new Rect(10, 10, 40, 20), glyphHeight: 10),
            Fragment("bbb", new Rect(56, 10, 40, 20), glyphHeight: 20),
            Fragment("ccc", new Rect(102, 10, 40, 20), glyphHeight: 30),
        };

        var trace = new GroupingTrace();
        var grouped = OcrTextBlockGrouper.Group(blocks, GroupingProfile.Interface, null, trace);

        Assert.Equal(["b0", "b1", "b2"], Assert.Single(trace.Lines).SourceIds);
        Assert.Equal(30, Assert.Single(grouped).LayoutGlyphHeight);
    }

    [Fact]
    public void AMergeThatEndsUpMixedCarriesNoEstimateAtAll()
    {
        // Two fragments that each have one, joined into text that is of neither script. The
        // combination is not a smaller number or a larger one — there is nothing there, and the
        // trace has to say so rather than name a source it did not come from.
        var blocks = new List<OcrTextBlock>
        {
            Fragment("abc", new Rect(10, 10, 40, 20), glyphHeight: 10, OcrLayoutScript.Latin),
            Fragment("日本", new Rect(56, 10, 40, 20), glyphHeight: 16, OcrLayoutScript.Cjk),
        };

        var grouped = OcrTextBlockGrouper.Group(blocks, GroupingProfile.Interface, null, new GroupingTrace());

        var line = Assert.Single(grouped);
        Assert.Equal(OcrLayoutScript.Mixed, line.LayoutScript);
        Assert.Null(line.LayoutGlyphHeight);
    }

    [Fact]
    public void AskingForTheTraceChangesNothingAboutTheGrouping()
    {
        var blocks = new List<OcrTextBlock>
        {
            new("Since its initial release, PaddleOCR has", new Rect(147, 24, 1742, 29)),
            new("proven performance in real-world use", new Rect(147, 62, 1771, 31)),
            new("cost.", new Rect(145, 648, 62, 31)),
            new("On June 11, 2026, PaddleOCR released", new Rect(148, 711, 1755, 27)),
        }.AsDetected();

        var untraced = OcrTextBlockGrouper.Group(blocks, GroupingProfile.General);
        var traced = OcrTextBlockGrouper.Group(blocks, GroupingProfile.General, [], new GroupingTrace());

        Assert.Equal(
            untraced.Select(group => group.Text),
            traced.Select(group => group.Text));
        Assert.Equal(
            untraced.Select(group => group.Lines.Count),
            traced.Select(group => group.Lines.Count));
    }

    /// <summary>
    /// A detected fragment with its estimate set by hand, so a merge can be watched combining
    /// values that differ. Real fragments of one line usually estimate within a pixel of each
    /// other, which hides exactly the behaviour under test.
    /// </summary>
    private static OcrTextBlock Fragment(
        string text, Rect box, double glyphHeight, OcrLayoutScript script = OcrLayoutScript.Latin) =>
        new(text, box, LayoutScript: script, LayoutBounds: box, LayoutGlyphHeight: glyphHeight);
}
