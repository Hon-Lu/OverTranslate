using OverTranslate.Models;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// 顯示外觀 read into a session's request, put back to its defaults, and asked whether it is at them —
/// shared by the page and the running bar's panel (#355).
/// </summary>
public class RealtimeAppearanceTests
{
    private static RealtimeSettings Changed()
    {
        var realtime = new RealtimeSettings
        {
            TextColor = "#FFD60A",
            SampleSourceTextColor = true,
            ScrimColor = "#0A84FF",
            ScrimOpacity = 40,
            NaturalBackgroundEnabled = true,
            BorderEnabled = true,
            BorderColorMode = RealtimeBorderColorMode.Fixed,
            BorderColor = "#FF453A",
            FontSizeMode = RealtimeFontSizeMode.Fixed,
            FixedFontSize = 30,
        };
        return realtime;
    }

    private static RealtimeStartRequest Request(AppSettings settings) =>
        RealtimeQuickStart.Build(
            settings,
            new System.Drawing.Rectangle(0, 0, 1920, 1080),
            "JA",
            "ZH-HANT",
            TranslationProvider.Microsoft,
            RealtimeCaptureMode.Screen,
            IntPtr.Zero);

    [Fact]
    public void A_running_session_changed_from_the_panel_draws_what_a_fresh_start_would()
    {
        var before = Request(new AppSettings());

        var settings = new AppSettings { Realtime = Changed() };
        var changed = RealtimeQuickStart.WithAppearance(before, settings.Realtime);

        Assert.Equal(Request(settings), changed);
    }

    [Fact]
    public void Changing_the_look_leaves_everything_else_about_the_sitting_alone()
    {
        var before = Request(new AppSettings());
        var changed = RealtimeQuickStart.WithAppearance(before, Changed());

        Assert.Equal(before.ScreenBounds, changed.ScreenBounds);
        Assert.Equal(before.SourceLanguage, changed.SourceLanguage);
        Assert.Equal(before.TargetLanguage, changed.TargetLanguage);
        Assert.Equal(before.Provider, changed.Provider);
        Assert.Equal(before.CaptureMode, changed.CaptureMode);
        Assert.Equal(before.SourceWindow, changed.SourceWindow);
    }

    [Fact]
    public void Automatic_size_and_random_border_reach_the_session_as_no_value()
    {
        var realtime = Changed();
        realtime.FontSizeMode = RealtimeFontSizeMode.Auto;
        realtime.BorderColorMode = RealtimeBorderColorMode.Random;

        var request = RealtimeQuickStart.WithAppearance(Request(new AppSettings()), realtime);

        Assert.Null(request.FixedFontSize);
        Assert.Null(request.FixedBorderColor);
    }

    [Fact]
    public void Reset_puts_every_value_back_and_nothing_else()
    {
        var realtime = Changed();
        realtime.SourceLanguage = "KO";
        realtime.CompareDisplay = true;

        Assert.False(realtime.HasDefaultAppearance());
        realtime.ResetAppearance();

        Assert.True(realtime.HasDefaultAppearance());
        Assert.Equal("KO", realtime.SourceLanguage);
        Assert.True(realtime.CompareDisplay);
    }

    [Fact]
    public void A_value_still_being_dragged_counts_as_shown()
    {
        var realtime = new RealtimeSettings();

        Assert.True(realtime.HasDefaultAppearance());
        Assert.False(realtime.HasDefaultAppearance(shownOpacity: realtime.ScrimOpacity + 5));
        Assert.False(realtime.HasDefaultAppearance(shownFixedFontSize: realtime.FixedFontSize + 1));
    }
}
