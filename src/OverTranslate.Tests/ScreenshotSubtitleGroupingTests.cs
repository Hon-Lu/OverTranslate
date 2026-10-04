using System.Text.Json;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class ScreenshotSubtitleGroupingTests
{
    [Theory]
    [InlineData("speaker")]
    [InlineData("bullet")]
    [InlineData("gap")]
    [InlineData("column")]
    [InlineData("size")]
    [InlineData("latin")]
    [InlineData("interface")]
    [InlineData("vertical")]
    public void IndependentRowsStaySeparate(string reason)
    {
        var first = new OcrTextBlock("気安く触れないでほしい。", new Rect(0, 0, 540, 60),
            LayoutScript: OcrLayoutScript.Cjk, LayoutBounds: new Rect(0, 0, 540, 60), LayoutGlyphHeight: 49);
        var second = first with { Text = "忘れてもらっては困る。私は君が嫌いなんだ。",
            Bounds = new Rect(0, 72, 980, 60), LayoutBounds = new Rect(0, 72, 980, 60) };
        switch (reason)
        {
            case "speaker": first = first with { Text = "しろは", LayoutBounds = new Rect(0, 0, 180, 60) }; break;
            case "bullet": second = second with { Text = "• " + second.Text }; break;
            case "gap": second = second with { LayoutBounds = new Rect(0, 110, 980, 60) }; break;
            case "column": second = second with { LayoutBounds = new Rect(600, 72, 980, 60) }; break;
            case "size": second = second with { LayoutGlyphHeight = 30 }; break;
            case "latin":
                first = first with { Text = "This sentence is complete.", LayoutScript = OcrLayoutScript.Latin };
                second = second with { Text = "Another independent sentence.", LayoutScript = OcrLayoutScript.Latin }; break;
        }
        var profile = reason == "interface" ? GroupingProfile.Interface :
            reason == "vertical" ? GroupingProfile.Vertical : GroupingProfile.General;
        Assert.Equal(2, OcrTextBlockGrouper.Group([first, second], profile).Count);
    }

    [Fact]
    public void JapaneseGameBodyRowsJoinWithoutSpeakerOrControls()
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "screenshot-subtitle-ja.json")));
        foreach (var input in data.RootElement.EnumerateArray())
        {
            var blocks = input.GetProperty("Blocks").EnumerateArray().Select(b =>
            {
                Rect Box(string key)
                {
                    var r = b.GetProperty(key).EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    return new(r[0], r[1], r[2], r[3]);
                }
                double? Number(string key) => b.GetProperty(key).ValueKind == JsonValueKind.Null
                    ? null : b.GetProperty(key).GetDouble();
                return new OcrTextBlock(b.GetProperty("Text").GetString()!, Box("Bounds"),
                    LayoutScript: (OcrLayoutScript)b.GetProperty("Script").GetInt32(),
                    LayoutBounds: Box("LayoutBounds"), LayoutGlyphHeight: Number("Glyph"))
                    { LayoutInkHeight = Number("Ink") };
            }).ToArray();
            var expected = input.GetProperty("BodyIds").EnumerateArray().Select(v => v.GetString()!).ToArray();
            var trace = new GroupingTrace();
            OcrTextBlockGrouper.Group(blocks, GroupingProfile.General, null, trace);
            var sources = trace.Lines.ToDictionary(l => l.Id, l => l.SourceIds);
            var groups = trace.Groups.Select(g => g.SelectMany(l => sources[l]).ToArray()).ToArray();
            Assert.True(groups.Any(g => g.SequenceEqual(expected)), input.GetProperty("Name").GetString());
        }
    }
}
