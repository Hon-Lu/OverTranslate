using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A block whose ink is set the other way from what the shape of its box says is suspected, and
/// read again by the column pipeline; every other block is left to the shape, as it was. See
/// Ocr.Manga.MangaWritingDirection and MangaPageLayout.Suspect / ColumnsAgree.
/// </summary>
public class MangaWritingDirectionTests
{
    private const int PageWidth = 700, PageHeight = 500;

    private sealed class Page
    {
        public readonly byte[] Pixels = Enumerable.Repeat((byte)255, PageWidth * PageHeight).ToArray();

        public LumaPage Luma => new(Pixels, PageWidth, PageHeight);

        /// <summary>A square glyph, hatched as strokes are.</summary>
        public void Glyph(int left, int top, int size)
        {
            for (int y = top; y < top + size; y++)
                for (int x = left; x < left + size; x++)
                    if ((x + y) % 3 == 0) Pixels[y * PageWidth + x] = 0;
        }

        /// <summary>A line across of <paramref name="count"/> glyphs, a tenth of a glyph apart.</summary>
        public void Across(int left, int top, int count, int size)
        {
            for (int i = 0; i < count; i++) Glyph(left + i * (size + size / 10), top, size);
        }

        /// <summary>A column down of <paramref name="count"/> glyphs, a tenth of a glyph apart.</summary>
        public void Down(int left, int top, int count, int size)
        {
            for (int i = 0; i < count; i++) Glyph(left, top + i * (size + size / 10), size);
        }
    }

    [Fact]
    public void ACaptionOfLinesAcross_InABoxTooTallToBeARow_IsSuspectedAcross()
    {
        // vertical-image-ja5's caption: nine lines in a box 1.3 times as wide as it is tall.
        var page = new Page();
        for (int line = 0; line < 7; line++) page.Across(110, 110 + line * 30, 18, 20);
        var box = RectangleF.FromLTRB(100, 100, 510, 410);

        Assert.False(MangaPageLayout.RunsAcross(box));
        Assert.Equal(MangaWriting.Across, MangaPageLayout.Suspect(box, page.Luma));
    }

    [Theory]
    [InlineData(150)] // 2.73 times as wide as tall: a row, by its shape
    [InlineData(190)] // 2.16: columns by its shape, but wide enough to have been squeezed
    public void ABalloonOfShortColumns_WiderThanTwiceItsHeight_IsSuspectedDown(int height)
    {
        // vertical-image-ja5's balloon: sixteen short columns, captured at 2.48–2.88.
        var page = new Page();
        for (int column = 0; column < 12; column++) page.Down(110 + column * 32, 110, 5, 20);
        var box = new RectangleF(100, 100, 410, height);

        Assert.Equal(MangaWriting.Down, MangaPageLayout.Suspect(box, page.Luma));
    }

    [Fact]
    public void AnOrdinaryBalloonOfColumns_IsLeftToItsShape()
    {
        // Narrower than twice its height: manga-ocr reads it, and nothing is asked again.
        var page = new Page();
        for (int column = 0; column < 4; column++) page.Down(110 + column * 32, 110, 7, 20);
        var box = RectangleF.FromLTRB(100, 100, 340, 270);

        Assert.Equal(MangaWriting.Unsure, MangaPageLayout.Suspect(box, page.Luma));
    }

    [Fact]
    public void ColumnsWhoseCharactersLineUp_AreNotLinesAcross()
    {
        // A grid of glyphs: the rows of paper between the characters are too thin to be between lines.
        var page = new Page();
        for (int column = 0; column < 5; column++) page.Down(110 + column * 32, 110, 7, 20);
        var box = RectangleF.FromLTRB(100, 100, 280, 270);

        Assert.Equal(MangaWriting.Unsure, MangaPageLayout.Suspect(box, page.Luma));
    }

    [Fact]
    public void ASingleColumnWithGapsDownIt_IsNotLinesAcross()
    {
        // 「え…？…」 down one column: the gaps make rows of paper, but each "line" is one glyph long.
        var page = new Page();
        for (int glyph = 0; glyph < 4; glyph++) page.Glyph(110, 110 + glyph * 50, 20);
        var box = RectangleF.FromLTRB(100, 100, 150, 300);

        Assert.Equal(MangaWriting.Unsure, MangaPageLayout.Suspect(box, page.Luma));
    }

    [Fact]
    public void ARowOfSeveralLines_WideEnoughToBeARow_IsLeftToItsShape()
    {
        // Already a row by its shape: MangaTextRows cuts it into its lines, as before.
        var page = new Page();
        for (int line = 0; line < 3; line++) page.Across(110, 110 + line * 30, 25, 20);
        var box = RectangleF.FromLTRB(100, 100, 660, 200);

        Assert.True(MangaPageLayout.RunsAcross(box));
        Assert.Equal(MangaWriting.Unsure, MangaPageLayout.Suspect(box, page.Luma));
    }

    [Fact]
    public void ABlankBox_IsNotSuspected() =>
        Assert.Equal(MangaWriting.Unsure, MangaPageLayout.Suspect(new RectangleF(100, 100, 300, 120), new Page().Luma));

    private static OcrTextBlock Read(string text, bool across) =>
        new(text, new System.Windows.Rect(0, 0, 10, 10)) { RunsAcross = across };

    [Fact]
    public void TheColumnPipelineSettlesIt_ByWhichWayMostOfWhatItReadRuns()
    {
        OcrTextBlock[] caption = [Read("戸野田", true), Read("優（22）", true), Read("特に特徴のない某Ｆラン大社会学部卒。", true)];
        OcrTextBlock[] columns = [Read("もっと厚化粧で派手な印象だったが…", false), Read("ぶ", true)];

        Assert.True(MangaPageLayout.ColumnsAgree(MangaWriting.Across, caption));
        Assert.False(MangaPageLayout.ColumnsAgree(MangaWriting.Down, caption));
        Assert.True(MangaPageLayout.ColumnsAgree(MangaWriting.Down, columns));
        Assert.False(MangaPageLayout.ColumnsAgree(MangaWriting.Across, columns));
        Assert.False(MangaPageLayout.ColumnsAgree(MangaWriting.Across, []));
        Assert.False(MangaPageLayout.ColumnsAgree(MangaWriting.Unsure, caption));
    }
}
