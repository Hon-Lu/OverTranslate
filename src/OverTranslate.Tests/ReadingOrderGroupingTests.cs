using System.Windows;
using System.IO;
using System.Text.Json;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class ReadingOrderGroupingTests
{
    [Theory]
    [InlineData(0.5, false)]
    [InlineData(1.0, false)]
    [InlineData(2.0, false)]
    [InlineData(1.0, true)]
    public void CapturedArticleRetainsEveryFragmentInReadingOrder(double scale, bool interfaceMode)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "WebParagraphs", "split-rows.json")));
        var blocks = json.RootElement[0].GetProperty("Blocks").EnumerateArray().Select(item =>
        {
            Rect Box(string name)
            {
                var a = item.GetProperty(name).EnumerateArray().Select(v => v.GetDouble() * scale).ToArray();
                return new(a[0], a[1], a[2], a[3]);
            }
            double? Metric(string name) => item.GetProperty(name).ValueKind == JsonValueKind.Null
                ? null : item.GetProperty(name).GetDouble() * scale;
            return new OcrTextBlock(item.GetProperty("Text").GetString()!, Box("Bounds"),
                LayoutBounds: Box("LayoutBounds"), LayoutScript: (OcrLayoutScript)item.GetProperty("Script").GetInt32(),
                LayoutGlyphHeight: Metric("Glyph"), RenderGlyphHeight: Metric("Render")) { LayoutInkHeight = Metric("Ink") };
        }).ToArray();
        var trace = new GroupingTrace();
        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        var profile = interfaceMode ? GroupingProfile.Interface : GroupingProfile.General;
        var groups = OcrTextBlockGrouper.Group(blocks, profile, decisions, trace);
        var sources = trace.Lines.ToDictionary(line => line.Id, line => line.SourceIds);
        Assert.Equal(new[] { "b0", "b2", "b1", "b3", "b4", "b5", "b6", "b7", "b8", "b9" },
            trace.Groups.SelectMany(group => group.SelectMany(id => sources[id])));
        Assert.Equal(string.Join(" ", new[] { 0, 2, 1, 3, 4, 5, 6, 7, 8, 9 }.Select(i => blocks[i].Text)),
            string.Join(" ", groups.Select(g => g.Text)));
        if (interfaceMode)
            Assert.Contains(decisions, d => !d.Joined && d.Rule == "unresolved row fragment");
        else
            Assert.Single(groups);
        Assert.Equal(groups.Select(g => g.Text), OcrTextBlockGrouper.Group(blocks, profile).Select(g => g.Text));
    }

    [Fact]
    public void EmptyCornerOfAParagraphDoesNotAbsorbAnIndependentLabel()
    {
        var blocks = new[] {
            B("A wide first line of an ordinary paragraph", 0, 0, 500, 18),
            B("a shorter concluding sentence.", 0, 20, 240, 18),
            B("TEST!!!", 430, 22, 60, 12) };
        var groups = OcrTextBlockGrouper.Group(blocks, GroupingProfile.General);
        Assert.Contains(groups, g => g.Text == "TEST!!!");
        Assert.DoesNotContain(groups, g => g.Text.Contains("paragraph") && g.Text.Contains("TEST!!!"));
    }

    [Theory]
    [InlineData(0.5, false)]
    [InlineData(1.0, false)]
    [InlineData(2.0, false)]
    [InlineData(1.0, true)]
    public void SplitVisualRowCannotBeSkippedByAWrappingLine(double scale, bool interfaceMode)
    {
        var blocks = new[] {
            B("nav rail still offers the update", 2, 38, 233, 18, scale),
            B("because what the user declined was being interrupted, not", 256, 39, 404, 18, scale),
            B("the update itself. See the reference.", 3, 55, 427, 17, scale) };
        var groups = OcrTextBlockGrouper.Group(blocks, interfaceMode ? GroupingProfile.Interface : GroupingProfile.General);
        Assert.Equal(string.Join(" ", blocks.Select(b => b.Text)), string.Join(" ", groups.Select(b => b.Text)));
    }

    [Fact]
    public void SpanningPreviousLineCannotTakeOnlyOneHalfOfTheFollowingRow()
    {
        var blocks = new[] {
            B("A long introductory sentence continues with these details", 2, 20, 658, 18),
            B("nav rail still offers the update", 2, 38, 233, 18),
            B("because what the user declined was being interrupted, not", 256, 39, 404, 18) };
        var groups = OcrTextBlockGrouper.Group(blocks, GroupingProfile.General);
        Assert.Equal(3, groups.Count);
    }

    [Fact]
    public void IndependentColumnsCanStillContinueDownward()
    {
        var blocks = new[] {
            B("The left column has a long opening line", 0, 0, 300, 18),
            B("The right column has its own opening", 400, 0, 300, 18),
            B("and continues in the left column.", 0, 19, 280, 18),
            B("and continues in the right column.", 400, 19, 280, 18) };
        var groups = OcrTextBlockGrouper.Group(blocks, GroupingProfile.General);
        Assert.Equal(new[] { blocks[0].Text + " " + blocks[2].Text, blocks[1].Text + " " + blocks[3].Text }, groups.Select(g => g.Text));
    }

    private static OcrTextBlock B(string text, double x, double y, double w, double h, double scale = 1)
    {
        var box = new Rect(x * scale, y * scale, w * scale, h * scale);
        var script = LayoutScriptDetection.For(text);
        return new(text, box, LayoutScript: script, LayoutBounds: box,
            LayoutGlyphHeight: OnnxOcrEngine.LayoutGlyphHeightFor(script, box, text));
    }
}
