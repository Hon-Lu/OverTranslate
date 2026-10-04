using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The one promise both overlays make about the text they draw: all of it is on screen.
/// </summary>
/// <remarks>
/// <para>Both used to finish a line that would not fit with <c>TextTrimming.CharacterEllipsis</c>,
/// which is the worst failure available to them — the reader is shown a sentence that ends, and
/// nothing tells them it ended early. See issue #73.</para>
///
/// <para>These build the real window and read back the visual tree it produced rather than
/// re-deriving its arithmetic. What is in question is whether the numbers agree with the box WPF
/// actually lays out, and a test that recomputed them would agree with itself whatever shipped.</para>
///
/// <para>Realtime only. The screenshot overlay resolves <c>{StaticResource AppFont}</c> as its XAML
/// loads, so constructing it needs an <see cref="Application"/> — process-wide state this suite
/// deliberately keeps out, for the reasons in AssemblyInfo. Its half of the same fix is covered by
/// <see cref="OverlayBubbleHeightTests"/> and by running it.</para>
/// </remarks>
public class OverlayTextFittingTests
{
    // Roughly a subtitle band: wide, and tall enough for the line plus the air the in-app guidance
    // asks the user to leave.
    private static readonly System.Drawing.Rectangle SubtitleBlock = new(200, 800, 900, 70);

    // Long enough that the readability floor cannot squeeze it onto one line of a 900px block:
    // ~160 CJK glyphs against the ~105 that fit at 8.5px. Below that the shrink absorbs everything
    // and the trimming this file is about never came up.
    private const string RunOnTranslation =
        "嗯，那麼接下來呢，我們就照著剛才已經討論好的順序，一項一項慢慢地把它處理完畢吧，這樣一來應該就不會有" +
        "任何遺漏了，也比較不會浪費彼此的時間，你覺得這樣安排可以嗎，如果沒有問題的話我們現在就開始動手，有任" +
        "何想法都可以隨時提出來一起討論";

    [Fact]
    public void A_translation_that_fits_is_still_drawn_on_one_line()
    {
        var drawn = Draw(Line("そうだね", "說得也是", new Rect(20, 15, 300, 34)));

        Assert.False(drawn.Wrapped);
    }

    [Fact]
    public void A_translation_too_long_for_one_line_wraps_instead_of_being_trimmed()
    {
        var drawn = Draw(Line("うん、じゃあ次はね", RunOnTranslation, new Rect(20, 15, 860, 34)));

        Assert.True(drawn.Wrapped);

        // Wrapping buys width back, so the size it settles on is well above the floor a single line
        // would have been ground down to. Getting the floor here would mean the search below the
        // wrap is picking up the shrunken size instead of the source-matched one.
        Assert.True(drawn.FontSize > 10, $"wrapped at {drawn.FontSize:0.##}px");
    }

    /// <summary>
    /// A block drawn too tight for the translation at any readable size. The band overflows it,
    /// which is visible and the user can redraw; the text itself stays whole.
    /// </summary>
    [Fact]
    public void A_block_too_small_for_any_readable_size_still_keeps_the_whole_sentence()
    {
        var drawn = Draw(
            Line("Hi", RunOnTranslation, new Rect(2, 2, 160, 18)),
            block: new System.Drawing.Rectangle(0, 0, 170, 40));

        Assert.True(drawn.Wrapped);
        Assert.True(drawn.BandHeight > 40, "the band is expected to outgrow a block this tight");
    }

    /// <summary>
    /// A model that answered over two lines. Nothing strips the break, and NoWrap does not ignore
    /// one — the TextBlock still breaks there — so the band has to be built tall enough for what is
    /// really in it. On the screenshot overlay this was the whole of the reported bug.
    /// </summary>
    [Fact]
    public void A_translation_that_arrives_with_a_line_break_keeps_both_lines()
    {
        var drawn = Draw(Line(
            "Hello there",
            "你好，很高興見到你\n今天過得還好嗎",
            new Rect(20, 15, 300, 34)));

        Assert.True(drawn.BandHeight > 40, $"band is {drawn.BandHeight:0.#}px, too short for two lines");
    }

    [Fact]
    public void A_grouped_block_is_drawn_whole_too()
    {
        var drawn = Draw(Line(
            "Are you sure about this?\nThere is no way back.",
            RunOnTranslation,
            new Rect(20, 5, 800, 60),
            sourceLines: [new Rect(20, 5, 800, 28), new Rect(20, 35, 700, 28)]));

        Assert.True(drawn.Wrapped);
    }

    /// <summary>
    /// A Latin line set at a slant: its upright box is mostly tilt, so the glyph height that covers
    /// it is far larger than the letters. The font follows the letters; the band still covers the
    /// slant. Measured on region-comic-en-3, where this line was drawn at a 147px font over 17px
    /// capitals.
    /// </summary>
    [Fact]
    public void A_tilted_line_is_sized_by_its_letters_but_still_covered_whole()
    {
        var tilted = new TranslatedBlock(
            "I THINK THAT GUY'S A PLAYER TOO.",
            "我覺得那傢伙也是個花花公子。",
            new Rect(100, 50, 322, 211),
            RenderGlyphHeight: 173) { FontGlyphHeight = 17.8 };
        var block = new System.Drawing.Rectangle(0, 0, 650, 400);

        var drawn = Draw(tilted, block);

        Assert.False(drawn.Wrapped);
        Assert.True(drawn.FontSize <= 17.8 * 1.15 + 0.01, $"font {drawn.FontSize:0.##}");
        Assert.True(drawn.BandHeight >= 211, $"band {drawn.BandHeight:0.##}");

        // The same line with no separate font height is sized from the coverage figure, as every
        // level line and every non-Latin source still is.
        var unchanged = Draw(tilted with { FontGlyphHeight = null }, block);
        Assert.True(unchanged.FontSize > 40, $"font {unchanged.FontSize:0.##}");
    }

    private readonly record struct DrawnLine(bool Wrapped, double FontSize, double BandHeight);

    /// <summary>
    /// Builds the block window for real, checks the box it produced against the text put inside it,
    /// and returns what the calling test is specifically about.
    /// </summary>
    private static DrawnLine Draw(TranslatedBlock line, System.Drawing.Rectangle? block = null) =>
        OnStaThread(() =>
        {
            // 進階選項 are both off here, so nothing in this window asks for the picture underneath;
            // these tests are about the box the text lands in, not about what is behind it.
            var window = new RealtimeBlockWindow(
                0, block ?? SubtitleBlock, _ => null, "JA", "ZH-TW",
                RealtimeSubtitleColors.DefaultText,
                RealtimeSubtitleColors.DefaultScrim,
                RealtimeSubtitleColors.DefaultScrimOpacity);

            // The window is never shown, so nothing raises Loaded and nothing reads a DPI off a real
            // presentation source. The 1.0 it starts at is what an unscaled display would have given.
            typeof(RealtimeBlockWindow)
                .GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, true);

            window.SetLines([line]);

            var canvas = (Canvas)window.FindName("TextCanvas");
            var container = Assert.IsType<Border>(Assert.Single(canvas.Children));
            var text = Assert.IsType<TextBlock>(container.Child);

            Assert.Equal(TextTrimming.None, text.TextTrimming);

            var innerWidth = container.Width - container.Padding.Left - container.Padding.Right;
            var innerHeight = container.Height - container.Padding.Top - container.Padding.Bottom;
            var wrapped = text.TextWrapping == TextWrapping.Wrap;

            var laidOut = new FormattedText(
                text.Text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize,
                Brushes.Black,
                1.0);

            // Half a pixel of tolerance: the box was built from measurements of this same text, so
            // anything past rounding is text the reader would not get.
            if (wrapped)
            {
                laidOut.MaxTextWidth = innerWidth;
                Assert.True(
                    laidOut.Height <= innerHeight + 0.5,
                    $"wrapped text needs {laidOut.Height:0.##}px of height, box gives {innerHeight:0.##}px");
            }
            else
            {
                Assert.True(
                    laidOut.Width <= innerWidth + 0.5,
                    $"one line needs {laidOut.Width:0.##}px of width, box gives {innerWidth:0.##}px");
                Assert.True(
                    laidOut.Height <= innerHeight + 0.5,
                    $"one line needs {laidOut.Height:0.##}px of height, box gives {innerHeight:0.##}px");
            }

            return new DrawnLine(wrapped, text.FontSize, container.Height);
        });

    private static TranslatedBlock Line(
        string original, string translated, Rect bounds, IReadOnlyList<Rect>? sourceLines = null) =>
        new(original, translated, bounds, sourceLines);

    /// <summary>
    /// Runs the work on a fresh STA thread, which WPF elements require and xunit's own worker is
    /// not. Nothing built there outlives the call, so no state reaches the rest of the suite.
    /// </summary>
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
