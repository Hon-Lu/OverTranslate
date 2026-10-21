using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Layout;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;
using Size = System.Windows.Size;

namespace OverTranslate.Tests;

/// <summary>
/// 顯示外觀 → 文字大小 → 固定 (#316): the size is the reader's, a sentence too long for it wraps,
/// and only one too long for the block shrinks.
/// </summary>
/// <remarks>
/// The ladder itself is checked against a made-up measure — every glyph one em wide, every line
/// 1.22 em tall — so each rung can be reached on purpose. The window is built for real only where
/// what is in question is how it uses the answer: what covers the source, and what a panel does.
/// </remarks>
public class RealtimeFixedFontSizeTests
{
    private const double MinFontSize = 8.5;

    [Fact]
    public void A_short_line_is_set_at_the_fixed_size_even_above_the_source()
    {
        // 32 over a 14px line: 自動 would hold it to 14 × 1.15. 固定 asked for larger text.
        var fit = RealtimeFixedFontLayout.Line(32, maxWidth: 600, maxHeight: 200, MinFontSize, Em(5));

        Assert.Equal(32, fit.FontSize);
        Assert.False(fit.Wrapped);
        Assert.Equal(160, fit.TextWidth);
    }

    [Fact]
    public void A_long_line_keeps_the_fixed_size_and_wraps_inside_the_block()
    {
        // 50 glyphs at 20 is 1000 wide against 600 of room: 自動 would shrink it to one line.
        var fit = RealtimeFixedFontLayout.Line(20, maxWidth: 600, maxHeight: 200, MinFontSize, Em(50));

        Assert.Equal(20, fit.FontSize);
        Assert.True(fit.Wrapped);
        Assert.True(fit.TextWidth <= 600, $"width {fit.TextWidth}");
        Assert.True(fit.TextHeight <= 200, $"height {fit.TextHeight}");
        Assert.Equal(2 * 20 * 1.22, fit.TextHeight, 3);
    }

    [Fact]
    public void Wrapping_past_the_block_is_the_only_thing_that_shrinks_it_and_then_just_enough()
    {
        // 200 glyphs at 20 wrap to seven lines, 170 tall, in a block 60 tall.
        var measure = Em(200);
        var fit = RealtimeFixedFontLayout.Line(20, maxWidth: 600, maxHeight: 60, MinFontSize, measure);

        Assert.True(fit.Wrapped);
        Assert.True(fit.FontSize < 20, $"font {fit.FontSize}");
        Assert.True(fit.TextHeight <= 60, $"height {fit.TextHeight} — the part past the block is not drawn");
        Assert.True(measure(fit.FontSize + 0.5, 600).Height > 60, "shrunk further than it had to be");
    }

    [Fact]
    public void A_group_wraps_at_its_own_width_and_widens_only_once_that_is_too_tall()
    {
        var narrow = RealtimeFixedFontLayout.Group(
            20, groupWidth: 200, maxWidth: 600, maxHeight: 200, MinFontSize, Em(20));
        Assert.Equal(20, narrow.FontSize);
        Assert.Equal(200, narrow.TextWidth);

        // 60 glyphs at the group's 10 a line is six lines, 146 tall, against 100 of room — across
        // the block they take two.
        var widened = RealtimeFixedFontLayout.Group(
            20, groupWidth: 200, maxWidth: 600, maxHeight: 100, MinFontSize, Em(60));
        Assert.Equal(20, widened.FontSize);
        Assert.True(widened.TextWidth > 200 && widened.TextWidth <= 600, $"width {widened.TextWidth}");
        Assert.True(widened.TextHeight <= 100, $"height {widened.TextHeight}");
    }

    [Fact]
    public void A_fixed_size_smaller_than_the_source_still_leaves_the_source_covered() => OnSta(() =>
    {
        // A 40px line set at 10: the text is a quarter of the line it replaces.
        var source = new Rect(20, 100, 300, 40);
        var (background, text) = Render(RealtimeBlockMode.Subtitle, fixedFontSize: 10, source, "說得也是");

        Assert.Equal(10, text.FontSize);
        double top = Canvas.GetTop(background);
        Assert.True(top <= source.Top, $"band starts at {top}, the source at {source.Top}");
        Assert.True(top + background.Height >= source.Bottom,
            $"band ends at {top + background.Height}, the source at {source.Bottom}");
    });

    [Fact]
    public void A_fixed_size_larger_than_the_source_grows_the_band_round_it() => OnSta(() =>
    {
        // 32 over a 17px line: drawn at 32, not capped at the line's height.
        var source = new Rect(20, 100, 300, 17);
        var (background, text) = Render(RealtimeBlockMode.Subtitle, fixedFontSize: 32, source, "說得也是");

        Assert.Equal(32, text.FontSize);
        Assert.True(background.Height >= 32 * 1.22, $"band {background.Height}");
        double middle = Canvas.GetTop(background) + background.Height / 2;
        Assert.Equal(source.Top + source.Height / 2, middle, 1);
    });

    [Fact]
    public void A_panel_row_too_narrow_for_the_fixed_size_falls_back_to_the_group() => OnSta(() =>
    {
        // Ten glyphs at 20 are 200 wide in a 60px row: the row's Viewbox would squeeze it, so it is
        // drawn as one block instead, at 20.
        var (_, wide) = Render(RealtimeBlockMode.Panel, fixedFontSize: 20, new Rect(20, 20, 60, 30), "這是一段比較長的譯文");
        Assert.Equal(20, wide.FontSize);
        Assert.IsNotType<Viewbox>(LogicalParent(wide));

        // 12 fits the row, so the row is drawn as a row.
        var (_, fits) = Render(RealtimeBlockMode.Panel, fixedFontSize: 12, new Rect(20, 20, 300, 30), "譯文");
        Assert.Equal(12, fits.FontSize);
        Assert.IsType<Viewbox>(LogicalParent(fits));
    });

    [Fact]
    public void A_panel_row_too_short_for_the_fixed_size_keeps_its_row_and_grows_round_it() => OnSta(() =>
    {
        // 32 in a 20px row: wide enough, so it stays a row — on the menu's left margin — and its box
        // grows to the size round the row's centre instead of the Viewbox shrinking the text.
        var source = new Rect(20, 100, 300, 20);
        var (_, text) = Render(RealtimeBlockMode.Panel, fixedFontSize: 32, source, "譯文");
        var viewbox = Assert.IsType<Viewbox>(LogicalParent(text));
        var box = Assert.IsType<Border>(viewbox.Parent);

        Assert.Equal(32, text.FontSize);
        Assert.Equal(source.Left, Canvas.GetLeft(box));
        Assert.True(box.Height >= 32 * 1.22, $"row box {box.Height}");
        Assert.Equal(source.Top + source.Height / 2, Canvas.GetTop(box) + box.Height / 2, 3);
    });

    [Fact]
    public void Panel_rows_whose_grown_boxes_would_overlap_fall_back_to_the_group() => OnSta(() =>
    {
        // A menu with rows 20px apart, one block per item. At 32 each row's box would grow to ~39,
        // past the 20 to the next row's centre, and the two would be drawn over each other.
        TranslatedBlock[] menu =
        [
            new("Start", "開始遊戲", new Rect(20, 100, 300, 16)),
            new("Options", "選項設定", new Rect(20, 120, 300, 16)),
        ];

        var crowded = RenderAll(fixedFontSize: 32, menu);
        Assert.All(crowded, text => Assert.Equal(32, text.FontSize));
        Assert.All(crowded, text => Assert.IsNotType<Viewbox>(LogicalParent(text)));

        // At 12 the grown box is ~15, inside the 20: the rows stay rows, as region-panel-en at 12 does.
        var roomy = RenderAll(fixedFontSize: 12, menu);
        Assert.All(roomy, text => Assert.Equal(12, text.FontSize));
        Assert.All(roomy, text => Assert.IsType<Viewbox>(LogicalParent(text)));
    });

    [Fact]
    public void Settings_default_to_auto_and_clamp_the_fixed_size()
    {
        var settings = new RealtimeSettings();
        Assert.Equal(RealtimeFontSizeMode.Auto, settings.FontSizeMode);
        Assert.Equal(24, settings.FixedFontSize);

        // A file from before these keys reads as the overlay it always drew.
        var old = SettingsService.Parse("""{"Realtime":{"ScrimColor":"#1E3A5F"}}""");
        Assert.Equal(RealtimeFontSizeMode.Auto, old.Realtime.FontSizeMode);
        Assert.Equal(24, old.Realtime.FixedFontSize);

        var stored = SettingsService.Parse("""{"Realtime":{"FontSizeMode":"Fixed","FixedFontSize":32}}""");
        Assert.Equal(RealtimeFontSizeMode.Fixed, stored.Realtime.FontSizeMode);
        Assert.Equal(32, stored.Realtime.FixedFontSize);

        Assert.Equal(48, SettingsService.Parse("""{"Realtime":{"FixedFontSize":99}}""").Realtime.FixedFontSize);
        Assert.Equal(10, SettingsService.Parse("""{"Realtime":{"FixedFontSize":3}}""").Realtime.FixedFontSize);
    }

    /// <summary>Every glyph one em wide and every line 1.22 em tall, for <paramref name="glyphs"/> glyphs.</summary>
    private static Func<double, double?, Size> Em(int glyphs) => (size, width) =>
    {
        if (width is not { } room) return new Size(glyphs * size, size * 1.22);
        int perLine = Math.Max(1, (int)Math.Floor(room / size));
        int lines = (glyphs + perLine - 1) / perLine;
        return new Size(Math.Min(glyphs, perLine) * size, lines * size * 1.22);
    };

    private static DependencyObject? LogicalParent(TextBlock text) => LogicalTreeHelper.GetParent(text);

    /// <summary>The one box a single line draws: its background and its text.</summary>
    private static (Border Background, TextBlock Text) Render(
        RealtimeBlockMode mode, double fixedFontSize, Rect source, string translation)
    {
        var window = new RealtimeBlockWindow(0, new System.Drawing.Rectangle(0, 0, 600, 300),
            _ => null, "JA", "ZH-TW", "#FFFFFF", "#000000", 70, mode: mode, fixedFontSize: fixedFontSize);
        try
        {
            var type = typeof(RealtimeBlockWindow);
            type.GetField("_lines", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window,
                new List<TranslatedBlock> { new("source line", translation, source) });
            type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var background = Assert.IsType<Border>(Assert.Single(((Canvas)window.FindName("ScrimCanvas")).Children));
            var box = Assert.IsType<Border>(Assert.Single(((Canvas)window.FindName("TextCanvas")).Children));
            var text = box.Child as TextBlock ?? Assert.IsType<TextBlock>(Assert.IsType<Viewbox>(box.Child).Child);
            return (background, text);
        }
        finally { window.Close(); }
    }

    /// <summary>Every line's text, in a Panel block, for lines that each draw one box.</summary>
    private static List<TextBlock> RenderAll(double fixedFontSize, IReadOnlyList<TranslatedBlock> lines)
    {
        var window = new RealtimeBlockWindow(0, new System.Drawing.Rectangle(0, 0, 600, 300),
            _ => null, "EN", "ZH-TW", "#FFFFFF", "#000000", 70, mode: RealtimeBlockMode.Panel,
            fixedFontSize: fixedFontSize);
        try
        {
            var type = typeof(RealtimeBlockWindow);
            type.GetField("_lines", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, lines.ToList());
            type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var boxes = ((Canvas)window.FindName("TextCanvas")).Children.Cast<Border>().ToList();
            Assert.Equal(lines.Count, boxes.Count);
            return [.. boxes.Select(box =>
                box.Child as TextBlock ?? Assert.IsType<TextBlock>(Assert.IsType<Viewbox>(box.Child).Child))];
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
