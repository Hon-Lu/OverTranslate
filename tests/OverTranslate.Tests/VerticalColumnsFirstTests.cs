using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The detector's probability map given to the columns before anything on it is framed across.
/// </summary>
/// <remarks>
/// The maps are drawn to the proportions measured on
/// <c>vertical-image-ja3/zang-songnofuriren-001-147hua/2026-09-28 19 08 58 (2).png</c>: characters
/// about 15 map pixels across, the readings between them half as tall, and 12 pixels between the row
/// of heads and the columns it was taken from. What is asserted is what the library would frame: the map thresholded, dilated by
/// a pixel, and taken apart into eight-connected blobs, as its post-processing does.
/// </remarks>
public class VerticalColumnsFirstTests
{
    private const int Width = 200;
    private const int Height = 240;

    [Fact]
    public void Heads_lit_as_one_row_go_back_to_their_columns()
    {
        var map = Map(
            (20, 30, 15, 15), (35, 34, 25, 7), (60, 30, 15, 15), (75, 34, 25, 7), (100, 30, 15, 15),
            (20, 57, 15, 150), (60, 57, 15, 150), (100, 57, 15, 150));

        Assert.True(ColumnsFirst.Carve(map, Width, Height, 0.2f));

        var framed = Framed(map);
        Assert.Equal(3, framed.Count);
        Assert.All(framed, box =>
        {
            Assert.True(box.Top <= 30, $"the column starts at {box.Top}, under its head");
            Assert.True(box.Bottom >= 206);
            Assert.True(box.Right - box.Left + 1 <= 17, "the column is framed alone");
        });
    }

    /// <summary>What lies between two heads in the row is the readings beside them, and goes.</summary>
    [Fact]
    public void The_readings_between_the_heads_are_taken_off_the_map()
    {
        var map = Map(
            (20, 30, 15, 15), (35, 34, 25, 7), (60, 30, 15, 15), (75, 34, 25, 7), (100, 30, 15, 15),
            (20, 57, 15, 150), (60, 57, 15, 150), (100, 57, 15, 150));

        ColumnsFirst.Carve(map, Width, Height, 0.2f);

        for (var y = 30; y < 45; y++)
            for (var x = 38; x < 57; x++)
                Assert.Equal(0f, map[y * Width + x]);
    }

    [Fact]
    public void Tails_lit_as_one_row_go_back_to_their_columns()
    {
        var map = Map(
            (20, 30, 15, 120), (60, 30, 15, 120),
            (20, 162, 15, 15), (35, 166, 25, 7), (60, 162, 15, 15));

        Assert.True(ColumnsFirst.Carve(map, Width, Height, 0.2f));

        var framed = Framed(map);
        Assert.Equal(2, framed.Count);
        Assert.All(framed, box => Assert.True(box.Bottom >= 176, $"the column ends at {box.Bottom}, over its tail"));
    }

    /// <summary>
    /// A row that stands clear of the columns — writing across, a title — is not theirs to take.
    /// </summary>
    [Fact]
    public void A_row_standing_clear_of_the_columns_is_left_as_the_model_wrote_it()
    {
        var map = Map(
            (20, 30, 15, 120), (60, 30, 15, 120),
            (20, 200, 95, 15));
        var before = (float[])map.Clone();

        Assert.False(ColumnsFirst.Carve(map, Width, Height, 0.2f));
        Assert.Equal(before, map);
    }

    [Fact]
    public void A_page_with_no_row_is_left_as_the_model_wrote_it()
    {
        var map = Map((20, 30, 15, 120), (60, 30, 15, 120));
        var before = (float[])map.Clone();

        Assert.False(ColumnsFirst.Carve(map, Width, Height, 0.2f));
        Assert.Equal(before, map);
    }

    /// <summary>
    /// A head whose own column the map shows too broken to be found stays, given to the writing
    /// under it, rather than being cleared with the readings.
    /// </summary>
    /// <remarks>
    /// MEASURED on <c>vertical-manga-web/mit-c.png</c> at the realtime second size: に and を, the
    /// last characters of two columns, were one row, only に's column was found, and clearing the
    /// rest lost を. Here the writing under the second head is two columns the map has joined, too
    /// wide to be taken for one.
    /// </remarks>
    [Fact]
    public void A_head_whose_column_is_not_found_is_given_to_the_writing_under_it()
    {
        var map = Map(
            (20, 30, 15, 15), (35, 34, 25, 7), (60, 30, 35, 15),
            (20, 57, 15, 150),
            (60, 57, 35, 150));

        ColumnsFirst.Carve(map, Width, Height, 0.2f);

        Assert.True(map[37 * Width + 75] > 0.2f, "the head is still on the map");
        var framed = Framed(map);
        Assert.Contains(framed, box => box.Left <= 75 && box.Right >= 75 && box.Top <= 30 && box.Bottom >= 150);
    }

    private static float[] Map(params (int X, int Y, int W, int H)[] kernels)
    {
        var values = new float[Width * Height];
        foreach (var (x, y, w, h) in kernels)
            for (var row = y; row < y + h; row++)
                for (var column = x; column < x + w; column++)
                    values[row * Width + column] = 0.9f;
        return values;
    }

    // What the library frames: over its threshold, dilated by a pixel, eight-connected.
    private static List<(int Left, int Top, int Right, int Bottom)> Framed(float[] map)
    {
        var on = new bool[map.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                if (map[y * Width + x] <= 0.2f) continue;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < Width && ny < Height) on[ny * Width + nx] = true;
                    }
            }

        var seen = new bool[map.Length];
        var boxes = new List<(int, int, int, int)>();
        for (var start = 0; start < on.Length; start++)
        {
            if (!on[start] || seen[start]) continue;
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            var stack = new Stack<int>();
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % Width, y = i / Width;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= Width || ny >= Height) continue;
                        var n = ny * Width + nx;
                        if (!on[n] || seen[n]) continue;
                        seen[n] = true;
                        stack.Push(n);
                    }
            }
            boxes.Add((left, top, right, bottom));
        }
        return boxes;
    }
}
