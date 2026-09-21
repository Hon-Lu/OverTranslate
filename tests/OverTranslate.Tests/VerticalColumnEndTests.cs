using OverTranslate.Services.Ocr;
using SkiaSharp;
using Xunit;
using TextBox = RapidOcrNet.TextBox;

namespace OverTranslate.Tests;

/// <summary>
/// The crop that reaches for the character the detector's box stopped short of.
/// </summary>
/// <remarks>
/// On pixels, because the whole rule is "is there still ink immediately outside this edge" and a
/// test written on rectangles would be testing the arithmetic rather than the question.
/// </remarks>
public class VerticalColumnEndTests
{
    private const int Left = 60;
    private const int Width = 26;
    private const int FirstMark = 40;

    [Fact]
    public void A_box_that_cut_the_top_character_reaches_back_for_it()
    {
        using var page = Page();
        // Starts halfway down the first mark, which is what the detector does.
        var extended = VerticalColumnEnds.Extend(page, Box(44, 240));

        Assert.Equal(FirstMark, Top(extended));
    }

    [Fact]
    public void A_box_that_already_holds_its_column_does_not_move()
    {
        using var page = Page();
        var box = Box(FirstMark, 240);

        var extended = VerticalColumnEnds.Extend(page, box);

        Assert.Equal(FirstMark, Top(extended));
        Assert.Equal(240, Bottom(extended));
    }

    /// <summary>
    /// A rule running through where the column ends is not more of the column.
    /// </summary>
    /// <remarks>
    /// The border of a narration box, a balloon's outline, the edge of a panel: each offers an
    /// unbroken row of ink for the walk to spend its whole budget on. What makes it a rule is that
    /// the ink runs THROUGH — unbroken from outside one edge to outside the other.
    /// </remarks>
    [Fact]
    public void A_rule_crossing_the_column_stops_the_reach()
    {
        using var page = Page(ruleAt: 36);
        var extended = VerticalColumnEnds.Extend(page, Box(40, 240));

        Assert.Equal(40, Top(extended));
    }

    /// <summary>
    /// A neighbour beside the column is not a rule, however much ink it has.
    /// </summary>
    /// <remarks>
    /// The case that decided the shape of this test. A column of vertical Japanese has the next
    /// column of the same balloon on one side and its own reading on the other, so asking merely
    /// whether there is ink out there refuses to extend almost every column on the page. MEASURED
    /// on 私の知らない人…: at the row holding the top of 私 there are 12 ink pixels to its left and
    /// 7 to its right, and the 私 was left off the page for it.
    /// </remarks>
    [Fact]
    public void Ink_beside_the_column_with_a_gutter_between_is_not_a_rule()
    {
        using var page = Page(neighbours: true);
        var extended = VerticalColumnEnds.Extend(page, Box(44, 240));

        Assert.Equal(FirstMark, Top(extended));
    }

    /// <summary>Over artwork every row has ink, so the question cannot be asked.</summary>
    [Fact]
    public void A_box_over_artwork_is_left_alone()
    {
        using var page = Page(noisy: true);
        var extended = VerticalColumnEnds.Extend(page, Box(44, 240));

        Assert.Equal(44, Top(extended));
    }

    private static SKBitmap Page(int? ruleAt = null, bool noisy = false, bool neighbours = false)
    {
        var bitmap = new SKBitmap(200, 300);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var ink = new SKPaint { Color = SKColors.Black };
        if (noisy)
        {
            var random = new Random(7);
            for (var y = 0; y < 300; y++)
            for (var x = 0; x < 200; x++)
                if (random.Next(2) == 0)
                    canvas.DrawRect(x, y, 1, 1, ink);
        }

        // Ten characters down the column, 8 tall on a 20 pitch — a page is mostly background,
        // and the flat-background pre-filter is part of what is under test.
        for (var i = 0; i < 10; i++)
            canvas.DrawRect(Left, FirstMark + i * 20, Width, 8, ink);
        if (neighbours)
            for (var i = 0; i < 10; i++)
            {
                // The next column of the balloon, and the reading down the other side. Both run the
                // whole length of the column, and neither touches it.
                canvas.DrawRect(Left - 20, FirstMark + i * 20, 14, 8, ink);
                canvas.DrawRect(Left + Width + 6, FirstMark + i * 20, 8, 8, ink);
            }
        if (ruleAt is { } y2) canvas.DrawRect(0, y2, 200, 4, ink);
        return bitmap;
    }

    private static TextBox Box(int top, int bottom) => new()
    {
        Score = 0.9f,
        BoxPoints =
        [
            new(Left, top), new(Left + Width, top),
            new(Left + Width, bottom), new(Left, bottom),
        ],
    };

    private static int Top(TextBox box) => box.BoxPoints.Min(p => p.Y);

    private static int Bottom(TextBox box) => box.BoxPoints.Max(p => p.Y);
}
