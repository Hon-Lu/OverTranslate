using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class ParagraphRowEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaggeredCutsUnderACompleteRowRejoinInVisualOrder(bool reversedInput)
    {
        var input = Article();
        var expected = string.Join(" ", input.Select(b => b.Text));
        if (reversedInput) Array.Reverse(input);
        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        var output = OcrTextBlockGrouper.Group(input, GroupingProfile.General, decisions);
        Assert.Equal(expected, Assert.Single(output).Text);
        Assert.Equal(2, decisions.Count(d => d.Joined && d.Rule == "paragraph row evidence"));
    }

    [Theory]
    [InlineData("no-anchor")]
    [InlineData("large-heading")]
    [InlineData("repeated-gutter")]
    [InlineData("wide-gap")]
    [InlineData("loose-rows")]
    [InlineData("mixed-script")]
    public void InsufficientOrColumnLikeEvidenceDoesNotEnableNewRowMerges(string variant)
    {
        var input = Article();
        switch (variant)
        {
            case "no-anchor": input = input.Skip(1).ToArray(); break;
            case "large-heading": input[0] = B(input[0].Text, 0, -15, 660, 35); break;
            case "repeated-gutter":
                input[3] = B(input[3].Text, 0, 40, 610);
                input[4] = B(input[4].Text, 634, 40, 26);
                break;
            case "wide-gap":
                input[1] = B(input[1].Text, 0, 20, 580);
                input[3] = B(input[3].Text, 0, 40, 180);
                break;
            case "loose-rows":
                input[3] = B(input[3].Text, 0, 60, 230);
                input[4] = B(input[4].Text, 254, 60, 406);
                break;
            case "mixed-script": input[0] = input[0] with { LayoutScript = OcrLayoutScript.Mixed }; break;
        }
        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        var trace = new GroupingTrace();
        OcrTextBlockGrouper.Group(input, GroupingProfile.General, decisions, trace);
        Assert.DoesNotContain(decisions, d => d.Joined && d.Rule == "paragraph row evidence");
        // In particular, the short right fragment on row two must remain separate.
        var right = Array.FindIndex(input, b => b.Text == "the");
        Assert.Contains(trace.Lines, line => line.SourceIds.SequenceEqual(new[] { $"b{right}" }));
    }

    [Fact]
    public void NonGeneralProfilesKeepTheirExistingRowDecisions()
    {
        foreach (var profile in new[] { GroupingProfile.Interface, GroupingProfile.Realtime })
        {
            var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
            OcrTextBlockGrouper.Group(Article(), profile, decisions);
            Assert.DoesNotContain(decisions, d => d.Rule == "paragraph row evidence");
        }
    }

    private static OcrTextBlock[] Article() => [
        B("A complete line establishes the edges of this ordinary paragraph", 0, 0, 660),
        B("The next line continues until the missing separator before", 0, 20, 610),
        B("the", 634, 20, 26),
        B("next part continues", 0, 40, 230),
        B("with the rest of this last sentence.", 254, 40, 406)];

    private static OcrTextBlock B(string text, double x, double y, double width, double height = 20)
    {
        var box = new Rect(x, y, width, height);
        return new(text, box, LayoutBounds: box, LayoutScript: OcrLayoutScript.Latin,
            LayoutGlyphHeight: 12, RenderGlyphHeight: 12);
    }
}
