using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Wrapped translations are measured the way the TextBlock that draws them breaks them.
/// </summary>
/// <remarks>
/// <para>The box a wrapped translation gets used to come from FormattedText, which keeps an
/// unbreakable run wider than the line on one line where the TextBlock breaks it — see
/// <see cref="WrappedTextMeasure"/>. The box was a line short, and it clips.</para>
///
/// <para>These read back the drawn TextBlock after layout rather than measuring again: a check made
/// with the same ruler as the code under test is what <see cref="OverlayTextFittingTests"/> does,
/// and it is why that file never caught this.</para>
/// </remarks>
public class WrappedTextMeasureTests
{
    // The en-4 balloon as the realtime session reads it on a 125% display: three source lines in
    // one group, and Youdao's translation, whose ”嗯…… cannot be broken and is wider than the line.
    private static readonly TranslatedBlock Youdao = new(
        "\"YF YOU KNOW,\" HUH...",
        "“如果你知道的話，”嗯……",
        new Rect(167, 717, 220, 198),
        [new Rect(175, 717, 212, 68), new Rect(167, 779, 220, 72), new Rect(176, 841, 184, 74)],
        RenderGlyphHeight: 45.93) { FontGlyphHeight = 45.93 };

    private static readonly System.Drawing.Rectangle ComicPage = new(0, 0, 816, 1297);

    [Fact]
    public void A_grouped_translation_with_an_unbreakable_run_is_drawn_whole()
    {
        var drawn = Draw(Youdao, ComicPage, dpi: 1.25);

        Assert.True(drawn.TextHeight <= drawn.BoxHeight + 0.5,
            $"drawn text is {drawn.TextHeight:0.##}px tall in a {drawn.BoxHeight:0.##}px box");

        // The size the source asks for is 38.95px, and at that size the TextBlock needs four lines
        // where the group has room for three. Only a smaller size keeps it whole.
        Assert.True(drawn.FontSize < 38.9, $"font {drawn.FontSize:0.##}");
    }

    /// <summary>
    /// Punctuation and Latin runs that glue into pieces wider than the line, each at a geometry the
    /// synthetic sweep found it clipped at before the fix: Latin-sized line boxes, half again as
    /// tall as their glyphs, with a fifth of a glyph between them.
    /// </summary>
    [Theory]
    [InlineData("OK，“Yes”—— 就這樣。", 3, 46, 83)]
    [InlineData("真的嗎？！？！不會吧！！！", 3, 46, 106)]
    [InlineData("「……」", 2, 34, 60)]
    [InlineData("這把Excalibur的攻擊力是+250，暴擊率12.5%。", 4, 34, 83)]
    [InlineData("等等——你剛剛說什麼？！", 4, 34, 60)]
    public void A_grouped_translation_is_drawn_inside_its_box(string translation, int lineCount, double glyph, double width)
    {
        var lines = Enumerable.Range(0, lineCount)
            .Select(i => new Rect(100, 100 + i * glyph * 1.7, width, glyph * 1.5))
            .ToList();
        var group = new TranslatedBlock(
            "SOURCE", translation, lines.Aggregate(Rect.Union), lines, RenderGlyphHeight: glyph);

        var drawn = Draw(group, new System.Drawing.Rectangle(0, 0, 1400, 1000), dpi: 1.0);

        Assert.True(drawn.TextHeight <= drawn.BoxHeight + 0.5,
            $"drawn text is {drawn.TextHeight:0.##}px tall in a {drawn.BoxHeight:0.##}px box");
    }

    /// <summary>
    /// One source line in a block hardly wider than it, so the translation is forced to wrap and
    /// the band may grow to the block's height. A word with no break in it is split by the
    /// TextBlock character by character; FormattedText counted it as one line.
    /// </summary>
    [Fact]
    public void A_single_line_forced_to_wrap_is_drawn_inside_its_band()
    {
        var line = new TranslatedBlock("SOURCE", "Supercalifragilisticexpialidocious",
            new Rect(10, 200, 60, 36), RenderGlyphHeight: 24);

        var drawn = Draw(line, new System.Drawing.Rectangle(0, 0, 80, 1000), dpi: 1.25);

        Assert.True(drawn.Wrapped);
        Assert.True(drawn.TextHeight <= drawn.BoxHeight + 0.5,
            $"drawn text is {drawn.TextHeight:0.##}px tall in a {drawn.BoxHeight:0.##}px box");
    }

    /// <summary>
    /// The measurement against a TextBlock laid out in a padded box, the way both overlays draw
    /// one — and against FormattedText, to keep the reason for it on record.
    /// </summary>
    [Fact]
    public void The_measurement_matches_what_a_TextBlock_draws_where_FormattedText_does_not()
    {
        OnStaThread(() =>
        {
            var font = TranslatedTextFont.For("ZH-HANT");
            var typeface = new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            const double size = 38.95, width = 176;

            var drawn = new TextBlock
            {
                Text = Youdao.TranslatedText, FontFamily = font, FontSize = size, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
            var box = new Border { Width = width + 10, Padding = new Thickness(5, 3, 5, 3), Child = drawn };
            box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            box.Arrange(new Rect(box.DesiredSize));

            var measured = new WrappedTextMeasure().Measure(Youdao.TranslatedText, typeface, size, width);
            var formatted = new FormattedText(Youdao.TranslatedText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, size, Brushes.Black, 1.0) { MaxTextWidth = width };

            Assert.Equal(drawn.ActualHeight, measured.Height, 1);
            Assert.True(formatted.Height < drawn.ActualHeight - size,
                $"FormattedText {formatted.Height:0.##}px against {drawn.ActualHeight:0.##}px drawn");
            return 0;
        });
    }

    private readonly record struct DrawnText(bool Wrapped, double FontSize, double TextHeight, double BoxHeight);

    private static DrawnText Draw(TranslatedBlock line, System.Drawing.Rectangle block, double dpi) =>
        OnStaThread(() =>
        {
            var window = new RealtimeBlockWindow(
                0, block, _ => null, "EN", "ZH-HANT",
                RealtimeSubtitleColors.DefaultText,
                RealtimeSubtitleColors.DefaultScrim,
                RealtimeSubtitleColors.DefaultScrimOpacity);
            try
            {
                // Never shown, so nothing reads a DPI or raises Loaded: both are set as Loaded would.
                var type = typeof(RealtimeBlockWindow);
                type.GetField("_dpiX", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, dpi);
                type.GetField("_dpiY", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, dpi);
                type.GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);

                window.SetLines([line]);

                var canvas = (Canvas)window.FindName("TextCanvas");
                canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                canvas.Arrange(new Rect(0, 0, block.Width / dpi, block.Height / dpi));

                var container = Assert.IsType<Border>(Assert.Single(canvas.Children));
                var text = Assert.IsType<TextBlock>(container.Child);
                return new DrawnText(
                    text.TextWrapping == TextWrapping.Wrap,
                    text.FontSize,
                    text.ActualHeight,
                    container.Height - container.Padding.Top - container.Padding.Bottom);
            }
            finally { window.Close(); }
        });

    /// <summary>A fresh STA thread, which WPF elements require and xunit's own worker is not.</summary>
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
