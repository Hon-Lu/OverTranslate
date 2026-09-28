using OverTranslate.Services.Ocr;
using SkiaSharp;
using Xunit;
using TextBox = RapidOcrNet.TextBox;

namespace OverTranslate.Tests;

/// <summary>
/// One detector quad around columns written on the artwork, cut apart on the detector's own
/// probability map.
/// </summary>
/// <remarks>
/// Every page here has a busy background, so the pixel cut declines — no colour covers 60% of the
/// quad — and whatever is cut is cut by the map. That is the case the map exists for: narration over
/// a landscape, where there is no blank gutter to project. The map is at the page's own scale, as it
/// is on a screenshot the detector does not shrink.
/// </remarks>
public class VerticalProbabilitySplitTests
{
    private const int Width = 200;
    private const int Height = 220;

    [Fact]
    public void Two_columns_on_the_artwork_come_apart()
    {
        using var page = BusyPage();
        var map = Map(
            (40, 20, 15, 161),
            (70, 20, 15, 101),
            // The reading between them, which is what joined them in the library's dilated mask.
            (60, 30, 5, 10), (60, 60, 5, 10));

        var parts = VerticalColumnDetection.Split(page, [Quad(25, 5, 75, 190)], map);

        Assert.True(parts.Count >= 2);
        Assert.Contains(parts, p => Holds(p, 40, 55) && !Holds(p, 70, 85));
        Assert.Contains(parts, p => Holds(p, 70, 85) && !Holds(p, 40, 55));
    }

    /// <summary>
    /// The reading between two cut columns is not made a part of its own.
    /// </summary>
    /// <remarks>
    /// Made a part, it is read and put into the sentence. MEASURED over the 141 pages of
    /// <c>vertical-image-ja3</c>: 俺 of the gloss 俺の師匠 went into しかし俺おじいちゃんが.
    /// </remarks>
    [Fact]
    public void The_reading_between_cut_columns_is_left_out()
    {
        using var page = BusyPage();
        var map = Map((40, 20, 15, 161), (70, 20, 15, 101), (60, 30, 5, 10), (60, 60, 5, 10));

        var parts = VerticalColumnDetection.Split(page, [Quad(25, 5, 75, 190)], map);

        Assert.Equal(2, parts.Count);
    }

    /// <summary>
    /// A column's box is rebuilt as the library would build it around that column alone.
    /// </summary>
    /// <remarks>
    /// DB's unclip offsets the kernel's rectangle by area × ratio ÷ perimeter; the dilation adds a
    /// pixel. For a 15x161 kernel at ratio 1.4 that is 10.6, so the box runs from about 29 to 66.
    /// </remarks>
    [Fact]
    public void A_cut_column_gets_the_box_the_library_would_have_drawn()
    {
        using var page = BusyPage();
        var map = Map((40, 20, 15, 161), (80, 20, 15, 161));

        var parts = VerticalColumnDetection.Split(page, [Quad(10, 0, 120, 210)], map);

        var left = parts.OrderBy(p => p.BoxPoints.Min(q => q.X)).First();
        Assert.InRange(left.BoxPoints.Min(q => q.X), 28, 30);
        Assert.InRange(left.BoxPoints.Max(q => q.X), 65, 67);
        Assert.InRange(left.BoxPoints.Min(q => q.Y), 8, 10);
        Assert.InRange(left.BoxPoints.Max(q => q.Y), 191, 193);
    }

    /// <summary>
    /// A column and the reading beside it are one column, and the quad is left as it was drawn.
    /// </summary>
    /// <remarks>
    /// Cutting the reading away makes the column's box tighter than grouping was measured on.
    /// MEASURED on <c>2026-09-20 19 14 56.png</c>: <c>到達していない</c> cut from <c>とうたつ</c> narrowed
    /// from 52 to 33 and the narration it belongs to came apart in two.
    /// </remarks>
    [Fact]
    public void A_column_and_its_reading_are_not_cut()
    {
        using var page = BusyPage();
        var map = Map(
            [(40, 20, 15, 161), .. Enumerable.Range(0, 6).Select(i => (60, 20 + i * 25, 6, 10))]);
        var quad = Quad(25, 5, 50, 190);

        var parts = VerticalColumnDetection.Split(page, [quad], map);

        Assert.Same(quad, Assert.Single(parts));
    }

    /// <summary>
    /// Scribbled artwork lights the map thinly over an area, and is not a column.
    /// </summary>
    /// <remarks>
    /// MEASURED on <c>…-chapter-432/007</c> at the realtime second size: foliage made a strip filling
    /// 0.22 of itself, was counted as a column, and the part cut for it read as <c>82</c> and joined
    /// two neighbouring lines into one translation.
    /// </remarks>
    [Fact]
    public void Scribbled_artwork_beside_a_column_is_not_a_second_column()
    {
        using var page = BusyPage();
        var scribble = Enumerable.Range(0, 14).Select(i => (100, 60 + i * 6, 50, 1))
            .Append((100, 60, 1, 80));
        var map = Map([(40, 20, 16, 161), .. scribble]);
        var quad = Quad(25, 5, 140, 190);

        var parts = VerticalColumnDetection.Split(page, [quad], map);

        Assert.Same(quad, Assert.Single(parts));
    }

    [Fact]
    public void A_horizontal_line_is_not_cut_into_characters()
    {
        using var page = BusyPage();
        var map = Map([.. Enumerable.Range(0, 5).Select(i => (30 + i * 20, 50, 14, 14))]);
        var quad = Quad(20, 40, 120, 34);

        var parts = VerticalColumnDetection.Split(page, [quad], map);

        Assert.Same(quad, Assert.Single(parts));
    }

    /// <summary>
    /// A column another of the detector's boxes already holds is not read a second time.
    /// </summary>
    /// <remarks>
    /// Judged on the column's kernel, not on the box rebuilt around it. MEASURED on
    /// <c>mit-c.png</c>: the rebuilt box shared only 0.6 with the box already reading
    /// <c>やり過ごすだけ</c>, and the balloon came back as <c>やり過ごすだけ…すだけ</c>.
    /// One column is then enough to replace the quad: the map has shown two.
    /// </remarks>
    [Fact]
    public void A_column_already_boxed_elsewhere_is_left_to_that_box()
    {
        using var page = BusyPage();
        var map = Map((40, 20, 15, 161), (80, 100, 15, 81));
        var merged = Quad(25, 90, 90, 100);
        var own = Quad(30, 10, 36, 150);

        var parts = VerticalColumnDetection.Split(page, [own, merged], map);

        Assert.Equal(2, parts.Count);
        Assert.Contains(own, parts);
        var cut = Assert.Single(parts, p => !ReferenceEquals(p, own));
        Assert.True(Holds(cut, 80, 95) && !Holds(cut, 40, 55));
    }

    [Fact]
    public void Without_a_map_the_quad_on_artwork_is_left_as_it_was()
    {
        using var page = BusyPage();
        var quad = Quad(25, 5, 75, 190);

        var parts = VerticalColumnDetection.Split(page, [quad]);

        Assert.Same(quad, Assert.Single(parts));
    }

    private static SKBitmap BusyPage()
    {
        var bitmap = new SKBitmap(Width, Height);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var level = (byte)((x * 37 + y * 91 + x * y) % 256);
                bitmap.SetPixel(x, y, new SKColor(level, level, level));
            }

        return bitmap;
    }

    private static DetectorProbability Map(params (int X, int Y, int W, int H)[] kernels)
    {
        var values = new float[Width * Height];
        foreach (var (x, y, w, h) in kernels)
            for (var row = y; row < y + h; row++)
                for (var column = x; column < x + w; column++)
                    values[row * Width + column] = 0.9f;

        return new DetectorProbability(values, Width, Height, 1f, 1f, 0.2f, 0.4f, 1.4f);
    }

    private static TextBox Quad(int x, int y, int width, int height) => new()
    {
        Score = 0.9f,
        BoxPoints =
        [
            new(x, y), new(x + width, y), new(x + width, y + height), new(x, y + height),
        ],
    };

    private static bool Holds(TextBox box, int from, int to) =>
        box.BoxPoints.Min(p => p.X) <= from && box.BoxPoints.Max(p => p.X) >= to;
}
