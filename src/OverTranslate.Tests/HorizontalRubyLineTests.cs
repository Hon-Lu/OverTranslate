using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// The reading printed above a line, dropped before grouping sees the page.
/// </summary>
/// <remarks>
/// <para>A different job from <see cref="VerticalRubyColumnTests"/>. Across the page a reading is
/// never merged into the sentence — it is half the size, so the grouper's size gate refuses it —
/// so what this removes is a bubble drawn over the page and a translation request spent on kana.
/// Since the gentler verdict is already the behaviour without it, the only verdict here is to
/// delete, and every test below is really asking the same question: is this certainly a reading.
/// </para>
///
/// <para>The ruby figures are real boxes off the three pages under
/// <c>.ai/test-images/ja-ruby</c>; the two that must be kept are real boxes off the image corpus.
/// </para>
/// </remarks>
public class HorizontalRubyLineTests
{
    private static OcrTextBlock Line(string text, Rect bounds) =>
        new(text, bounds) { LayoutBounds = bounds };

    private static List<string> Kept(params OcrTextBlock[] lines) =>
        HorizontalRubyLines.Drop([.. lines]).Select(line => line.Text).ToList();

    /// <summary>としょかん over 図書館だより, on textbook.png.</summary>
    [Fact]
    public void A_reading_above_its_line_is_dropped()
    {
        var reading = Line("としょかん", new Rect(113, 70, 154, 37));
        var writing = Line("図書館だより", new Rect(97, 99, 349, 66));

        Assert.Equal(["図書館だより"], Kept(reading, writing));
    }

    /// <summary>
    /// A single kana is a reading too, so the reading is not asked to be wider than it is tall.
    /// </summary>
    /// <remarks>
    /// よ over 読, inside 今日は学校の図書館で…: one character, 32 by 31, which is the commonest
    /// shape a reading has and the one an aspect test would throw away. The line it reads is asked
    /// to run across the page; the reading is not.
    /// </remarks>
    [Fact]
    public void A_one_character_reading_is_dropped()
    {
        var reading = Line("よ", new Rect(838, 228, 32, 31));
        var writing = Line("今日は学校の図書館で、まえから読みたかった本を借りました。", new Rect(93, 252, 1378, 57));

        Assert.Equal(["今日は学校の図書館で、まえから読みたかった本を借りました。"], Kept(reading, writing));
    }

    /// <summary>
    /// A label standing above a line is kept, because it leaves clear space the reading never does.
    /// </summary>
    /// <remarks>
    /// しゅとく over 第三階層 西の祭壇, on panel.png, and the nearest thing in the corpus to a
    /// false positive: kana only, small, inside the line's span, hiragana. What refuses it is that
    /// it sits 0.40 of a line clear, while ruby is set hard on the base line's ascent and every
    /// reading measured overlaps it.
    /// </remarks>
    [Fact]
    public void A_label_standing_clear_above_a_line_is_kept()
    {
        var label = Line("しゅとく", new Rect(113, 224, 97, 30));
        var writing = Line("第三階層 西の祭壇", new Rect(105, 273, 328, 47));

        Assert.Equal(["しゅとく", "第三階層 西の祭壇"], Kept(label, writing));
    }

    /// <summary>
    /// Katakana over kanji is kept, whatever the geometry says.
    /// </summary>
    /// <remarks>
    /// エンディングテーマ over 夢限大みゅーたいぷ, on a poster in screen-panel-en-ja: 0.45 of the
    /// title's height, inside its span, and only 0.03 of a line clear of it — the geometry alone
    /// calls this a reading. Katakana ruby is real, but katakana over kanji is more often a label,
    /// and this rule only deletes. Keeping it is what a doubtful verdict looks like here.
    /// </remarks>
    [Fact]
    public void Katakana_above_a_line_is_kept()
    {
        var label = Line("エンディングテーマ", new Rect(379, 140, 77, 19));
        var title = Line("夢限大みゅーたいぷ", new Rect(333, 160, 172, 31));

        Assert.Equal(["エンディングテーマ", "夢限大みゅーたいぷ"], Kept(label, title));
    }

    /// <summary>Two lines of one size are two lines, however tightly they are set.</summary>
    [Fact]
    public void A_line_of_kana_the_same_size_as_the_one_below_is_kept()
    {
        var above = Line("そのひとはなにもいわずに", new Rect(100, 200, 620, 54));
        var below = Line("静かに席を立った。", new Rect(100, 252, 480, 55));

        Assert.Equal(2, Kept(above, below).Count);
    }

    /// <summary>A reading belongs to the line under it, not to one in the next column.</summary>
    [Fact]
    public void A_reading_over_a_line_it_does_not_overlap_is_kept()
    {
        var reading = Line("としょかん", new Rect(113, 70, 154, 37));
        var elsewhere = Line("図書館だより", new Rect(900, 99, 349, 66));

        Assert.Equal(2, Kept(reading, elsewhere).Count);
    }

    /// <summary>A reading needs a kanji to be the reading of.</summary>
    [Fact]
    public void Kana_above_a_line_without_kanji_is_kept()
    {
        var above = Line("あたら", new Rect(94, 394, 77, 33));
        var below = Line("あたらしいものがたりはとてもおもしろくて", new Rect(103, 419, 1434, 56));

        Assert.Equal(2, Kept(above, below).Count);
    }

    /// <summary>
    /// The room a reading took goes to the line it read, on the drawn rectangle only.
    /// </summary>
    /// <remarks>
    /// <see cref="OcrTextBlock.Bounds"/> is what the overlay covers, so the translation is drawn
    /// over the reading instead of leaving it showing above the band.
    /// <see cref="OcrTextBlock.LayoutBounds"/> is what grouping measures leading and alignment
    /// with, and is deliberately untouched: growing a line's box upward by half a line would shrink
    /// every ratio taken against it, on a page whose leading is already wide because it sets ruby.
    /// </remarks>
    [Fact]
    public void The_readings_room_is_given_to_the_drawn_box_and_not_to_the_measured_one()
    {
        var reading = Line("としょかん", new Rect(113, 70, 154, 37));
        var writing = Line("図書館だより", new Rect(97, 99, 349, 66));

        var kept = Assert.Single(HorizontalRubyLines.Drop([reading, writing]));

        Assert.Equal(70, kept.Bounds.Top);
        Assert.Equal(writing.LayoutBounds, kept.LayoutBounds);
    }

    /// <summary>
    /// A page with no readings on it comes back as it went in.
    /// </summary>
    /// <remarks>
    /// This runs on every horizontal capture the app groups, including every English one, so the
    /// case that matters most is the one where it must do nothing at all.
    /// </remarks>
    [Fact]
    public void A_page_with_no_readings_is_returned_unchanged()
    {
        var first = Line("Shots deal more damage for each bullet remaining in the", new Rect(100, 200, 620, 30));
        var second = Line("magazine", new Rect(100, 232, 140, 26));

        Assert.Same(first, HorizontalRubyLines.Drop([first, second])[0]);
    }

    /// <summary>
    /// Through the grouper: the readings go and the sentence arrives whole.
    /// </summary>
    /// <remarks>
    /// The first line of textbook.png as the detector reads it — three readings over one sentence,
    /// one of them two readings the detector framed as a single box. Before this rule the page sent
    /// fifteen groups to be translated and eleven of them were kana.
    /// </remarks>
    [Fact]
    public void A_sentence_under_its_readings_reaches_the_translator_alone()
    {
        OcrTextBlock[] page =
        [
            Line("きょうがっこう", new Rect(106, 225, 273, 37)),
            Line("よ", new Rect(838, 228, 32, 31)),
            Line("ほんか", new Rect(1113, 228, 142, 31)),
            Line("としょかん", new Rect(387, 230, 132, 29)),
            Line("今日は学校の図書館で、まえから読みたかった本を借りました。", new Rect(93, 252, 1378, 57)),
        ];

        var grouped = OcrTextBlockGrouper.Group(page, GroupingProfile.General);

        Assert.Equal(["今日は学校の図書館で、まえから読みたかった本を借りました。"], grouped.Select(b => b.Text));
    }
}
