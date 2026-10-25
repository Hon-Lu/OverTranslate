using OverTranslate.Controls;
using Xunit;
using Color = System.Windows.Media.Color;

namespace OverTranslate.Tests;

/// <summary>
/// The arithmetic and the 返回 list behind the realtime appearance panel's colour picker (#355).
/// </summary>
public class ColorPickerModelTests
{
    [Theory]
    [InlineData("#FF0000")]
    [InlineData("#FF9F0A")]
    [InlineData("#30D158")]
    [InlineData("#0A84FF")]
    [InlineData("#BF5AF2")]
    [InlineData("#52C4FA")]
    [InlineData("#FAFAFA")]
    [InlineData("#808080")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#123456")]
    public void A_colour_survives_the_round_trip(string hex)
    {
        var color = Parse(hex);
        Assert.Equal(color, HsvColor.FromColor(color).ToColor());
    }

    [Fact]
    public void The_corners_of_the_square_are_white_black_and_the_pure_hue()
    {
        Assert.Equal(Parse("#FFFFFF"), new HsvColor(200, 0, 1).ToColor());
        Assert.Equal(Parse("#000000"), new HsvColor(200, 1, 0).ToColor());
        Assert.Equal(Parse("#000000"), new HsvColor(200, 0, 0).ToColor());
        Assert.Equal(Parse("#0000FF"), new HsvColor(240, 1, 1).ToColor());
    }

    [Theory]
    [InlineData(0, "#FF0000")]
    [InlineData(60, "#FFFF00")]
    [InlineData(120, "#00FF00")]
    [InlineData(180, "#00FFFF")]
    [InlineData(240, "#0000FF")]
    [InlineData(300, "#FF00FF")]
    [InlineData(360, "#FF0000")]
    [InlineData(-60, "#FF00FF")]
    public void The_strip_wraps_through_the_six_primaries(double hue, string expected) =>
        Assert.Equal(Parse(expected), new HsvColor(hue, 0.5, 0.5).PureHue());

    [Fact]
    public void A_grey_keeps_the_hue_it_was_given()
    {
        // The square dragged to its white edge: the strip must not jump back to red.
        Assert.Equal(210, HsvColor.FromColor(Parse("#FFFFFF"), hue: 210).Hue);
        Assert.Equal(210, HsvColor.FromColor(Parse("#000000"), hue: 210).Hue);

        // A colour with a hue of its own brings it.
        Assert.Equal(0, HsvColor.FromColor(Parse("#FF0000"), hue: 210).Hue);
    }

    [Fact]
    public void Out_of_range_values_are_held_inside_it()
    {
        Assert.Equal(Parse("#FFFFFF"), new HsvColor(0, -1, 2).ToColor());
        Assert.Equal(Parse("#FF0000"), new HsvColor(0, 3, 1).ToColor());
    }

    [Fact]
    public void Undo_walks_back_through_every_colour_applied()
    {
        var history = new ColorPickHistory();
        var white = Parse("#FFFFFF");
        var red = Parse("#FF0000");
        var blue = Parse("#0000FF");

        Assert.False(history.CanUndo);
        history.Record(white, red);
        history.Record(red, blue);

        Assert.True(history.TryUndo(out var first));
        Assert.Equal(red, first);
        Assert.True(history.TryUndo(out var second));
        Assert.Equal(white, second);
        Assert.False(history.CanUndo);
        Assert.False(history.TryUndo(out _));
    }

    [Fact]
    public void The_same_colour_applied_again_is_no_step()
    {
        var history = new ColorPickHistory();
        var red = Parse("#FF0000");

        history.Record(red, red);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Clearing_forgets_everything()
    {
        var history = new ColorPickHistory();
        history.Record(Parse("#FFFFFF"), Parse("#FF0000"));
        history.Clear();
        Assert.False(history.CanUndo);
    }

    [Theory]
    [InlineData("#52C4FA", "#52C4FA")]
    [InlineData("52c4fa", "#52C4FA")]
    [InlineData(" #000000 ", "#000000")]
    public void A_whole_code_is_read_with_or_without_its_hash(string typed, string expected)
    {
        Assert.True(ColorPicker.TryParseHex(typed, out var color));
        Assert.Equal(expected, ColorPicker.Format(color));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#")]
    [InlineData("#FFF")]
    [InlineData("#52C4F")]
    [InlineData("#52C4FAFF")]
    [InlineData("#GGGGGG")]
    [InlineData("##52C4F")]
    public void Anything_short_of_a_whole_code_is_not_a_colour(string? typed) =>
        Assert.False(ColorPicker.TryParseHex(typed, out _));

    [Fact]
    public void The_swatches_are_one_row_of_six_hues_then_white_and_black()
    {
        Assert.Equal(8, ColorPicker.Presets.Count);
        Assert.Equal(Parse("#FFFFFF"), ColorPicker.Presets[6]);
        Assert.Equal(Parse("#000000"), ColorPicker.Presets[7]);

        // The hues in the strip's order, so the row reads as the strip does.
        var hues = ColorPicker.Presets.Take(6).Select(c => HsvColor.FromColor(c).Hue).ToList();
        Assert.Equal(hues.OrderBy(h => h), hues);
    }

    private static Color Parse(string hex) =>
        (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
}
