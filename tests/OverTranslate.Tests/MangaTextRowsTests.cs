using System.Drawing;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A block the manga detector set across is cut into the lines it was set in, so the overlays
/// draw it as a paragraph; and anything that may be one line stays one. See
/// Ocr.Manga.MangaTextRows.
/// </summary>
public class MangaTextRowsTests
{
    private const int PageWidth = 500, PageHeight = 300;

    private sealed class Page
    {
        public readonly byte[] Pixels = Enumerable.Repeat((byte)255, PageWidth * PageHeight).ToArray();

        public LumaPage Luma => new(Pixels, PageWidth, PageHeight);

        public void Fill(int left, int top, int right, int bottom, byte value)
        {
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                    Pixels[y * PageWidth + x] = value;
        }

        /// <summary>
        /// A line of <paramref name="count"/> square glyphs <paramref name="size"/> across, a tenth
        /// of a glyph apart, each hatched as strokes are.
        /// </summary>
        public void Line(int left, int top, int count, int size, byte ink = 0)
        {
            for (int glyph = 0; glyph < count; glyph++)
            {
                int x0 = left + glyph * (size + size / 10);
                for (int y = top; y < top + size; y++)
                    for (int x = x0; x < x0 + size; x++)
                        if ((x + y) % 3 == 0) Pixels[y * PageWidth + x] = ink;
            }
        }
    }

    private static MangaBlock Block(int left, int top, int right, int bottom, string text) =>
        new(RectangleF.FromLTRB(left, top, right, bottom), text, 0.9);

    [Fact]
    public void ACaptionOfThreeLines_WithFuriganaBetween_IsThreeLinesEdgeToEdge()
    {
        var page = new Page();
        for (int line = 0; line < 3; line++)
        {
            page.Line(110, 108 + line * 32, 12, 20);
            // Furigana over some of the line's characters, in the gap above it.
            if (line > 0) page.Line(150, 108 + line * 32 - 10, 8, 8);
        }
        var block = Block(100, 100, 380, 200, "ギルドカードに記憶させると迷宮の入り口から転移できる水晶各階層の入り口に設置されている");

        var result = Assert.Single(MangaPageLayout.Assemble([block], [], [], page.Luma));

        Assert.True(result.RunsAcross);
        var lines = Assert.IsAssignableFrom<IReadOnlyList<System.Windows.Rect>>(result.SourceLineBounds);
        Assert.Equal(3, lines.Count);
        Assert.Equal(100, lines[0].Top);
        Assert.Equal(200, lines[^1].Bottom);
        for (int i = 1; i < lines.Count; i++) Assert.Equal(lines[i - 1].Bottom, lines[i].Top);
        Assert.All(lines, line => Assert.Equal(new[] { 100.0, 280.0 }, new[] { line.Left, line.Width }));
        Assert.Equal(result.Bounds, lines.Aggregate(System.Windows.Rect.Union));
        Assert.True(result.RenderGlyphHeight <= lines.Min(line => line.Height));
    }

    [Fact]
    public void ANamePlateWithFuriganaOverIt_StaysOneLine_AsItWas()
    {
        var page = new Page();
        page.Line(150, 130, 4, 7);
        page.Line(110, 142, 10, 24);
        var block = Block(100, 120, 390, 178, "剣聖オリヴァー・カーディフ");

        var withPage = Assert.Single(MangaPageLayout.Assemble([block], [], [], page.Luma));
        var without = Assert.Single(MangaPageLayout.Assemble([block], [], []));

        Assert.True(withPage.RunsAcross);
        Assert.Null(withPage.SourceLineBounds);
        Assert.Equal(without, withPage);
    }

    [Fact]
    public void TwoLinesFarApart_AreTwo_NotMore()
    {
        var page = new Page();
        page.Line(110, 110, 10, 20);
        page.Line(110, 170, 10, 20);
        var block = Block(100, 100, 360, 200, "付与術士は迷宮の奥へオルン・ドゥーラ");

        var rows = MangaTextRows.Find(page.Luma, block.Bounds, 18);

        Assert.Equal(new[] { (110, 130), (170, 190) }, rows);
    }

    [Fact]
    public void ColumnsSetCharacterByCharacter_AreNotLines()
    {
        // Glyphs in a grid, two pixels apart down the page: columns, not rows of writing.
        var page = new Page();
        for (int row = 0; row < 4; row++) page.Line(110, 110 + row * 22, 10, 20);
        var block = Block(100, 100, 340, 205, "縦に並んだ文字をたくさん書いてある四十字ほどの塊");

        Assert.Empty(MangaTextRows.Find(page.Luma, block.Bounds, 40));
    }

    [Fact]
    public void ALineOnADarkStrip_WithThePageAboveAndBelowIt_IsOneLine()
    {
        // White letters on a black strip; the box reaches past the strip onto the light page.
        var page = new Page();
        page.Fill(100, 115, 400, 175, 0);
        page.Line(120, 135, 8, 20, ink: 255);
        var block = Block(100, 100, 400, 190, "第1話/おわり");

        Assert.Empty(MangaTextRows.Find(page.Luma, block.Bounds, 7));
    }

    [Fact]
    public void ALineCutByTheBoxEdge_IsNotCounted()
    {
        // The box starts inside a line of something above the caption: it may run on past the box.
        var page = new Page();
        page.Line(110, 95, 10, 20);
        page.Line(110, 140, 10, 20);
        var block = Block(100, 100, 360, 170, "見出しの下の一行だけの文");

        Assert.Empty(MangaTextRows.Find(page.Luma, block.Bounds, 12));
    }

    [Fact]
    public void TooFewCharactersForTheLines_IsOneLine()
    {
        var page = new Page();
        page.Line(110, 110, 3, 20);
        page.Line(110, 150, 3, 20);
        var block = Block(100, 100, 360, 180, "あい");

        Assert.Empty(MangaTextRows.Find(page.Luma, block.Bounds, 2));
    }
}
