using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// A block window restyled in place by the running bar's 顯示外觀 panel (#355) draws what a window
/// built with the same look would — the panel no longer builds new windows, which is what made the
/// band flash up.
/// </summary>
public class RealtimeBlockAppearanceTests
{
    private static readonly RealtimeBlockAppearance Changed = new(
        TextColor: "#FFD60A", ScrimColor: "#0A84FF", ScrimOpacity: 50,
        NaturalBackground: false, SampleTextColor: false,
        Border: true, BorderColor: "#FF453A", FixedFontSize: 30);

    [Fact]
    public void A_copy_beside_its_block_gets_the_band_whatever_the_settings_say()
    {
        var request = new RealtimeStartRequest(
            new System.Drawing.Rectangle(0, 0, 1920, 1080), "JA", "ZH-HANT", TranslationProvider.Microsoft,
            "#FAFAFA", "#000000", 72, NaturalBackground: true, SampleSourceTextColor: true);

        var over = RealtimeBlockAppearance.For(request, compared: false);
        var beside = RealtimeBlockAppearance.For(request, compared: true);

        Assert.True(over.NaturalBackground);
        Assert.True(over.SampleTextColor);
        Assert.False(beside.NaturalBackground);
        Assert.False(beside.SampleTextColor);
        Assert.Equal(over with { NaturalBackground = false, SampleTextColor = false }, beside);
    }

    [Fact]
    public void Restyled_in_place_it_draws_what_a_new_window_would() => OnSta(() =>
    {
        var restyled = Build(RealtimeBlockAppearance.For(Plain(), compared: false));
        var fresh = Build(Changed);
        try
        {
            restyled.ApplyAppearance(Changed);

            var (restyledBand, restyledText) = Render(restyled);
            var (freshBand, freshText) = Render(fresh);

            Assert.Equal(Colour(freshBand.Background), Colour(restyledBand.Background));
            Assert.Equal(Colour(freshBand.BorderBrush), Colour(restyledBand.BorderBrush));
            Assert.Equal(freshBand.BorderThickness, restyledBand.BorderThickness);
            Assert.Equal(freshText.FontSize, restyledText.FontSize);
            Assert.Equal(Colour(freshText.Foreground), Colour(restyledText.Foreground));

            Assert.Equal(RealtimeSubtitleColors.Scrim("#0A84FF", 50), Colour(restyledBand.Background));
            Assert.Equal(RealtimeSubtitleColors.Border("#FF453A"), Colour(restyledBand.BorderBrush));
        }
        finally
        {
            restyled.Close();
            fresh.Close();
        }
    });

    [Fact]
    public void Turning_the_border_off_in_place_takes_it_away() => OnSta(() =>
    {
        var window = Build(Changed);
        try
        {
            window.ApplyAppearance(Changed with { Border = false });
            var (band, _) = Render(window);
            Assert.Null(band.BorderBrush);
            Assert.Equal(new System.Windows.Thickness(0), band.BorderThickness);
        }
        finally { window.Close(); }
    });

    private static RealtimeStartRequest Plain() => new(
        new System.Drawing.Rectangle(0, 0, 1920, 1080), "EN", "ZH-HANT", TranslationProvider.Microsoft,
        "#FFFFFF", "#000000", 70, NaturalBackground: false, SampleSourceTextColor: false);

    private static RealtimeBlockWindow Build(RealtimeBlockAppearance look) =>
        new(0, new System.Drawing.Rectangle(0, 0, 600, 300), _ => null, "EN", "ZH-HANT",
            look.TextColor, look.ScrimColor, look.ScrimOpacity, look.NaturalBackground, look.SampleTextColor,
            RealtimeBlockMode.Subtitle, look.Border, look.BorderColor, fixedFontSize: look.FixedFontSize);

    /// <summary>One line drawn as Rebuild draws it: its band, and the text set on it.</summary>
    private static (Border Band, TextBlock Text) Render(RealtimeBlockWindow window)
    {
        var type = typeof(RealtimeBlockWindow);
        type.GetField("_lines", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window,
            new List<TranslatedBlock> { new("source line", "譯文", new Rect(20, 20, 300, 30)) });
        type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);

        var band = Assert.IsType<Border>(Assert.Single(((Canvas)window.FindName("ScrimCanvas")).Children));
        var holder = Assert.Single(((Canvas)window.FindName("TextCanvas")).Children);
        return (band, FindText(holder)!);
    }

    private static TextBlock? FindText(object element) => element switch
    {
        TextBlock text => text,
        Border { Child: { } child } => FindText(child),
        Panel panel => panel.Children.Cast<object>().Select(FindText).FirstOrDefault(found => found is not null),
        Viewbox { Child: { } child } => FindText(child),
        _ => null,
    };

    private static Color? Colour(Brush? brush) => (brush as SolidColorBrush)?.Color;

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
