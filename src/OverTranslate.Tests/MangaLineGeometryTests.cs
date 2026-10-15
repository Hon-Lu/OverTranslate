using System.Drawing;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Ocr.Manga;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Which way a manga block's lines run, from the column detector's lines, and which reader that
/// sends it to. See Ocr.Manga.MangaLineGeometry and MangaVerticalReader.Routes.
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
        Assert.Equal(true, Assert.Single(MangaVerticalReader.Routes(page, [caption], ComponentsAsQuads)));
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
        Assert.Equal(false, Assert.Single(MangaVerticalReader.Routes(page, [balloon], ComponentsAsQuads)));
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
        RectangleF[] blocks = [RectangleF.FromLTRB(50, 50, 210, 270), RectangleF.FromLTRB(290, 390, 810, 440)];

        Assert.All(MangaVerticalReader.Routes(page, blocks, ComponentsAsQuads), route => Assert.Null(route));
        Assert.All(MangaVerticalReader.Routes(page, blocks, null), route => Assert.Null(route));
    }

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
