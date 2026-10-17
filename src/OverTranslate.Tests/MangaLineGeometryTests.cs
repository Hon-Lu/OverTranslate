using System.Drawing;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Ocr.Manga;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Which way a manga block's lines run, from the column detector's lines, and which reader that
/// sends it to. See Ocr.Manga.MangaLineGeometry and MangaVerticalReader.ReaderFor.
/// </summary>
public class MangaLineGeometryTests
{
    private static SKPointI[] Quad(int left, int top, int right, int bottom) =>
        [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];

    [Fact]
    public void AQuad_IsMeasuredAlongItself_WhicheverCornerItStartsAt()
    {
        Assert.Equal((100.0, 20.0, true), MangaLineGeometry.Measure(Quad(0, 0, 100, 20)));
        Assert.Equal((100.0, 20.0, false), MangaLineGeometry.Measure(Quad(0, 0, 20, 100)));
        // The same column, its corners listed from the bottom left.
        Assert.Equal((100.0, 20.0, false), MangaLineGeometry.Measure([new(0, 100), new(0, 0), new(20, 0), new(20, 100)]));
    }

    [Fact]
    public void TheBlockGoesTheWayMostOfTheLengthOfItsLongLinesGoes()
    {
        Assert.Equal(MangaLineDirection.Across, MangaLineGeometry.Vote([(200, 20, true), (200, 20, true), (60, 20, false)], 1));
        Assert.Equal(MangaLineDirection.Down, MangaLineGeometry.Vote([(120, 20, false), (120, 20, false), (100, 20, true)], 1));
    }

    [Fact]
    public void LinesTooShortOrTooThinToSay_LeaveItToTheShape()
    {
        // One or two characters: not 1.6 times as long as thick.
        Assert.Equal(MangaLineDirection.Unsure, MangaLineGeometry.Vote([(30, 20, true), (25, 20, false)], 1));
        // Long, but under two pixels thick where it was found.
        Assert.Equal(MangaLineDirection.Unsure, MangaLineGeometry.Vote([(200, 20, true)], 0.05));
        Assert.Equal(MangaLineDirection.Unsure, MangaLineGeometry.Vote([], 1));
        // As long across as down.
        Assert.Equal(MangaLineDirection.Unsure, MangaLineGeometry.Vote([(100, 20, true), (100, 20, false)], 1));
    }

    [Fact]
    public void BlocksTallerThanWide_AreNotAsked()
    {
        using var page = new Bitmap(400, 400);
        int calls = 0;
        var directions = MangaLineGeometry.Directions(
            page, [new RectangleF(10, 10, 60, 200), new RectangleF(100, 10, 99, 100)], _ => { calls++; return []; });

        Assert.All(directions, direction => Assert.Equal(MangaLineDirection.Unsure, direction));
        Assert.Equal(0, calls);
    }

    [Theory]
    [MemberData(nameof(AwkwardBlocks))]
    public void AwkwardBlocks_AreUnsureAndNeverThrow(RectangleF block)
    {
        using var page = Page(300, 200, _ => { });
        var directions = MangaLineGeometry.Directions(page, [block], ComponentsAsQuads);

        Assert.Equal(MangaLineDirection.Unsure, Assert.Single(directions));
    }

    public static TheoryData<RectangleF> AwkwardBlocks => new()
    {
        new RectangleF(10, 10, 200, 100),           // blank
        new RectangleF(10, 10, 2, 1),               // two pixels
        new RectangleF(250, 150, 400, 300),         // mostly off the page
        new RectangleF(500, 500, 100, 50),          // wholly off it
        new RectangleF(-80, -80, 60, 40),           // off it the other way
        new RectangleF(10, 10, 0, 0),
        new RectangleF(float.NaN, 10, 100, 50),
    };

    [Fact]
    public void WhateverTheDetectorHandsBack_NothingThrows()
    {
        using var page = Page(300, 200, _ => { });
        var blocks = new[] { new RectangleF(10, 10, 200, 80), new RectangleF(10, 100, 150, 80) };

        IReadOnlyList<SKPointI[]> Junk(Bitmap mosaic) =>
        [
            [],
            [new(0, 0)],
            Quad(5, 5, 5, 5),
            Quad(-50, -50, -10, -10),
            Quad(0, 0, mosaic.Width * 3, mosaic.Height * 3),
        ];

        var directions = MangaLineGeometry.Directions(page, blocks, Junk);
        Assert.Equal(2, directions.Length);
        Assert.Empty(MangaLineGeometry.Directions(page, [], Junk));
    }

    [Fact]
    public void RowsInABoxTooNarrowForARow_GoToTheColumnPipeline_SetAcross()
    {
        // A caption: four lines across in a box 1.5 times as wide as tall, a row by its lines and
        // a block of columns by its shape.
        using var page = Page(800, 600, g =>
        {
            for (int line = 0; line < 4; line++) g.FillRectangle(Brushes.Black, 60, 60 + line * 40, 280, 22);
        });
        var caption = RectangleF.FromLTRB(50, 50, 350, 250);

        Assert.False(MangaPageLayout.RunsAcross(caption));
        Assert.Equal(MangaReader.ColumnsAcross, Assert.Single(MangaVerticalReader.Readers(page, [caption], ComponentsAsQuads)));
    }

    [Fact]
    public void ColumnsInABoxWideEnoughForARow_GoToTheColumnPipeline_SetDown()
    {
        // vertical-image-ja5's balloon: many short columns in a box nearly three times as wide as tall.
        using var page = Page(800, 600, g =>
        {
            for (int column = 0; column < 12; column++) g.FillRectangle(Brushes.Black, 60 + column * 36, 60, 22, 120);
        });
        var balloon = RectangleF.FromLTRB(50, 50, 500, 200);

        Assert.True(MangaPageLayout.RunsAcross(balloon));
        Assert.Equal(MangaReader.ColumnsDown, Assert.Single(MangaVerticalReader.Readers(page, [balloon], ComponentsAsQuads)));
    }

    [Fact]
    public void BlocksWhoseLinesAndShapeAgree_StayWithManga()
    {
        using var page = Page(900, 600, g =>
        {
            // Columns in a box of columns, a line across in a box that is a row.
            for (int column = 0; column < 4; column++) g.FillRectangle(Brushes.Black, 60 + column * 36, 60, 22, 200);
            g.FillRectangle(Brushes.Black, 300, 400, 500, 30);
        });
        RectangleF[] blocks = [RectangleF.FromLTRB(50, 50, 210, 270), RectangleF.FromLTRB(290, 375, 810, 455)];

        Assert.All(MangaVerticalReader.Readers(page, blocks, ComponentsAsQuads), reader => Assert.Equal(MangaReader.Manga, reader));
        Assert.All(MangaVerticalReader.Readers(page, blocks, null), reader => Assert.Equal(MangaReader.Manga, reader));
    }

    [Theory]
    // vertical-image-ja5's balloon framed at 2.48: a block of columns by its shape, but too many for manga-ocr.
    [InlineData(464, 187, (int)MangaLineDirection.Down, (int)MangaReader.ColumnsDown)]
    [InlineData(464, 161, (int)MangaLineDirection.Down, (int)MangaReader.ColumnsDown)]   // 2.88, a row by its shape
    [InlineData(400, 200, (int)MangaLineDirection.Down, (int)MangaReader.ColumnsDown)]   // exactly WideColumnsRatio
    [InlineData(157, 100, (int)MangaLineDirection.Down, (int)MangaReader.Manga)]         // 1.57, the widest it reads right
    [InlineData(464, 187, (int)MangaLineDirection.Unsure, (int)MangaReader.Manga)]       // the lines say nothing
    [InlineData(495, 381, (int)MangaLineDirection.Across, (int)MangaReader.ColumnsAcross)] // ja5's caption, 1.30
    [InlineData(600, 100, (int)MangaLineDirection.Across, (int)MangaReader.Manga)]       // a row, and its box says so
    [InlineData(60, 500, (int)MangaLineDirection.Unsure, (int)MangaReader.Columns)]      // too long, whatever its lines
    [InlineData(60, 200, (int)MangaLineDirection.Unsure, (int)MangaReader.Manga)]
    public void WhatMangaOcrCanRead_IsDecidedByTheBoxAndItsLines(
        float width, float height, int lines, int expected) =>
        Assert.Equal((MangaReader)expected, MangaVerticalReader.ReaderFor(new RectangleF(10, 10, width, height), (MangaLineDirection)lines));

    [Fact]
    public void WhileTheLinesAreMeasured_WholeBatchesOfBlocksTheyCannotChangeAreRead()
    {
        var tall = new RectangleF(0, 0, 60, 200);
        var wide = new RectangleF(0, 0, 300, 100);

        // Five that are manga-ocr's whatever, one that is asked: the first four make a whole batch.
        Assert.Equal([0, 1, 3, 4], MangaVerticalReader.ReadAhead([tall, tall, wide, tall, tall, tall]));
        // Not enough for a whole batch: reading them early would add one.
        Assert.Empty(MangaVerticalReader.ReadAhead([tall, tall, wide, tall]));
        // Nothing asked, so nothing to wait for.
        Assert.Empty(MangaVerticalReader.ReadAhead([tall, tall, tall, tall, tall]));
    }

    [Fact]
    public void BlocksSentAcross_AreReadByMangaOcrAsWell()
    {
        MangaReader[] readers =
            [MangaReader.Manga, MangaReader.ColumnsAcross, MangaReader.ColumnsDown, MangaReader.Columns, MangaReader.Manga];

        Assert.Equal([0, 1, 4], MangaVerticalReader.MangaReads(readers, []));
        // What was read ahead is not read again.
        Assert.Equal([1, 4], MangaVerticalReader.MangaReads(readers, [0]));
    }

    [Fact]
    public void AVerticalBalloonTheLinesSentAcross_IsMangaOcrsAfterAll()
    {
        // The reported balloon (兄が流れ弾から俺を庇って死んだ。) as the column pipeline read it:
        // columns, one dropped, and the furigana of 兄 on its own.
        var groups = Groups("俺を庇って", "死んだ。", "流れ弾から", "あに");

        Assert.False(MangaVerticalReader.ReadsAcross(groups));
        Assert.False(MangaVerticalReader.TakesColumns(MangaReader.ColumnsAcross, groups));
    }

    [Theory]
    [InlineData("IT'S NOT")]
    [InlineData("WHAT ARE / YOU DOING?")]
    [InlineData("OPEN ドア")]        // Latin for two characters of three
    public void EnglishInABoxSentAcross_StaysAcross(string text)
    {
        var groups = Groups(text.Split(" / "));

        Assert.True(MangaVerticalReader.ReadsAcross(groups));
        Assert.True(MangaVerticalReader.TakesColumns(MangaReader.ColumnsAcross, groups));
    }

    [Theory]
    [InlineData("I")]                // a letter or two found in kana
    [InlineData("OK")]
    [InlineData("あいOKう")]
    [InlineData("abcあいうえ")]       // three letters, but fewer than half
    [InlineData("")]
    public void ALetterOrTwo_OrMostlyKana_DoesNotTurnABlockAcross(string text) =>
        Assert.False(MangaVerticalReader.ReadsAcross(Groups(text)));

    [Theory]
    [InlineData((int)MangaReader.Columns)]
    [InlineData((int)MangaReader.ColumnsDown)]
    public void LongAndWideBlocks_KeepTheColumnPipelinesReadingWhateverItSays(int reader) =>
        Assert.True(MangaVerticalReader.TakesColumns((MangaReader)reader, Groups("俺を庇って", "死んだ。")));

    private static List<Services.OcrTextBlock> Groups(params string[] texts) =>
        [.. texts.Select((text, i) => new Services.OcrTextBlock(text, new System.Windows.Rect(i * 50, 0, 40, 150)))];

    [Fact]
    public void TheColumnEnginesSlots_AreTakenAndGivenBackOnceEach()
    {
        using var engine = new OnnxOcrEngine();
        var slots = new List<IOcrSlot>();
        while (((IOcrEngine)engine).TryTakeSlot() is { } slot) slots.Add(slot);

        Assert.Equal(OnnxOcrEngine.ConcurrentRecognitions, slots.Count);
        Assert.Null(((IOcrEngine)engine).TryTakeSlot());

        slots[0].Dispose();
        slots[0].Dispose(); // a second dispose must not give back a slot it does not hold
        var again = ((IOcrEngine)engine).TryTakeSlot();
        Assert.NotNull(again);
        Assert.Null(((IOcrEngine)engine).TryTakeSlot());

        again!.Dispose();
        foreach (var slot in slots) slot.Dispose();
    }

    private static Bitmap Page(int width, int height, Action<Graphics> draw)
    {
        var page = new Bitmap(width, height);
        using var g = Graphics.FromImage(page);
        g.Clear(Color.White);
        draw(g);
        return page;
    }

    /// <summary>
    /// A stand-in for the column detector: each connected patch of dark pixels, boxed.
    /// </summary>
    private static IReadOnlyList<SKPointI[]> ComponentsAsQuads(Bitmap mosaic)
    {
        int w = mosaic.Width, h = mosaic.Height;
        var dark = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                dark[y * w + x] = mosaic.GetPixel(x, y).GetBrightness() < 0.5f;

        var seen = new bool[w * h];
        var quads = new List<SKPointI[]>();
        var stack = new Stack<int>();
        for (int start = 0; start < w * h; start++)
        {
            if (!dark[start] || seen[start]) continue;
            int left = w, top = h, right = 0, bottom = 0;
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop(), x = p % w, y = p / w;
                left = Math.Min(left, x); right = Math.Max(right, x + 1);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
                foreach (var q in new[] { p - 1, p + 1, p - w, p + w })
                {
                    if (q < 0 || q >= w * h || seen[q] || !dark[q]) continue;
                    if ((q == p - 1 && x == 0) || (q == p + 1 && x == w - 1)) continue;
                    seen[q] = true;
                    stack.Push(q);
                }
            }

            quads.Add(Quad(left, top, right, bottom));
        }
        return quads;
    }
}
