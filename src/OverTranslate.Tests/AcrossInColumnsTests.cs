using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Overlay;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// #315: a vertical source translated into a language that is not written downwards is set
/// across in the column's place, instead of one letter per cell.
/// </summary>
/// <remarks>
/// The rules are checked against a plain monospaced measure; the two windows are then built for
/// real, as <see cref="RealtimeVerticalTextTests"/> builds them, and read back out of the visual
/// tree — the drawn TextBlock's own height, not the measurement the layout was given.
/// </remarks>
public class AcrossInColumnsTests
{
    // ---- which targets keep the columns ----

    [Theory]
    [InlineData("JA")]
    [InlineData("ZH-HANT")]
    [InlineData("ZH")]
    [InlineData("ZH-HANS")]
    [InlineData("KO")]
    [InlineData("zh-hant")]
    public void The_languages_written_downwards_keep_their_columns(string target) =>
        Assert.True(VerticalTargetLanguages.SetsDown(target));

    /// <summary>
    /// Every other target the app offers is set across, and so is one this build has never heard of.
    /// </summary>
    [Theory]
    [InlineData("EN")]
    [InlineData("ID")]
    [InlineData("DE")]
    [InlineData("RU")]
    [InlineData("EL")]
    [InlineData("FI")]
    [InlineData("XX")]
    [InlineData("")]
    [InlineData(null)]
    public void Every_other_target_is_set_across(string? target) =>
        Assert.False(VerticalTargetLanguages.SetsDown(target));

    // ---- placement ----

    [Fact]
    public void Placement_marks_a_column_across_for_a_target_not_written_downwards_and_keeps_its_columns()
    {
        var cells = new[] { new Rect(80, 10, 20, 20), new Rect(80, 30, 20, 20) };
        var column = new TranslatedBlock("縦書き", "Vertical", new Rect(80, 10, 20, 40), cells);
        var plate = new TranslatedBlock("名前", "Name", new Rect(10, 100, 120, 20)) { RunsAcross = true };

        var placed = OverlayPlacement.Place([column, plate], CaptureLayoutMode.General, verticalText: true, "EN");

        Assert.Equal(OverlayLayoutIntent.AcrossInColumns, placed[0].LayoutIntent);
        Assert.Same(cells, placed[0].SourceLineBounds);
        Assert.Equal(column with { LayoutIntent = OverlayLayoutIntent.AcrossInColumns }, placed[0]);
        // What the reading stage already set across is untouched by the target.
        Assert.Equal(OverlayLayoutIntent.Default, placed[1].LayoutIntent);
    }

    // ---- the fit ----

    // A monospaced face: each character 0.55 of the size across, each line 1.3 of it down, broken
    // at spaces and, for a word wider than the line, inside it — as a TextBlock breaks one.
    private static Size Mono(string text, double size, double? width)
    {
        double advance = size * 0.55;
        double lineHeight = size * 1.3;
        if (width is not { } maxWidth) return new(text.Length * advance, lineHeight);

        int perLine = Math.Max(1, (int)Math.Floor(maxWidth / advance + 1e-9));
        int lines = 0;
        int used = 0;
        double widest = 0;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int need = used == 0 ? word.Length : used + 1 + word.Length;
            if (used > 0 && need <= perLine)
            {
                used = need;
            }
            else
            {
                if (used > 0) widest = Math.Max(widest, used * advance);
                int pieces = (word.Length + perLine - 1) / perLine;
                lines += pieces;
                used = word.Length - (pieces - 1) * perLine;
                if (pieces > 1) widest = Math.Max(widest, perLine * advance);
            }
        }
        widest = Math.Max(widest, used * advance);
        return new(widest, Math.Max(1, lines) * lineHeight);
    }

    private const string Sentence = "I really did not think you would actually come back here";

    [Fact]
    public void A_sentence_in_a_one_column_balloon_fits_its_height_holding_every_word_whole()
    {
        var room = new AcrossInColumnsRoom(40, 40, 0);
        var fit = AcrossInColumnsLayout.Fit(Sentence, 30, 300, 26, 9.5, 7, room, Mono);

        double longestWord = Sentence.Split(' ').Max(word => Mono(word, fit.FontSize, null).Width);
        Assert.True(fit.Width >= longestWord, $"{fit.Width:0.#}px will not hold a {longestWord:0.#}px word");
        Assert.True(fit.Height <= 300 + 0.01, $"the text runs {fit.Height:0.#}px down a 300px column");
        Assert.True(fit.Left >= -40 - 0.01 && fit.Left + fit.Width <= 30 + 40 + 0.01, "the text box left its room");
        Assert.True(fit.FontSize <= 26, "the size grew past where it started");
    }

    /// <summary>
    /// Size before width: a translation that fits the balloon's own margin at the starting size is
    /// not shrunk, and it does not spread past that margin either.
    /// </summary>
    [Fact]
    public void A_short_translation_keeps_its_size_and_sits_in_the_middle_of_the_column()
    {
        var fit = AcrossInColumnsLayout.Fit("What?", 30, 300, 24, 9.5, 7, new(200, 200, 200), Mono);

        Assert.Equal(24, fit.FontSize);
        Assert.Equal(30 / 2.0, fit.Left + fit.Width / 2, 1);
        Assert.Equal(300 / 2.0, fit.Top + fit.Height / 2, 1);
    }

    /// <summary>
    /// The narrowest width that fits, rather than one wide band across the middle of a tall balloon.
    /// </summary>
    [Fact]
    public void The_text_takes_the_balloon_shape_rather_than_a_wide_band()
    {
        var fit = AcrossInColumnsLayout.Fit(Sentence, 30, 300, 14, 9.5, 7, new(400, 400, 0), Mono);

        Assert.True(fit.Width < 30 + 14 * 2 + 0.01, $"the text spread to {fit.Width:0.#}px with room to stay narrow");
    }

    /// <summary>
    /// A word too long for the margin is held whole by taking the room beside the column; with no
    /// room for it at any readable size it is broken at a readable size, not shrunk out of sight.
    /// </summary>
    [Fact]
    public void A_long_compound_word_is_kept_whole_while_any_size_still_holds_it()
    {
        const string word = "Donaudampfschifffahrtsgesellschaft";

        var roomy = AcrossInColumnsLayout.Fit(word, 30, 300, 20, 9.5, 7, new(150, 150, 0), Mono);
        Assert.True(roomy.Width >= Mono(word, roomy.FontSize, null).Width, "the word was broken with room beside it");

        var tight = AcrossInColumnsLayout.Fit(word, 30, 300, 20, 9.5, 7, new(60, 60, 0), Mono);
        Assert.True(tight.FontSize >= 9.5, $"the word was shrunk to {tight.FontSize}px rather than broken");
        Assert.True(tight.Width <= 30 + 120 + 0.01, $"the text box is {tight.Width:0.#}px against 150px of room");
        Assert.True(tight.Height <= 300 + 0.01, "the broken word ran past the column");
    }

    /// <summary>
    /// The last resort is a box taller than the room, never a shorter sentence.
    /// </summary>
    [Fact]
    public void Text_that_fits_nowhere_is_set_at_the_emergency_size_whole()
    {
        string text = string.Concat(Enumerable.Repeat("words that will never fit ", 40));

        var fit = AcrossInColumnsLayout.Fit(text, 30, 60, 20, 9.5, 7, AcrossInColumnsRoom.None, Mono);

        Assert.Equal(7, fit.FontSize);
        Assert.Equal(Mono(text, 7, fit.Width).Height, fit.Height, 3);
        Assert.Equal(0, fit.Top);
    }

    /// <summary>
    /// 固定: the size is the reader's, so the room beside the column goes before any of it.
    /// </summary>
    [Fact]
    public void A_fixed_size_takes_the_room_before_it_gives_up_any_size()
    {
        var auto = AcrossInColumnsLayout.Fit(Sentence, 30, 120, 24, 9.5, 7, new(200, 200, 0), Mono);
        var fixedSize = AcrossInColumnsLayout.Fit(Sentence, 30, 120, 24, 24, 7, new(200, 200, 0), Mono, keepSize: true);

        Assert.True(auto.FontSize < 24, "自動 was expected to shrink inside the balloon's margin");
        Assert.Equal(24, fixedSize.FontSize);
    }

    [Fact]
    public void Room_beside_a_neighbour_is_half_the_gap_and_the_edge_is_taken_whole()
    {
        var source = new Rect(100, 50, 30, 200);
        var room = AcrossInColumnsRoom.Within(
            source,
            [new Rect(40, 60, 30, 100), new Rect(100, 300, 30, 50), new Rect(500, 600, 10, 10)],
            new Rect(0, 0, 200, 400));

        Assert.Equal((100 - 70) / 2.0, room.Left, 3);
        Assert.Equal(200 - 130, room.Right, 3);
        Assert.Equal(300 - 250, room.Below, 3);
    }

    // ---- the live overlay ----

    private static readonly System.Drawing.Rectangle Block = new(300, 100, 600, 400);
    private static readonly Rect SourceColumn = new(400, 40, 34, 300);

    private readonly record struct DrawnAcross(string Text, Rect TextBox, double DrawnHeight, Rect Background, int Elements);

    [Theory]
    [InlineData("EN", "I really did not think you would actually come back here")]
    [InlineData("ID", "Aku benar-benar tidak menyangka kamu akan kembali ke sini")]
    [InlineData("DE", "Donaudampfschifffahrtsgesellschaftskapitän kommt zurück")]
    [InlineData("RU", "Я правда не думал, что ты вернёшься сюда")]
    [InlineData("EL", "Δεν πίστευα ότι θα επέστρεφες εδώ")]
    public void The_live_overlay_sets_the_translation_across_in_the_columns_place(string target, string translated)
    {
        var drawn = OnStaThread(() => DrawLive(target, translated, SourceColumn));

        // One element holding the whole sentence, not a cell per letter.
        Assert.Equal(1, drawn.Elements);
        Assert.Equal(translated, drawn.Text);

        // The drawn TextBlock's own height, measured again at the width it was given, is inside the
        // band: nothing below it was measured short and left outside.
        Assert.True(drawn.DrawnHeight > 0, "the text was not measured at all");
        Assert.True(drawn.Background.Top <= drawn.TextBox.Top + 0.5
            && drawn.Background.Top + drawn.Background.Height >= drawn.TextBox.Top + drawn.DrawnHeight - 0.5,
            "the band does not cover the drawn text");

        // Still covering the column it replaces, and still inside the block.
        Assert.True(drawn.Background.Left <= SourceColumn.Left + 0.5 && drawn.Background.Right >= SourceColumn.Right - 0.5);
        Assert.True(drawn.Background.Top <= SourceColumn.Top + 0.5 && drawn.Background.Bottom >= SourceColumn.Bottom - 0.5);
        Assert.True(drawn.TextBox.Left >= -0.5 && drawn.TextBox.Right <= Block.Width + 0.5);
    }

    [Theory]
    [InlineData("JA", "そうだったのか")]
    [InlineData("KO", "그랬구나")]
    public void The_live_overlay_still_sets_a_target_read_downwards_down_the_column(string target, string translated)
    {
        var drawn = OnStaThread(() => DrawLive(target, translated, SourceColumn));

        Assert.True(drawn.Elements > 1, $"a {target} target is expected one glyph per cell");
    }

    private static DrawnAcross DrawLive(string target, string translated, Rect column)
    {
        var window = new RealtimeBlockWindow(
            0, Block, _ => null, "JA", target,
            RealtimeSubtitleColors.DefaultText,
            RealtimeSubtitleColors.DefaultScrim,
            RealtimeSubtitleColors.DefaultScrimOpacity,
            orientation: RealtimeTextOrientation.Vertical);
        typeof(RealtimeBlockWindow)
            .GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, true);

        window.SetLines([new TranslatedBlock("これはたてがき", translated, column, null, 34)]);

        var container = Assert.IsType<Border>(Assert.Single(((Canvas)window.FindName("TextCanvas")).Children));
        var scrim = Assert.IsType<Border>(Assert.Single(((Canvas)window.FindName("ScrimCanvas")).Children));
        var background = new Rect(Canvas.GetLeft(scrim), Canvas.GetTop(scrim), scrim.Width, scrim.Height);

        if (container.Child is not TextBlock text)
        {
            int cells = container.Child is Canvas grid ? grid.Children.Count : 0;
            return new DrawnAcross("", Rect.Empty, 0, background, cells);
        }

        // Taken out of the window first: nothing in a window that was never shown is laid out, so a
        // measurement taken in place reads zero whatever the text.
        container.Child = null;
        MeasureOutside(text, container.Width);
        var box = new Rect(Canvas.GetLeft(container), Canvas.GetTop(container), container.Width, text.DesiredSize.Height);
        return new DrawnAcross(text.Text, box, text.DesiredSize.Height, background, 1);
    }

    // ---- the screenshot overlay ----

    [Theory]
    [InlineData("EN", "I really did not think you would actually come back here")]
    [InlineData("DE", "Donaudampfschifffahrtsgesellschaftskapitän kommt zurück")]
    [InlineData("RU", "Я правда не думал, что ты вернёшься сюда")]
    public void The_screenshot_overlay_sets_the_translation_across_over_the_column(string target, string translated)
    {
        var (text, textBox, background) = OnStaThread(() =>
        {
            var source = new TranslatedBlock("これはたてがき", translated, new Rect(400, 40, 34, 300), null, 34);
            var placed = OverlayPlacement.Place([source], CaptureLayoutMode.General, verticalText: true, target);
            var window = new OverlayWindow([], [], 0, 0, 941, 900, "JA", target, true);
            try
            {
                typeof(OverlayWindow).GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.UpdateBlocks(placed, [], 0, 0, 941, 900, "JA", target, true);
                var bubble = Assert.IsAssignableFrom<FrameworkElement>(
                    Assert.Single(((Canvas)window.FindName("BubbleBackgroundCanvas")).Children));
                var texts = (Canvas)window.FindName("BubbleTextCanvas");
                var written = Assert.IsType<TextBlock>(Assert.Single(texts.Children));
                // Out of the window to be measured — see DrawLive.
                texts.Children.Remove(written);
                MeasureOutside(written, written.Width);
                return (written.Text,
                    new Rect(Canvas.GetLeft(written), Canvas.GetTop(written), written.Width, written.DesiredSize.Height),
                    new Rect(Canvas.GetLeft(bubble), Canvas.GetTop(bubble), bubble.Width, bubble.Height));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(translated, text);
        Assert.True(textBox.Height > 0, "the text was not measured at all");
        // The bubble holds the text as it is drawn, and still hides the column.
        Assert.True(background.Contains(textBox), $"the text at {textBox} is outside its bubble {background}");
        var physical = ScreenGeometry.VirtualDesktopBounds();
        var column = new Rect(400 - physical.Left, 40 - physical.Top, 34, 300);
        Assert.True(background.Contains(column), $"the bubble {background} leaves the column {column} showing");
    }

    // In a panel of its own, laid out there: measured bare, an element out of a window that was
    // never shown still reads zero.
    private static void MeasureOutside(TextBlock text, double width)
    {
        var host = new Canvas();
        host.Children.Add(text);
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, 1, 1));
        text.Measure(new Size(width, double.PositiveInfinity));
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
