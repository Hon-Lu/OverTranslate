using OverTranslate.Services.Ocr;
using SkiaSharp;
using Xunit;
using TextBox = RapidOcrNet.TextBox;

namespace OverTranslate.Tests;

/// <summary>
/// One detector quad around two columns, cut apart on the page's own pixels.
/// </summary>
/// <remarks>
/// On pixels rather than on rectangles, because that is what the splitter reads. The shapes below
/// are the shape of the real case off the comic pages: a sentence set at full size with its reading
/// printed small beside it, both inside one quad that the detector drew around the pair.
/// </remarks>
public class VerticalColumnSplitTests
{
    [Fact]
    public void A_short_part_is_not_given_the_height_of_the_column_beside_it()
    {
        using var page = Page(
            // The sentence: eight characters down the full height of the quad.
            marks: Stack(40, 26, 20, 12, 8),
            // The reading: two small ones, beside the middle of it.
            reading: Stack(92, 10, 70, 8, 2));

        var parts = VerticalColumnDetection.Split(page, [Quad(30, 10, 80, 180)]);

        Assert.Equal(2, parts.Count);
        var (sentence, small) = Order(parts);
        Assert.InRange(Height(sentence), 150, 180);
        // The reading is 30 tall. Anything near the quad's 180 is the bug this covers: at that
        // height its characters measure bigger than the sentence's and it reads as a column.
        Assert.InRange(Height(small), 30, 70);
    }

    /// <summary>
    /// A part as long as the quad keeps the quad's own edges, slack and all.
    /// </summary>
    /// <remarks>
    /// Grouping compares one column's length against the next one's, so a part cut tight beside a
    /// quad that was never cut is a difference in how they were measured rather than in what they
    /// say. MEASURED: trimming every part to its ink took three balloons on the fifteen comic pages
    /// apart that had been grouping correctly.
    /// </remarks>
    [Fact]
    public void A_part_that_runs_the_whole_quad_keeps_the_quads_edges()
    {
        using var page = Page(
            marks: Stack(40, 26, 20, 12, 8),
            reading: Stack(92, 10, 20, 12, 8));

        var parts = VerticalColumnDetection.Split(page, [Quad(30, 10, 80, 180)]);

        Assert.Equal(2, parts.Count);
        foreach (var part in parts)
            Assert.Equal(180, Height(part));
    }

    [Fact]
    public void A_quad_holding_one_column_is_left_alone()
    {
        using var page = Page(marks: Stack(40, 26, 20, 12, 8), reading: []);

        var parts = VerticalColumnDetection.Split(page, [Quad(30, 10, 46, 180)]);

        Assert.Single(parts);
    }

    /// <summary>A column of characters, as marks down the page rather than one solid bar.</summary>
    /// <remarks>
    /// Solid would be wrong here rather than merely unrealistic: the splitter only reads a box whose
    /// background is one flat colour over most of it, which is what tells a balloon from artwork.
    /// </remarks>
    private static (int X, int Y, int W, int H)[] Stack(
        int x, int width, int top, int height, int count) =>
        [.. Enumerable.Range(0, count).Select(i => (x, top + i * 20, width, height))];

    private static SKBitmap Page(
        (int X, int Y, int W, int H)[] marks, (int X, int Y, int W, int H)[] reading)
    {
        var bitmap = new SKBitmap(200, 220);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var ink = new SKPaint { Color = SKColors.Black };
            foreach (var (x, y, w, h) in marks.Concat(reading))
                canvas.DrawRect(x, y, w, h, ink);
        }

        return bitmap;
    }

    private static TextBox Quad(int x, int y, int width, int height) => new()
    {
        Score = 0.9f,
        BoxPoints =
        [
            new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height),
        ],
    };

    private static int Height(TextBox box) =>
        box.BoxPoints.Max(p => p.Y) - box.BoxPoints.Min(p => p.Y);

    private static (TextBox Right, TextBox Left) Order(IReadOnlyList<TextBox> parts)
    {
        var ordered = parts.OrderBy(p => p.BoxPoints.Min(q => q.X)).ToArray();
        return (ordered[0], ordered[1]);
    }
}
