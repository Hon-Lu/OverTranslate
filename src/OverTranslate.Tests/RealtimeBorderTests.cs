using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeBorderTests
{
    [Fact]
    public void BorderIsOffByDefault_AndRandomOnceTurnedOn()
    {
        var settings = new RealtimeSettings();
        Assert.False(settings.BorderEnabled);
        Assert.Equal(RealtimeBorderColorMode.Random, settings.BorderColorMode);
        Assert.Equal(RealtimeSubtitleColors.DefaultBorder, settings.BorderColor);
    }

    [Fact]
    public void RandomBorder_KeepsOneColourPerLine_AndSpreadsDifferentLines()
    {
        Assert.Equal(
            RealtimeSubtitleColors.RandomBorder("[21:35] Alice: hello"),
            RealtimeSubtitleColors.RandomBorder("[21:35] Alice: hello"));
        var colours = Enumerable.Range(0, 40)
            .Select(i => RealtimeSubtitleColors.RandomBorder($"line {i}")).ToArray();
        Assert.True(colours.Distinct().Count() > 1);
        Assert.All(colours, colour => Assert.Equal(255, colour.A));
    }

    [Fact]
    public void UnreadableFixedBorder_FallsBackToTheAccentBlue() =>
        Assert.Equal(
            RealtimeSubtitleColors.Border(RealtimeSubtitleColors.DefaultBorder),
            RealtimeSubtitleColors.Border("not a colour"));

    [Theory]
    [InlineData(RealtimeBlockMode.Subtitle, "#FF0000")]
    [InlineData(RealtimeBlockMode.Subtitle, null)]
    [InlineData(RealtimeBlockMode.Panel, "#FF0000")]
    [InlineData(RealtimeBlockMode.Panel, null)]
    public void TheBandIsOutlined_WhenTheBorderIsOn(RealtimeBlockMode mode, string? fixedColour) => OnSta(() =>
    {
        var patch = RenderSingleLine(mode, border: true, fixedColour);
        var expected = fixedColour is null
            ? RealtimeSubtitleColors.RandomBorder("source line")
            : RealtimeSubtitleColors.Border(fixedColour);
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(patch.BorderBrush).Color);
        Assert.Equal(new Thickness(RealtimeSubtitleColors.BorderThickness), patch.BorderThickness);
    });

    [Theory]
    [InlineData(RealtimeBlockMode.Subtitle)]
    [InlineData(RealtimeBlockMode.Panel)]
    public void TheBandHasNoBorder_WhenTheBorderIsOff(RealtimeBlockMode mode) => OnSta(() =>
    {
        var patch = RenderSingleLine(mode, border: false, "#FF0000");
        Assert.Null(patch.BorderBrush);
        Assert.Equal(new Thickness(0), patch.BorderThickness);
    });

    private static Border RenderSingleLine(RealtimeBlockMode mode, bool border, string? borderColor)
    {
        var window = new RealtimeBlockWindow(0, new System.Drawing.Rectangle(0, 0, 600, 300),
            _ => null, "EN", "ZH-HANT", "#FFFFFF", "#000000", 70, mode: mode,
            border: border, borderColor: borderColor);
        try
        {
            var type = typeof(RealtimeBlockWindow);
            type.GetField("_lines", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window,
                new List<TranslatedBlock> { new("source line", "譯文", new Rect(20, 20, 300, 30)) });
            type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var backgrounds = (Canvas)window.FindName("ScrimCanvas");
            return Assert.IsType<Border>(Assert.Single(backgrounds.Children));
        }
        finally { window.Close(); }
    }

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
