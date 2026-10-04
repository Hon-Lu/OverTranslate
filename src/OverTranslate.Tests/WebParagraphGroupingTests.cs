using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class WebParagraphGroupingTests
{
    // Captured, full-precision OCR inputs, not a reimplementation of detection or estimates.
    public record Block(string Text, double[] Bounds, double[] LayoutBounds,
        OcrLayoutScript Script, double? Glyph, double? Render, double? Confidence, double? Ink)
    {
        public OcrTextBlock Restore(double scale = 1) => new(Text, Box(Bounds, scale),
            RenderGlyphHeight: Render * scale, Confidence: Confidence, LayoutScript: Script,
            LayoutBounds: Box(LayoutBounds, scale), LayoutGlyphHeight: Glyph * scale)
            { LayoutInkHeight = Ink * scale };
        private static Rect Box(double[] b, double s) => new(b[0] * s, b[1] * s, b[2] * s, b[3] * s);
    }
    public record Capture(string Image, Block[] Blocks);
    public record Result(string Image, string Profile, string[][] Groups);

    private static T Read<T>(string name) => JsonSerializer.Deserialize<T>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WebParagraphs", name)))!;

    public static TheoryData<int, string> Expected => new()
    {
        { 0, "b0|b1|b2|b3,b4" },
        { 1, "b0|b1|b2|b3,b4|b5|b6|b7" },
        { 2, "b0,b1,b2|b3,b4,b5,b6,b7,b8|b9,b10,b11,b12,b13,b14,b15|b16,b17,b18,b19,b20,b21,b22|b23" },
        { 3, "b0,b1|b2|b3,b4,b5|b6,b7,b8|b9,b10,b11|b12,b13,b14" },
        { 4, "b0,b1,b2,b3|b4,b5|b6|b7,b8,b9|b10,b11,b12|b13,b14" },
    };

    [Theory]
    [MemberData(nameof(Expected))]
    public void GeneralMatchesMarkedParagraphMembers(int index, string expected)
    {
        foreach (var scale in new[] { 0.5, 1.0, 2.0 })
        {
            var blocks = Read<Capture[]>("web.json")[index].Blocks.Select(b => b.Restore(scale)).ToArray();
            Assert.Equal(expected, Members(blocks, GroupingProfile.General));
        }
    }

    [Fact]
    public void IsolatedShortFinalWordDoesNotGetTheParagraphException()
    {
        var blocks = Read<Capture[]>("web.json")[2].Blocks.Skip(14).Take(2).Select(b => b.Restore()).ToArray();
        Assert.Equal("b0|b1", Members(blocks, GroupingProfile.General));
    }

    [Fact]
    public void BiggerOrIndependentWarningAfterEstablishedParagraphStaysSeparate()
    {
        var blocks = Read<Capture[]>("web.json")[2].Blocks.Skip(9).Take(7).Select(b => b.Restore()).ToArray();
        blocks[^1] = blocks[^1] with { Text = "Warning." };
        Assert.Equal("b0,b1,b2,b3,b4,b5|b6", Members(blocks, GroupingProfile.General));
        blocks[^1] = blocks[^1] with { Text = "warning.", LayoutBounds = new Rect(145, 648, 130, 48) };
        Assert.Equal("b0,b1,b2,b3,b4,b5|b6", Members(blocks, GroupingProfile.General));
    }

    [Fact]
    public void EarlierMixedSizeAndDirectionCounterexamplesDoNotRegress()
    {
        var inputs = Read<Capture[]>("counter.json");
        var baseline = Read<Result[]>("counter-baseline.json");
        foreach (var input in inputs)
            foreach (var profile in new[] { "general", "interface" })
            {
                var expected = baseline.Single(b => b.Image == input.Image && b.Profile == profile);
                Assert.Equal(string.Join("|", expected.Groups.Select(g => string.Join(",", g))),
                    Members(input.Blocks.Select(b => b.Restore()).ToArray(),
                        profile == "general" ? GroupingProfile.General : GroupingProfile.Interface));
            }
    }

    [Fact]
    public void InterfaceKeepsItsExistingMembersForAllFiveImages()
    {
        var baseline = Read<Result[]>("web-baseline.json");
        foreach (var input in Read<Capture[]>("web.json"))
        {
            var expected = baseline.Single(b => b.Image == input.Image && b.Profile == "interface");
            Assert.Equal(string.Join("|", expected.Groups.Select(g => string.Join(",", g))),
                Members(input.Blocks.Select(b => b.Restore()).ToArray(), GroupingProfile.Interface));
        }
    }

    [Fact]
    public void SyntheticFlatBackgroundInkSeparatesHeadingWithoutChangingRenderMetrics()
    {
        // Construct the signal in memory; no screenshot fixture is needed for the pixel sampler.
        using var image = new Bitmap(910, 187);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.White);
            // Leave the background dominant, as it would be between real glyph strokes.
            foreach (var (x, y, width, height) in new[] { (30, 77, 326, 20), (31, 114, 803, 16), (29, 141, 517, 16) })
                for (int offset = 0; offset + 3 <= width; offset += 8)
                    graphics.FillRectangle(System.Drawing.Brushes.Black, x + offset, y, 3, height);
        }
        var blocks = Read<Capture[]>("web.json")[0].Blocks.Select(b => b.Restore() with { LayoutInkHeight = null }).ToList();
        var measured = OcrService.PrepareScreenshotGrouping(image, blocks, GroupingProfile.General);
        Assert.Equal(20, measured[2].LayoutInkHeight);
        Assert.Equal(16, measured[3].LayoutInkHeight);
        Assert.Equal(blocks.Select(b => b.LayoutGlyphHeight), measured.Select(b => b.LayoutGlyphHeight));
        Assert.Equal(blocks.Select(b => b.RenderGlyphHeight), measured.Select(b => b.RenderGlyphHeight));
        Assert.Equal(blocks.Select(b => b.Bounds), measured.Select(b => b.Bounds));
        Assert.Equal("b0|b1|b2|b3,b4", Members(measured, GroupingProfile.General));
    }

    [Fact]
    public void InkEvidenceAbstainsOnEmptyOrNonTextGeometry()
    {
        using var pixels = new SkiaSharp.SKBitmap(200, 60);
        pixels.Erase(SkiaSharp.SKColors.White);
        Assert.Null(TextInkMetrics.Measure(pixels, new Rect(0, 0, 200, 20)));
        Assert.Null(TextInkMetrics.Measure(pixels, new Rect(0, 0, 10, 20)));
        Assert.Null(TextInkMetrics.Measure(pixels, new Rect(500, 0, 200, 20)));
    }

    private static string Members(IReadOnlyList<OcrTextBlock> blocks, GroupingProfile profile)
    {
        var trace = new GroupingTrace();
        var groups = OcrTextBlockGrouper.Group(blocks, profile, null, trace);
        var withoutTrace = OcrTextBlockGrouper.Group(blocks, profile);
        Assert.Equal(groups.Count, withoutTrace.Count);
        foreach (var (traced, plain) in groups.Zip(withoutTrace))
        {
            // Record equality compares SourceLineBounds by reference; compare its contents
            // separately so two independent grouping passes can be checked meaningfully.
            Assert.Equal(traced with { SourceLineBounds = null }, plain with { SourceLineBounds = null });
            Assert.Equal<Rect>(traced.Lines, plain.Lines);
        }
        var ids = trace.Lines.ToDictionary(l => l.Id, l => l.SourceIds);
        return string.Join("|", trace.Groups.Select(g => string.Join(",", g.SelectMany(l => ids[l]))));
    }
}
