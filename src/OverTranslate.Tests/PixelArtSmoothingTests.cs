using System;
using OverTranslate.Services.Ocr;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What the detector is handed for a retro game shown at an integer scale: the same picture
/// wherever the user's box starts, and nothing different for anything that is not pixel art.
/// </summary>
/// <remarks>
/// No inference here, as in <see cref="DetectorFrameGeometryTests"/>: what the detector reads off
/// these pictures was measured with OcrHarness, and these lock the properties that measurement
/// depends on.
/// </remarks>
public class PixelArtSmoothingTests
{
    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(3, 1, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(4, 3, 1)]
    [InlineData(6, 5, 0)]
    public void FindsTheBlockSizeAndWhereTheGridStarts(int block, int cutX, int cutY)
    {
        using var full = Upscaled(NativePicture(160, 144, seed: 7), block);
        using var cut = Crop(full, cutX, cutY, full.Width - cutX - 1, full.Height - cutY - 1);

        var grid = PixelArtSmoothing.Find(cut);

        Assert.NotNull(grid);
        Assert.Equal(block, grid.Value.BlockX);
        Assert.Equal(block, grid.Value.BlockY);
        // A block edge sits at every multiple of the block size in the full picture; cutting cutX
        // pixels off the left moves them all to -cutX.
        Assert.Equal(((block - cutX) % block + block) % block, grid.Value.PhaseX);
        Assert.Equal(((block - cutY) % block + block) % block, grid.Value.PhaseY);
    }

    // The bug this exists for: the same Game Boy screen framed 2px inside was read as four fragments.
    // After the redraw, where the box starts no longer changes what the detector sees.
    [Theory]
    [InlineData(4, 2, 2)]
    [InlineData(4, 1, 3)]
    [InlineData(3, 1, 1)]
    [InlineData(3, 2, 0)]
    [InlineData(2, 1, 1)]
    public void TheRedrawDoesNotDependOnWhereTheBoxStarts(int block, int cutX, int cutY)
    {
        using var full = Upscaled(NativePicture(160, 144, seed: 11), block);
        using var cut = Crop(full, cutX, cutY, full.Width - cutX - 2, full.Height - cutY - 2);

        using var whole = PixelArtSmoothing.ForDetection(full);
        using var part = PixelArtSmoothing.ForDetection(cut);

        Assert.NotNull(whole);
        Assert.NotNull(part);
        Assert.Equal(cut.Width, part.Width);
        Assert.Equal(cut.Height, part.Height);

        // Away from the edges, where the cut picture has no neighbour to interpolate towards.
        var worst = 0;
        for (var y = block; y < part.Height - block; y++)
        for (var x = block; x < part.Width - block; x++)
        {
            var a = part.GetPixel(x, y);
            var b = whole.GetPixel(x + cutX, y + cutY);
            worst = Math.Max(worst, Math.Max(Math.Abs(a.Red - b.Red),
                Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue))));
        }

        // Skia's bilinear filter weighs in sixteenths of a pixel, so at 3x, where a third has no
        // exact binary form, a sample can land one sixteenth over: 15 levels across the palette's
        // widest step. The hard step the detector tripped over is 240.
        Assert.InRange(worst, 0, 16);
    }

    [Fact]
    public void ThePictureIsRedrawnSmoothAtTheSameSize()
    {
        using var full = Upscaled(NativePicture(40, 30, seed: 3), 4);
        using var smooth = PixelArtSmoothing.ForDetection(full);

        Assert.NotNull(smooth);
        Assert.Equal(full.Width, smooth.Width);
        Assert.Equal(full.Height, smooth.Height);
        // The hard steps between blocks are gone, which is what the detector was tripping over.
        Assert.NotNull(PixelArtSmoothing.Find(full));
        Assert.Null(PixelArtSmoothing.Find(smooth));
    }

    // Native-resolution content of every kind the corpora hold is left alone: anything with steps
    // between neighbouring pixels at more than one position per block.
    [Fact]
    public void LeavesAPictureThatIsNotPixelArtAlone()
    {
        using var native = NativePicture(640, 360, seed: 5);
        Assert.Null(PixelArtSmoothing.Find(native));
        Assert.Null(PixelArtSmoothing.ForDetection(native));
    }

    [Fact]
    public void LeavesAFlatPictureWithAFewEdgesAlone()
    {
        // Two vertical edges a multiple of eight apart would fold into one residue of every block
        // size; too few distinct edges is not evidence of a grid.
        using var bitmap = new SKBitmap(400, 200, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(new SKRect(96, 40, 160, 160), paint);
        }

        Assert.Null(PixelArtSmoothing.Find(bitmap));
    }

    // Random blocks of a few colours, like a game screen at its own resolution.
    private static SKBitmap NativePicture(int width, int height, int seed)
    {
        var random = new Random(seed);
        SKColor[] palette = [new(15, 56, 15), new(48, 98, 48), new(139, 172, 15), new(155, 188, 15), SKColors.White];
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            bitmap.SetPixel(x, y, palette[random.Next(palette.Length)]);
        return bitmap;
    }

    private static SKBitmap Upscaled(SKBitmap native, int block)
    {
        using (native)
        {
            var bitmap = new SKBitmap(native.Width * block, native.Height * block, SKColorType.Bgra8888, SKAlphaType.Premul);
            for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, native.GetPixel(x / block, y / block));
            return bitmap;
        }
    }

    private static SKBitmap Crop(SKBitmap source, int x, int y, int width, int height)
    {
        using var subset = new SKBitmap();
        Assert.True(source.ExtractSubset(subset, new SKRectI(x, y, x + width, y + height)));
        return subset.Copy();
    }
}
