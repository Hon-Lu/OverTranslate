using System.Drawing;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Ocr.Manga;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// A manga block is drawn tilted only when its ink and the column detector both say it is, and by
/// about the same angle; and the detector is never handed more than the area that keeps the step
/// inside its time. See Ocr.Manga.MangaColumnAngles.
/// </summary>
public class MangaColumnAnglesTests
{
    private const int PageWidth = 600, PageHeight = 700;

    /// <summary>
    /// A white page with two columns of black square glyphs leaning <paramref name="deviation"/>
    /// from vertical (negative: foot to the right), and the block round them.
    /// </summary>
    private static (LumaPage Luma, MangaBlock Block) Page(double deviation, double left = 260, double top = 120)
    {
        var pixels = Enumerable.Repeat((byte)255, PageWidth * PageHeight).ToArray();
        double radians = -deviation * Math.PI / 180;
        double dx = Math.Sin(radians), dy = Math.Cos(radians);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int column = 0; column < 2; column++)
        {
            for (int glyph = 0; glyph < 10; glyph++)
            {
                // Right column first, as Japanese reads; the left one 40px across from it.
                double cx = left + 60 - column * 40 * dy + glyph * 30 * dx;
                double cy = top + 12 + column * 40 * dx + glyph * 30 * dy;
                for (int y = (int)cy - 11; y <= (int)cy + 11; y++)
                    for (int x = (int)cx - 11; x <= (int)cx + 11; x++)
                        if (x >= 0 && y >= 0 && x < PageWidth && y < PageHeight && (x + y) % 3 != 0)
                            pixels[y * PageWidth + x] = 0;
                minX = Math.Min(minX, (float)cx - 12); maxX = Math.Max(maxX, (float)cx + 12);
                minY = Math.Min(minY, (float)cy - 12); maxY = Math.Max(maxY, (float)cy + 12);
            }
        }
        return (new LumaPage(pixels, PageWidth, PageHeight),
            new MangaBlock(RectangleF.FromLTRB(minX, minY, maxX, maxY), "二つの列で書かれた文", 0.9));
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(0)]
    [InlineData(11)]
    public void TheInkFindsTheColumnsLean(double deviation)
    {
        var (luma, block) = Page(deviation);

        Assert.Equal(deviation, MangaColumnAngles.InkAngle(luma, block.Bounds), tolerance: 1);
    }

    /// <summary>
    /// The detector stand-in: two columns through the middle of the mosaic, turned
    /// <paramref name="deviation"/> from vertical. The only candidate fills the mosaic.
    /// </summary>
    private static Func<Bitmap, IReadOnlyList<SKPointI[]>> Detector(double deviation, List<Size> seen)
    {
        return mosaic =>
        {
            seen.Add(mosaic.Size);
            double angle = 90 + deviation;
            if (angle > 90) angle -= 180;
            var line = new OcrLineGeometry(mosaic.Height * 0.8, mosaic.Height * 0.8 / 6, angle);
            return [.. new[] { -0.15, 0.15 }.Select(offset =>
            {
                var centre = new System.Windows.Point(mosaic.Width * (0.5 + offset), mosaic.Height / 2.0);
                return TiltedColumns.Quad(new System.Windows.Rect(centre, centre), line)
                    .Select(p => new SKPointI((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
            })];
        };
    }

    [Fact]
    public void ABlockBothSourcesCallTiltedIsDrawnAtTheDetectorsAngle()
    {
        var (luma, block) = Page(-6);
        var groups = MangaPageLayout.Assemble([block], [], [], luma);
        var seen = new List<Size>();

        var tilted = MangaColumnAngles.Apply(groups, [block], luma, Detector(-6.5, seen));

        var tilt = Assert.IsType<TiltedText>(Assert.Single(tilted).Tilt);
        Assert.True(tilt.Column);
        Assert.Equal(-6.5, tilt.Degrees, tolerance: 0.6);
        Assert.Equal(2, tilt.LineQuads.Count);
        // Only the tilt is added.
        Assert.Equal(groups[0].Text, tilted[0].Text);
        Assert.Equal(groups[0].Bounds, tilted[0].Bounds);
        Assert.Single(seen);
    }

    [Fact]
    public void TheTwoSourcesHaveToAgree()
    {
        var (luma, block) = Page(-6);
        var groups = MangaPageLayout.Assemble([block], [], [], luma);

        // The detector says −10° against the ink's −6°: more than two apart.
        var tilted = MangaColumnAngles.Apply(groups, [block], luma, Detector(-10, []));

        Assert.Same(groups, tilted);
        Assert.Null(tilted[0].Tilt);
    }

    [Fact]
    public void AStraightPageIsNotDetectedAtAll()
    {
        var (luma, block) = Page(0);
        var groups = MangaPageLayout.Assemble([block], [], [], luma);
        var seen = new List<Size>();

        var tilted = MangaColumnAngles.Apply(groups, [block], luma, Detector(-6, seen));

        Assert.Same(groups, tilted);
        Assert.Empty(seen);
    }

    [Fact]
    public void TheDetectorIsNeverHandedMoreThanTheCap()
    {
        // Eight big tilted blocks: at the full reduction they come to far more than the cap.
        var blocks = new List<MangaBlock>();
        LumaPage? luma = null;
        var pixels = Enumerable.Repeat((byte)255, PageWidth * PageHeight).ToArray();
        for (int i = 0; i < 8; i++)
        {
            var (one, block) = Page(-8, left: 10 + (i % 4) * 140, top: 20 + (i / 4) * 340);
            for (int y = 0; y < PageHeight; y++)
                for (int x = 0; x < PageWidth; x++)
                    if (one[x, y] == 0) pixels[y * PageWidth + x] = 0;
            blocks.Add(block);
        }
        luma = new LumaPage(pixels, PageWidth, PageHeight);
        var groups = MangaPageLayout.Assemble(blocks, [], [], luma);
        var seen = new List<Size>();

        MangaColumnAngles.Apply(groups, blocks, luma, Detector(-8, seen));

        var size = Assert.Single(seen);
        Assert.InRange(size.Width * size.Height, 1, MangaColumnAngles.MaxArea);
    }
}
