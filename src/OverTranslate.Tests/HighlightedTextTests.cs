using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using OverTranslate.Views.Controls;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The parsing half of the hint markup. Every case here is a string a translator can really write,
/// and the rule they all answer to is the same: a hint is the least important line on the page, so
/// nothing malformed may cost the user the sentence.
/// </summary>
public class HighlightedTextTests
{
    [Fact]
    public void An_unmarked_line_is_one_plain_stretch()
    {
        Assert.Equal(
            [new HighlightedText.Segment("按下快捷鍵即可使用截圖翻譯功能", false)],
            HighlightedText.Split("按下快捷鍵即可使用截圖翻譯功能"));
    }

    [Fact]
    public void Marked_words_are_separated_from_the_text_around_them()
    {
        Assert.Equal(
            [
                new HighlightedText.Segment("即時翻譯", true),
                new HighlightedText.Segment("進行中時，改為將", false),
                new HighlightedText.Segment("浮動視窗列", true),
                new HighlightedText.Segment("移至最上層", false),
            ],
            HighlightedText.Split("[[即時翻譯]]進行中時，改為將[[浮動視窗列]]移至最上層"));
    }

    [Fact]
    public void A_mark_at_the_very_end_needs_no_trailing_text()
    {
        Assert.Equal(
            [
                new HighlightedText.Segment("Press ", false),
                new HighlightedText.Segment("Ctrl+Alt+A", true),
            ],
            HighlightedText.Split("Press [[Ctrl+Alt+A]]"));
    }

    [Theory]
    [InlineData("這裡少了收尾 [[即時翻譯")]      // opened and never closed
    [InlineData("這裡多了收尾 即時翻譯]]")]      // closed but never opened
    public void A_broken_mark_degrades_to_plain_text_rather_than_eating_the_line(string text)
    {
        // Whatever else happens, every character the translator wrote still reaches the screen.
        Assert.Equal(text, string.Concat(HighlightedText.Split(text).Select(s => s.Text)));
    }

    [Fact]
    public void An_empty_mark_highlights_nothing_and_loses_nothing()
    {
        var segments = HighlightedText.Split("before [[]]after");

        Assert.Equal("before after", string.Concat(segments.Select(s => s.Text)));
        Assert.DoesNotContain(segments, s => s.Highlighted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_to_say_is_no_stretches_at_all(string? text)
    {
        Assert.Empty(HighlightedText.Split(text));
    }

    /// <summary>
    /// The other half: that setting the property really does fill a TextBlock, and that the marked
    /// run takes its colour from the theme by reference rather than by copy — which is what lets the
    /// emphasis follow a theme switch, and the one part of this that a parsing test cannot see.
    /// </summary>
    [Fact]
    public void The_marked_run_takes_the_accent_brush_from_the_resources_around_it()
    {
        OnUiThread(() =>
        {
            var accent = new SolidColorBrush(Colors.Magenta);
            var block = new TextBlock();
            block.Resources[HighlightedText.HighlightBrushKey] = accent;

            HighlightedText.SetSource(block, "plain [[marked]] plain");

            var runs = block.Inlines.OfType<Run>().ToList();
            Assert.Equal(["plain ", "marked", " plain"], runs.Select(r => r.Text));

            // Only the marked one is painted; the rest inherit whatever the hint's style says.
            Assert.Same(accent, runs[1].Foreground);
            Assert.Null(runs[0].ReadLocalValue(TextElement.ForegroundProperty) as Brush);
        });
    }

    [Fact]
    public void Setting_the_source_again_replaces_what_was_there()
    {
        // What a change of interface language does: the same TextBlock, a new string pushed through
        // the DynamicResource it is bound to.
        OnUiThread(() =>
        {
            var block = new TextBlock();

            HighlightedText.SetSource(block, "[[即時翻譯]]進行中");
            HighlightedText.SetSource(block, "while [[realtime]] runs");

            Assert.Equal(
                "while realtime runs",
                string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text)));
        });
    }

    /// <summary>
    /// The bug this had for one build: FieldHint collapses a TextBlock whose Text is empty,
    /// and content supplied as Inlines leaves Text empty — so every hint on the settings page filled
    /// in correctly and then vanished.
    /// </summary>
    /// <remarks>
    /// Against the real style out of the real dictionary rather than a copy of its trigger. The
    /// interaction being pinned is between two things that live apart and neither of which is wrong
    /// on its own, so a test carrying its own copy of one of them would go on passing after somebody
    /// changed the original.
    /// </remarks>
    [Fact]
    public void A_hint_wearing_the_page_style_stays_visible()
    {
        OnUiThread(() =>
        {
            var block = new TextBlock { Style = (Style)SharedStyles()["FieldHint"] };

            HighlightedText.SetSource(block, "plain [[marked]]");

            Assert.Equal(Visibility.Visible, block.Visibility);
            Assert.Equal(2, block.Inlines.Count);
        });
    }

    [Fact]
    public void A_hint_with_nothing_to_say_still_takes_up_no_room()
    {
        // The other half of the same trigger, which the local value above must not have cost us.
        OnUiThread(() =>
        {
            var block = new TextBlock { Style = (Style)SharedStyles()["FieldHint"] };

            HighlightedText.SetSource(block, "");

            Assert.Equal(Visibility.Collapsed, block.Visibility);
        });
    }

    /// <summary>
    /// The application's own style dictionary, loaded without an <see cref="Application"/> — the same
    /// trick LocalizationService uses for the strings, and for the same reason.
    /// </summary>
    private static ResourceDictionary SharedStyles()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

        return new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/OverTranslate;component/Themes/SharedStyles.xaml",
                UriKind.Absolute)
        };
    }

    /// <summary>
    /// WPF elements may only be built on an STA thread, and xunit runs its tests on MTA ones.
    /// </summary>
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
