using System.Drawing;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Where a block of columns is cut for CPT-flex, from a character map drawn by hand: one map cell to
/// a block pixel, the block at the page's top left corner on blank paper.
/// </summary>
public class MangaColumnCutsTests
{
    private const int Side = 400;
    private const int Glyph = 16;
    private const int Pitch = 20;

    // Columns of characters 16px a side every 20px down, at the given left edges and lengths.
    private static (float[] Region, float[] Affinity) Draw(params (int Left, int Count, int Size)[] columns)
    {
        var region = new float[Side * Side];
        var affinity = new float[Side * Side];
        foreach (var (left, count, size) in columns)
            for (int k = 0; k < count; k++)
                Fill(region, left, 10 + k * Pitch, size, size);
        return (region, affinity);
    }

    private static void Fill(float[] map, int x, int y, int w, int h)
    {
        for (int yy = y; yy < y + h; yy++)
        for (int xx = x; xx < x + w; xx++)
            map[yy * Side + xx] = 1;
    }

    private static MangaColumnCuts.Maps Maps((float[] Region, float[] Affinity) drawn) =>
        new(drawn.Region, drawn.Affinity, Side, Side, 1, new Size(Side, Side));

    private static LumaPage Paper(params Rectangle[] ink)
    {
        var pixels = new byte[Side * Side];
        Array.Fill(pixels, (byte)255);
        foreach (var r in ink)
            for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                pixels[y * Side + x] = 0;
        // Some ink inside the block, so that Otsu has two sides to part.
        for (int y = 10; y < 30; y++) pixels[y * Side + 1] = 0;
        return new LumaPage(pixels, Side, Side);
    }

    private static readonly RectangleF Block = new(0, 0, 200, 340);

    [Fact]
    public void FewCharacters_AreReadWhole_ButKnownToBeColumns()
    {
        var pieces = MangaColumnCuts.Pieces(Maps(Draw((20, 10, Glyph), (60, 10, Glyph), (100, 10, Glyph))), Block, Paper());

        Assert.Equal([Block], pieces);
    }

    [Fact]
    public void ManyColumns_AreCutBetweenThem_RightToLeft_AtMostThirtyCharactersAPiece()
    {
        // Four columns of about 15 characters: two pieces of about 30.
        var pieces = MangaColumnCuts.Pieces(
            Maps(Draw((20, 12, Glyph), (60, 12, Glyph), (100, 12, Glyph), (140, 12, Glyph))), Block, Paper());

        Assert.NotNull(pieces);
        Assert.Equal(2, pieces!.Count);
        Assert.Equal(Block.Right, pieces[0].Right);
        Assert.Equal(0, pieces[1].Left);
        // The cut is midway across the gap between the second and third columns.
        Assert.Equal(88, pieces[0].Left);
        Assert.Equal(pieces[0].Left, pieces[1].Right);
    }

    [Fact]
    public void Furigana_StaysWithTheColumnToItsLeft()
    {
        // Each column has furigana 6px wide just to its right; the cut must not part them.
        var drawn = Draw((20, 12, Glyph), (60, 12, Glyph), (100, 12, Glyph), (140, 12, Glyph));
        foreach (var left in new[] { 20, 60, 100, 140 })
            for (int k = 0; k < 12; k += 3)
                Fill(drawn.Region, left + Glyph + 3, 12 + k * Pitch, 6, 12);

        var pieces = MangaColumnCuts.Pieces(Maps(drawn), Block, Paper());

        Assert.NotNull(pieces);
        Assert.Equal(2, pieces!.Count);
        // Between the second column's furigana (ends at 85) and the third column (starts at 100).
        Assert.InRange(pieces[0].Left, 86, 99);
    }

    [Fact]
    public void ACutLineThroughInk_MovesToWhereTheInkIsLeast()
    {
        var drawn = Draw((20, 12, Glyph), (60, 12, Glyph), (100, 12, Glyph), (140, 12, Glyph));
        var unmoved = MangaColumnCuts.Pieces(Maps(drawn), Block, Paper())![0].Left;
        // A stroke of the page crosses the line the cut would take, the full height of the columns.
        var pieces = MangaColumnCuts.Pieces(Maps(drawn), Block, Paper(new Rectangle((int)unmoved - 1, 0, 3, 340)));

        Assert.NotNull(pieces);
        Assert.NotEqual(unmoved, pieces![0].Left);
        Assert.InRange(pieces[0].Left, 77, 99);
    }

    [Fact]
    public void InkAcrossTheWholeGap_IsStillCut_MidwayAcrossIt()
    {
        var drawn = Draw((20, 12, Glyph), (60, 12, Glyph), (100, 12, Glyph), (140, 12, Glyph));

        var pieces = MangaColumnCuts.Pieces(Maps(drawn), Block, Paper(new Rectangle(70, 0, 40, 340)));

        Assert.NotNull(pieces);
        Assert.Equal(88, pieces![0].Left);
    }

    [Fact]
    public void RowsJoinedAcross_AreNotColumns()
    {
        // Three rows of characters joined by links between them: words far wider than tall.
        var drawn = Draw();
        for (int row = 0; row < 3; row++)
        {
            for (int k = 0; k < 9; k++) Fill(drawn.Region, 10 + k * Pitch, 20 + row * 40, Glyph, Glyph);
            Fill(drawn.Affinity, 10, 24 + row * 40, 9 * Pitch, 8);
        }

        Assert.Null(MangaColumnCuts.Pieces(Maps(drawn), new RectangleF(0, 0, 200, 140), Paper()));
    }

    [Fact]
    public void ASingleColumn_IsNotTheseCutsToMake() =>
        Assert.Null(MangaColumnCuts.Pieces(Maps(Draw((20, 15, Glyph))), Block, Paper()));

    [Fact]
    public void LeaningColumns_AreNotCut()
    {
        var drawn = Draw();
        foreach (var left in new[] { 40, 90, 140 })
            for (int k = 0; k < 15; k++)
                Fill(drawn.Region, left - k * 2, 10 + k * Pitch, Glyph, Glyph);   // 2px across for every 20 down: 5.7°

        Assert.Null(MangaColumnCuts.Pieces(Maps(drawn), Block, Paper()));
    }

    [Fact]
    public void ABlockOfTooManyColumns_IsTheMangaModels_WhenItsColumnsWereFound()
    {
        MangaReader[] readers = [MangaReader.ColumnsDown, MangaReader.ColumnsDown, MangaReader.ColumnsAcross, MangaReader.Manga];
        IReadOnlyList<RectangleF>?[] pieces = [[Block], null, [Block], null];

        MangaVerticalReader.ReadColumnsCut(readers, pieces);

        Assert.Equal([MangaReader.Manga, MangaReader.ColumnsDown, MangaReader.ColumnsAcross, MangaReader.Manga], readers);
    }

    [Fact]
    public void AWideBlockReadAsColumns_IsSetDown()
    {
        // vertical-image-ja5's balloon, 2.55 times as wide as tall: a row by its shape alone.
        var balloon = new RectangleF(100, 100, 465, 182);

        var asColumns = MangaPageLayout.Assemble([new MangaBlock(balloon, "兄が流れ弾から", 0.9, Columns: true)], [], []);
        var byShape = MangaPageLayout.Assemble([new MangaBlock(balloon, "兄が流れ弾から", 0.9)], [], []);

        Assert.False(Assert.Single(asColumns).RunsAcross);
        Assert.True(Assert.Single(byShape).RunsAcross);
    }

    [Theory]
    [InlineData(130, 100, true)]
    [InlineData(129, 100, false)]
    [InlineData(100, 100, false)]
    [InlineData(100, 400, true)]    // a long narration of a few columns
    [InlineData(100, 399, false)]
    public void WideBlocks_AreAskedForTheirColumns(float width, float height, bool asked) =>
        Assert.Equal(asked, MangaColumnCuts.Asks(new RectangleF(0, 0, width, height)));
}
