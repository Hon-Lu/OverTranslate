using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// The second reading of the turned frame: where its findings land, and which of them are kept.
/// </summary>
public class TurnedFrameDetectionTests
{
    /// <summary>
    /// The turn and the mapping back are each other's inverse, checked on real pixels.
    /// </summary>
    /// <remarks>
    /// Worth doing on a bitmap rather than on arithmetic alone. Both directions are easy to write
    /// plausibly and backwards — a quarter turn has two of them — and the wrong one puts every
    /// recovered balloon on the opposite side of the page, which no test of the formula against
    /// itself would notice.
    /// </remarks>
    [Fact]
    public void A_mark_on_the_turned_frame_maps_back_to_where_it_started()
    {
        using var source = new Bitmap(80, 200);
        // Asymmetric on both axes, so a mapping that is right by luck cannot pass.
        var mark = new System.Drawing.Rectangle(10, 30, 6, 9);
        for (var x = mark.Left; x < mark.Right; x++)
            for (var y = mark.Top; y < mark.Bottom; y++)
                source.SetPixel(x, y, Color.Red);

        using var turned = TurnedFrameDetection.Turn(source);

        Assert.Equal(source.Height, turned.Width);
        Assert.Equal(source.Width, turned.Height);

        var found = FindRed(turned);
        var mappedBack = TurnedFrameDetection.ToUpright(found, source.Width);

        Assert.Equal(new Rect(mark.X, mark.Y, mark.Width, mark.Height), mappedBack);
    }

    [Fact]
    public void A_piece_the_upright_pass_already_read_is_not_taken_again()
    {
        var alreadyRead = new Rect(700, 150, 36, 180);
        // The same column as the turned frame sees it: 36x180 upright is 180x36 turned, and the
        // turn pivots on the upright width.
        var sameColumn = new Rect(150, 841 - (700 + 36), 180, 36);

        var missed = TurnedFrameDetection.PiecesTheUprightPassMissed(
            [alreadyRead], [sameColumn], sourceWidth: 841);

        Assert.Empty(missed);
    }

    [Fact]
    public void A_piece_the_upright_pass_never_read_is_taken()
    {
        var alreadyRead = new Rect(700, 150, 36, 180);
        var elsewhere = new Rect(600, 40, 190, 34);

        var missed = TurnedFrameDetection.PiecesTheUprightPassMissed(
            [alreadyRead], [elsewhere], sourceWidth: 841);

        Assert.Equal([0], missed);
    }

    /// <summary>
    /// A column the upright pass never read, under a box that holds the column beside it.
    /// </summary>
    /// <remarks>
    /// The case the overlap bar was raised for, with the figures off
    /// 2026-09-20 19 14 57.png. One detector quad is drawn around 正しい判断を, the reading beside
    /// it and the head of the balloon; the upright pass reads that quad as the middle column alone.
    /// So the head is covered — by a box whose text is not the head's — and a bar low enough to
    /// refuse anything half covered refuses the only reading of it there is.
    /// </remarks>
    [Fact]
    public void A_piece_under_the_box_of_a_column_that_was_read_as_something_else_is_taken()
    {
        var readAsTheColumnBeside = new Rect(1024, 986, 65, 177);
        // オレは, which shares 0.56 of itself with that box: 1069,995 36x84 upright.
        var head = new Rect(995, 715, 84, 36);

        var missed = TurnedFrameDetection.PiecesTheUprightPassMissed(
            [readAsTheColumnBeside], [head], sourceWidth: 1820);

        Assert.Equal([0], missed);
    }

    /// <summary>
    /// A speck the upright pass read inside a column cannot speak for the column.
    /// </summary>
    /// <remarks>
    /// The figures are off 2026-09-20 19 14 59.png. One detector quad covers the two columns
    /// パーティを組む and 資格なんてない！！, reads as nothing because it holds two columns at once,
    /// and the upright pass separately lands a 19x23 box on a single か inside the left one. That
    /// speck shares 0.79 of ITSELF with this column, so a test asked of the smaller rectangle
    /// refused the sentence to keep from doubling one character.
    /// </remarks>
    [Fact]
    public void A_speck_read_inside_a_column_does_not_stand_for_the_column()
    {
        var speck = new Rect(1083, 773, 19, 23);
        // パーティを組む at 1087,721 74x294 upright, which the turned frame holds at 721,660 294x74.
        var column = new Rect(721, 660, 294, 74);

        var missed = TurnedFrameDetection.PiecesTheUprightPassMissed(
            [speck], [column], sourceWidth: 1821);

        Assert.Equal([0], missed);
    }

    /// <summary>
    /// A column the upright pass read in pieces is still refused, because the pieces are weighed
    /// together.
    /// </summary>
    [Fact]
    public void A_column_the_upright_pass_read_in_fragments_is_not_taken_again()
    {
        var top = new Rect(1087, 721, 74, 150);
        var bottom = new Rect(1087, 871, 74, 144);
        var column = new Rect(721, 660, 294, 74);

        var missed = TurnedFrameDetection.PiecesTheUprightPassMissed(
            [top, bottom], [column], sourceWidth: 1821);

        Assert.Empty(missed);
    }

    /// <summary>
    /// A reading that repeats what the upright pass said in the same place is not added.
    /// </summary>
    /// <remarks>
    /// 2026-09-20 19 14 59 (2).png: the column 剣士…？ and its reading けんし are read upright as
    /// boxes 44 and 16 wide, and this pass finds the whole column as one box 102 wide because the
    /// balloon around it is empty. Those two cover 0.58 of it — and the balloon head this pass
    /// exists to recover is covered 0.56, so the rectangles cannot tell the two cases apart.
    /// </remarks>
    [Fact]
    public void A_reading_that_repeats_what_was_already_read_is_dropped()
    {
        var candidate = new OcrTextBlock("剣士…？", new Rect(1367, 847, 102, 208));
        var upright = new OcrTextBlock("剣士…？", new Rect(1389, 845, 44, 208));

        Assert.True(TurnedFrameDetection.SaysWhatWasAlreadyRead(candidate, [upright]));
    }

    [Fact]
    public void A_reading_of_different_words_in_the_same_place_is_kept()
    {
        // The オレは case: the box it lies under was read as the column beside it.
        var candidate = new OcrTextBlock("オレは", new Rect(1069, 995, 36, 84));
        var upright = new OcrTextBlock("正しい判断を", new Rect(1024, 986, 65, 177));

        Assert.False(TurnedFrameDetection.SaysWhatWasAlreadyRead(candidate, [upright]));
    }

    /// <summary>A lone glyph shares itself with half the sentences on the page.</summary>
    [Fact]
    public void A_single_character_the_upright_pass_read_cannot_speak_for_a_sentence()
    {
        var candidate = new OcrTextBlock("パーティを組む", new Rect(1087, 721, 74, 294));
        var upright = new OcrTextBlock("を", new Rect(1083, 773, 19, 23));

        Assert.False(TurnedFrameDetection.SaysWhatWasAlreadyRead(candidate, [upright]));
    }

    /// <summary>Two turned boxes on the same place cannot both come through.</summary>
    [Fact]
    public void The_turned_passs_own_boxes_are_weighed_against_each_other_too()
    {
        var candidate = new Rect(600, 40, 190, 34);
        var nearlyTheSame = new Rect(604, 42, 186, 30);

        var missed = TurnedFrameDetection.PiecesTheUprightPassMissed(
            [], [candidate, nearlyTheSame], sourceWidth: 841);

        Assert.Equal([0], missed);
    }

    /// <summary>
    /// A lone glyph only this pass can see is the picture, not a balloon.
    /// </summary>
    /// <remarks>
    /// MEASURED over 24 comic pages at two sizes: every one of the nine real balloons this pass
    /// recovers holds two characters or more, and every single-character addition was a page number
    /// or a mark in the artwork.
    /// </remarks>
    [Theory]
    [InlineData("ああ次の探索の準備か", true)]
    [InlineData("とはいえ", true)]
    [InlineData("でも", true)]
    [InlineData("墨", false)]
    [InlineData("6", false)]
    [InlineData(" 子 ", false)]
    public void Only_a_piece_holding_more_than_one_glyph_is_added(string text, bool expected)
    {
        var block = new OcrTextBlock(text, new Rect(0, 0, 30, 30));

        Assert.Equal(expected, TurnedFrameDetection.WorthKeeping(block));
    }

    /// <summary>Everything a block measures in pixels moves with it, not just its coverage box.</summary>
    [Fact]
    public void Mapping_a_block_back_moves_its_layout_box_as_well()
    {
        var block = new OcrTextBlock(
            "ああ",
            new Rect(150, 100, 180, 36),
            SourceLineBounds: [new Rect(150, 100, 90, 36)])
        {
            LayoutBounds = new Rect(148, 98, 184, 40),
        };

        var upright = TurnedFrameDetection.ToUpright(block, sourceWidth: 841);

        Assert.Equal(TurnedFrameDetection.ToUpright(block.Bounds, 841), upright.Bounds);
        Assert.Equal(TurnedFrameDetection.ToUpright(block.LayoutBounds, 841), upright.LayoutBounds);
        // Refilled by the vertical grouping; carried over it would be in the turned frame still.
        Assert.Null(upright.SourceLineBounds);
    }

    private static Rect FindRed(Bitmap bitmap)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (var x = 0; x < bitmap.Width; x++)
            for (var y = 0; y < bitmap.Height; y++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R < 200 || pixel.G > 50 || pixel.B > 50) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }

        Assert.True(left < int.MaxValue, "the mark was not found on the turned frame at all");
        return new Rect(left, top, right - left, bottom - top);
    }
}
