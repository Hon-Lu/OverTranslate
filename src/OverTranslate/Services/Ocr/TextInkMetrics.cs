using System.Drawing;
using System.Windows;
using SkiaSharp;

namespace OverTranslate.Services.Ocr;

// Visible height on a flat background, not an estimate of the font's nominal size.
internal static class TextInkMetrics
{
    internal static List<OcrTextBlock> Annotate(Bitmap bitmap, IReadOnlyList<OcrTextBlock> blocks)
    {
        using var pixels = OnnxOcrEngine.ConvertToSkBitmap(bitmap);
        return Annotate(pixels, blocks);
    }

    internal static List<OcrTextBlock> Annotate(SKBitmap pixels, IReadOnlyList<OcrTextBlock> blocks) =>
        blocks.Select(b => b with { LayoutInkHeight = Measure(pixels, b.LayoutBounds) }).ToList();

    internal static double? Measure(SKBitmap pixels, Rect box)
    {
        // Short strings have too much glyph-dependent variation to provide this evidence.
        if (box.Height < 6 || box.Width < box.Height * 8) return null;
        var left = Math.Max(0, (int)Math.Floor(box.Left));
        var top = Math.Max(0, (int)Math.Floor(box.Top));
        var right = Math.Min(pixels.Width, (int)Math.Ceiling(box.Right));
        var bottom = Math.Min(pixels.Height, (int)Math.Ceiling(box.Bottom));
        if (right <= left || bottom <= top) return null;
        var step = Math.Max(1, (right - left) / 512);
        var histogram = new int[4096];
        var total = 0;
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x += step)
            {
                var c = pixels.GetPixel(x, y);
                histogram[(c.Red >> 4) << 8 | (c.Green >> 4) << 4 | (c.Blue >> 4)]++;
                total++;
            }
        var mode = Array.IndexOf(histogram, histogram.Max());
        if (histogram[mode] < total * 0.45) return null;
        var red = (mode >> 8) * 16 + 8;
        var green = ((mode >> 4) & 15) * 16 + 8;
        var blue = (mode & 15) * 16 + 8;
        bool IsInk(SKColor c) => Math.Max(Math.Abs(c.Red - red),
            Math.Max(Math.Abs(c.Green - green), Math.Abs(c.Blue - blue))) > 40;
        var border = 0;
        var borderInk = 0;
        for (var x = left; x < right; x += step)
        {
            border += 2;
            if (IsInk(pixels.GetPixel(x, top))) borderInk++;
            if (IsInk(pixels.GetPixel(x, bottom - 1))) borderInk++;
        }
        if (borderInk > border * 0.1) return null;
        var rows = new int[bottom - top];
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x += step)
                if (IsInk(pixels.GetPixel(x, y))) rows[y - top]++;
        var peak = rows.Max();
        if (peak < 3) return null;
        var occupied = Enumerable.Range(0, rows.Length).Where(y => rows[y] > peak * 0.1).ToArray();
        if (occupied.Length == 0) return null;
        var height = occupied[^1] - occupied[0] + 1;
        return height >= 4 && height < box.Height * 0.95 ? height : null;
    }
}
