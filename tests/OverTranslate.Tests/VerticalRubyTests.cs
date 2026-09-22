using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// Ruby printed over a line is not a line of its own, and must not be drawn as a second bubble.
/// </summary>
/// <remarks>
/// The figures are real pairs off the 24 comic pages. What separates a reading from a piece of the
/// sentence grouping left behind is the SIZE: readings run 0.22 to 0.58 of the body, fragments 0.65
/// and up. Both sit right on top of the text they belong to, so position alone says nothing.
/// </remarks>
public class VerticalRubyTests
{
    private static OcrTextBlock Block(string text, Rect bounds, double glyph, bool across = false) =>
        new(text, bounds, RenderGlyphHeight: glyph) { RunsAcross = across, LayoutBounds = bounds };

    [Fact]
    public void A_reading_sitting_on_its_text_is_dropped()
    {
        // 俺たちは支援魔術を扱う付与術士が, with まじゅっ over 魔術.
        var body = Block("俺たちは支援魔術を扱う付与術士が", new Rect(459, 131, 134, 199), 28.3);
        var ruby = Block("まじゅっ", new Rect(526, 198, 19, 47), 11.8);

        var kept = VerticalColumnGrouping.WithoutRuby([body, ruby]);

        Assert.Equal(["俺たちは支援魔術を扱う付与術士が"], kept.Select(block => block.Text));
    }

    /// <summary>
    /// A piece of the sentence that grouping failed to join is set at the sentence's own size, and
    /// is kept — losing it would lose text, which is the opposite of the complaint.
    /// </summary>
    [Theory]
    [InlineData("べて", 32, 31.5)]
    [InlineData("間だろ", 30.3, 46.5)]
    [InlineData("に礼をしてほしくて", 24.7, 34)]
    public void A_fragment_set_at_the_bodys_own_size_is_kept(string text, double glyph, double bodyGlyph)
    {
        var body = Block("ておととうぜん劣るのは当然だろ", new Rect(1487, 118, 130, 252), bodyGlyph);
        var fragment = Block(text, new Rect(1546, 162, 35, 64), glyph);

        var kept = VerticalColumnGrouping.WithoutRuby([body, fragment]);

        Assert.Equal(2, kept.Count);
    }

    /// <summary>
    /// A name plate sets its title smaller than the name and the two boxes overlap, so on size and
    /// position alone the title reads exactly like ruby. What tells them apart is that ruby is set
    /// in columns.
    /// </summary>
    [Fact]
    public void A_smaller_title_line_of_a_name_plate_is_kept()
    {
        var name = Block("オリヴァー・カーディフ", new Rect(1201, 490, 268, 51), 35.2, across: true);
        var title = Block("剣聖", new Rect(1311, 469, 52, 36), 18, across: true);

        var kept = VerticalColumnGrouping.WithoutRuby([name, title]);

        Assert.Equal(2, kept.Count);
    }

    /// <summary>The body it sits on may run either way — a sign is still a word with a reading.</summary>
    [Fact]
    public void A_reading_over_a_horizontal_sign_is_dropped()
    {
        var sign = Block("迷宮入り口", new Rect(1611, 89, 128, 37), 30.8, across: true);
        var ruby = Block("ぐち", new Rect(1716, 86, 18, 13), 6.5);

        var kept = VerticalColumnGrouping.WithoutRuby([sign, ruby]);

        Assert.Equal(["迷宮入り口"], kept.Select(block => block.Text));
    }

    [Fact]
    public void Two_balloons_that_merely_sit_near_each_other_are_both_kept()
    {
        var one = Block("かの魔獣が", new Rect(336, 105, 55, 110), 22);
        var other = Block("近づいてるみたいだ", new Rect(308, 89, 40, 205), 30.2);

        Assert.Equal(2, VerticalColumnGrouping.WithoutRuby([one, other]).Count);
    }

    [Fact]
    public void A_reading_that_barely_touches_its_text_is_kept()
    {
        var body = Block("到着です！", new Rect(1116, 150, 53, 212), 42.4);
        // Mostly outside the body's box: below the bar this rule is willing to guess at.
        var ruby = Block("ちゃく", new Rect(1160, 186, 22, 51), 17);

        Assert.Equal(2, VerticalColumnGrouping.WithoutRuby([body, ruby]).Count);
    }
}
