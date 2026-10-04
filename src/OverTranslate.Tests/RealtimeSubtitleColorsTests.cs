using OverTranslate.Services.Realtime;
using Xunit;
using Color = System.Windows.Media.Color;

namespace OverTranslate.Tests;

/// <summary>
/// The settings file is hand-editable, so every one of these is a value that can really arrive.
/// </summary>
public class RealtimeSubtitleColorsTests
{
    /// <summary>Any opacity will do where the test is about the colour rather than the band.</summary>
    private const int Opacity = RealtimeSubtitleColors.DefaultScrimOpacity;

    [Theory]
    [InlineData("#FF8800", 0xFF, 0x88, 0x00)]
    [InlineData("#ff8800", 0xFF, 0x88, 0x00)]   // lower case
    [InlineData("ff8800", 0xFF, 0x88, 0x00)]    // no leading hash
    [InlineData("  #FF8800  ", 0xFF, 0x88, 0x00)]
    public void Text_reads_a_six_digit_colour(string hex, byte r, byte g, byte b)
    {
        var color = RealtimeSubtitleColors.Text(hex);

        Assert.Equal(Color.FromRgb(r, g, b), color);
    }

    [Fact]
    public void Text_is_always_opaque()
    {
        Assert.Equal(0xFF, RealtimeSubtitleColors.Text("#123456").A);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#FFF")]            // three-digit shorthand is not accepted
    [InlineData("#GGGGGG")]         // not hex
    [InlineData("rgb(1,2,3)")]
    [InlineData("#FF88000000")]     // too long for even an ARGB value
    public void An_unreadable_value_falls_back_to_the_default(string? hex)
    {
        // The point is that a typo costs the user their colour, not their subtitles.
        Assert.Equal(RealtimeSubtitleColors.Text(RealtimeSubtitleColors.DefaultText),
                     RealtimeSubtitleColors.Text(hex));
        Assert.Equal(
            RealtimeSubtitleColors.Scrim(RealtimeSubtitleColors.DefaultScrim, Opacity),
            RealtimeSubtitleColors.Scrim(hex, Opacity));
    }

    [Fact]
    public void An_eight_digit_value_keeps_its_rgb_and_loses_its_alpha()
    {
        var color = RealtimeSubtitleColors.Text("#40FF8800");

        Assert.Equal(Color.FromRgb(0xFF, 0x88, 0x00), color);
    }

    [Fact]
    public void Scrim_carries_the_opacity_it_is_given_whatever_the_colour_is()
    {
        foreach (var hex in new[] { "#000000", "#FFFFFF", "#1E3A5F", "not a colour" })
            Assert.Equal(
                RealtimeSubtitleColors.ScrimAlpha(40),
                RealtimeSubtitleColors.Scrim(hex, 40).A);
    }

    [Fact]
    public void The_default_opacity_is_the_alpha_the_band_was_fixed_at()
    {
        // 0xB8 is what every session drew before this was offered, so a settings file written by an
        // older build — which has no opacity key at all — has to come back looking the same.
        Assert.Equal(
            0xB8,
            RealtimeSubtitleColors.ScrimAlpha(RealtimeSubtitleColors.DefaultScrimOpacity));
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(100, 0xFF)]
    [InlineData(-40, 0x00)]     // hand-edited out of range, below
    [InlineData(150, 0xFF)]     // and above
    public void Opacity_spans_the_whole_alpha_range_and_clamps_outside_it(int opacity, byte alpha)
    {
        // Clamped rather than refused for the same reason an unreadable colour falls back: a typo in
        // a file the user is invited to edit should cost them the value, not the session.
        Assert.Equal(alpha, RealtimeSubtitleColors.ScrimAlpha(opacity));
    }

    [Fact]
    public void Format_round_trips_through_Text()
    {
        var original = Color.FromRgb(0x1E, 0x90, 0xD5);

        var formatted = RealtimeSubtitleColors.Format(original);

        Assert.Equal("#1E90D5", formatted);
        Assert.Equal(original, RealtimeSubtitleColors.Text(formatted));
    }

    [Fact]
    public void Format_ignores_alpha_so_a_scrim_colour_survives_a_save_and_reload()
    {
        // Round-tripping the scrim is how the picker writes back what it was shown; if Format kept
        // the alpha, each save would add another set of digits for Parse to strip.
        var stored = RealtimeSubtitleColors.Format(RealtimeSubtitleColors.Scrim("#1E3A5F", Opacity));

        Assert.Equal("#1E3A5F", stored);
        Assert.Equal(
            RealtimeSubtitleColors.Scrim("#1E3A5F", Opacity),
            RealtimeSubtitleColors.Scrim(stored, Opacity));
    }
}
