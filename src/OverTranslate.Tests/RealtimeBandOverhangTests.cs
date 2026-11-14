using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;
using DrawingColor = System.Drawing.Color;

namespace OverTranslate.Tests;

/// <summary>
/// The band — 背景固定 — over a line drawn with a thick edge. The band is sized from the box the line
/// was read in, the box is drawn round the letters, and the edge lies outside it: so the edge showed
/// as a dark rim round the band. Twelve pixels on a 40-pixel glyph, so that it reaches past the
/// band the translation alone would have drawn.
/// </summary>
public class RealtimeBandOverhangTests
{
    private const int Left = 60, Top = 60, Stroke = 8, Gap = 12, Height = 40, Strokes = 12, Edge = 12;
    private static readonly System.Drawing.Rectangle Block = new(0, 0, 400, 180);
    private static int Right => Left + (Strokes - 1) * (Stroke + Gap) + Stroke;

    // Tight to the white above and below, and holding the edge at the line's ends, as the detector's
    // boxes do on drawn edged text — see RealtimeCpuBackgroundTests.Repair_ErasesAThickEdgeThatLiesOutsideTheBox.
    private static readonly Rect Box = new(Left - Edge, Top, Right - Left + Edge * 2, Height);

    [Fact]
    public void The_band_grows_to_cover_an_edge_outside_the_box_and_the_translation_stays_put()
    {
        var (edgedBand, edgedText) = OnStaThread(() => Draw(() => Picture(edged: true)));
        var (boxBand, boxText) = OnStaThread(() => Draw(() => null));

        var edge = new Rect(Left - Edge, Top - Edge, Right - Left + Edge * 2, Height + Edge * 2);
        Assert.True(edgedBand.Contains(edge), $"the band {edgedBand} leaves the edge {edge} showing");
        Assert.True(!boxBand.Contains(edge), $"the band from the box alone, {boxBand}, already covers {edge}: the case is not tested");
        Assert.Equal(boxText, edgedText);
    }

    [Fact]
    public void A_line_without_an_edge_keeps_the_band_the_box_gives_it()
    {
        var (plainBand, plainText) = OnStaThread(() => Draw(() => Picture(edged: false)));
        var (boxBand, boxText) = OnStaThread(() => Draw(() => null));

        Assert.Equal(boxBand, plainBand);
        Assert.Equal(boxText, plainText);
    }

    private static System.Drawing.Bitmap Picture(bool edged)
    {
        var frame = new System.Drawing.Bitmap(Block.Width, Block.Height);
        using var graphics = System.Drawing.Graphics.FromImage(frame);
        graphics.Clear(DrawingColor.FromArgb(200, 190, 170));
        for (int index = 0; index < Strokes; index++)
        {
            int x = Left + index * (Stroke + Gap);
            if (edged)
                graphics.FillRectangle(System.Drawing.Brushes.Black, x - Edge, Top - Edge, Stroke + Edge * 2, Height + Edge * 2);
        }
        for (int index = 0; index < Strokes; index++)
            graphics.FillRectangle(System.Drawing.Brushes.White, Left + index * (Stroke + Gap), Top, Stroke, Height);
        return frame;
    }

    // The background's and the translation's rectangles, in the window's own units scaled back to the
    // frame's pixels.
    private static (Rect Band, Rect Text) Draw(Func<System.Drawing.Bitmap?> picture)
    {
        var window = new RealtimeBlockWindow(
            0, Block, _ => picture(), "JA", "ZH-TW",
            RealtimeSubtitleColors.DefaultText,
            RealtimeSubtitleColors.DefaultScrim,
            RealtimeSubtitleColors.DefaultScrimOpacity);
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(RealtimeBlockWindow).GetField("_isLoaded", flags)!.SetValue(window, true);
            double dpiX = (double)typeof(RealtimeBlockWindow).GetField("_dpiX", flags)!.GetValue(window)!;
            double dpiY = (double)typeof(RealtimeBlockWindow).GetField("_dpiY", flags)!.GetValue(window)!;

            window.SetLines([new TranslatedBlock("III", "譯文", Box, null, Height)]);

            var band = Assert.IsType<Border>(Assert.Single(((Canvas)window.FindName("ScrimCanvas")).Children));
            var text = Assert.IsType<Border>(Assert.Single(((Canvas)window.FindName("TextCanvas")).Children));
            Rect Physical(Border element) => new(
                Canvas.GetLeft(element) * dpiX, Canvas.GetTop(element) * dpiY, element.Width * dpiX, element.Height * dpiY);
            return (Physical(band), Physical(text));
        }
        finally
        {
            window.Close();
        }
    }

    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
