using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// One piece of writing detected twice, and the copy dropped before grouping reads both.
/// </summary>
/// <remarks>
/// Every pair below is a real one off the 15 comic pages in
/// <c>.ai/test-images/vertical-image-ja2</c> or the frames the app captured while the user read
/// them. What makes this delicate is that ruby lives in the same place — a reading is set hard
/// against its kanji, so the writing's expanded box swallows it — and ruby must survive this to
/// reach <see cref="VerticalRubyColumns"/>.
/// </remarks>
public class VerticalRepeatedColumnTests
{
    private static OcrTextBlock Column(string text, Rect bounds, double confidence = 0.9) =>
        new(text, bounds) { LayoutBounds = bounds, Confidence = confidence };

    /// <summary>の read twice inside 他の, which the reader was shown as のの他のパーティか…</summary>
    [Fact]
    public void A_second_detection_of_the_same_writing_goes()
    {
        var writing = Column("他の", new Rect(1387, 765, 53, 65));
        var again = Column("の", new Rect(1394, 778, 35, 43));
        var thrice = Column("の", new Rect(1393, 796, 26, 29));

        Assert.Equal(["他の"],
            VerticalRepeatedColumns.Drop([writing, again, thrice]).Select(c => c.Text));
    }

    /// <summary>The same box read the same way twice, which happens too.</summary>
    [Fact]
    public void The_same_writing_framed_twice_is_read_once()
    {
        var one = Column("別の魔獣が", new Rect(100, 50, 60, 250));
        var other = Column("別の魔獣が", new Rect(103, 60, 55, 110));

        Assert.Single(VerticalRepeatedColumns.Drop([one, other]));
    }

    /// <summary>
    /// A reading sitting inside the box of the writing it annotates is NOT a second detection.
    /// </summary>
    /// <remarks>
    /// This is the case that decides the whole design. Over the two corpora 62 boxes lie inside
    /// another and 38 of them are readings, so anything deciding on position alone throws the lot
    /// away — and dropping a reading here would take it out of
    /// <see cref="VerticalRubyColumns"/>'s hands, which is where the size and spacing are measured.
    /// A pronunciation shares no characters with the kanji it is set against, and every ruby pair
    /// measured scores 0.
    /// </remarks>
    [Theory]
    [InlineData("あつか", 19, 36, "支援魔術を扱う", 52, 198)]
    [InlineData("えん", 24, 47, "支援魔術【力上昇】！", 153, 589)]
    [InlineData("とき", 18, 43, "いざって時に仲間を", 67, 394)]
    [InlineData("ぬ", 15, 15, "パーティから抜けてもらう", 73, 564)]
    public void A_reading_inside_the_writings_box_is_kept(
        string reading, int width, int height, string writing, int wide, int tall)
    {
        var body = Column(writing, new Rect(100, 50, wide, tall));
        var ruby = Column(reading, new Rect(104, 54, width, height));

        Assert.Equal(2, VerticalRepeatedColumns.Drop([body, ruby]).Count);
    }

    /// <summary>Writing that merely happens to sit inside another box is kept.</summary>
    [Fact]
    public void A_different_piece_of_writing_inside_another_box_is_kept()
    {
        var one = Column("機んでくれて", new Rect(100, 50, 107, 203));
        var other = Column("構わない", new Rect(110, 60, 37, 117));

        Assert.Equal(2, VerticalRepeatedColumns.Drop([one, other]).Count);
    }

    /// <summary>
    /// The copy that goes is the one holding LESS of the writing, whichever box is smaller.
    /// </summary>
    /// <remarks>
    /// 比べて is framed inside a box 65x253 that came back holding only て, so deciding on the box
    /// would keep the て and lose two characters of the sentence — which is what the reader saw as
    /// <c>て比べて劣るのは当然だろ</c>.
    /// </remarks>
    [Fact]
    public void The_copy_holding_less_of_the_writing_is_the_one_that_goes()
    {
        var poor = Column("て", new Rect(1514, 138, 65, 253));
        var better = Column("比べて", new Rect(1544, 135, 36, 63));

        Assert.Equal(["比べて"], VerticalRepeatedColumns.Drop([poor, better]).Select(c => c.Text));
    }

    /// <summary>Boxes standing apart are two pieces of writing however alike they read.</summary>
    [Fact]
    public void Two_boxes_that_do_not_lie_on_each_other_are_both_kept()
    {
        var one = Column("そうだろ", new Rect(100, 50, 40, 160));
        var other = Column("そうだろ", new Rect(900, 600, 40, 160));

        Assert.Equal(2, VerticalRepeatedColumns.Drop([one, other]).Count);
    }
}
