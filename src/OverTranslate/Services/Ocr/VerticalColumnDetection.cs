using SkiaSharp;
using TextBox = RapidOcrNet.TextBox;

namespace OverTranslate.Services.Ocr;

/// <summary>Separates columns swallowed by one detector quad on a flat background.</summary>
internal static class VerticalColumnDetection
{
    internal static IReadOnlyList<TextBox> Split(SKBitmap image, IReadOnlyList<TextBox> boxes)
    {
        var result = new List<TextBox>();
        foreach (var box in boxes)
        {
            var p = box.BoxPoints;
            var left = Math.Max(0, p.Min(v => v.X));
            var right = Math.Min(image.Width, p.Max(v => v.X));
            var top = Math.Max(0, p.Min(v => v.Y));
            var bottom = Math.Min(image.Height, p.Max(v => v.Y));
            var width = right - left;
            var height = bottom - top;
            // Projection requires an upright column. Retain perspective quads intact.
            if (width < 16 || height < width * 1.5 ||
                Math.Abs(p[0].X - p[3].X) > 3 || Math.Abs(p[1].X - p[2].X) > 3 ||
                Math.Abs(p[0].Y - p[1].Y) > 3 || Math.Abs(p[3].Y - p[2].Y) > 3)
            {
                result.Add(box);
                continue;
            }

            var histogram = new int[256];
            for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++)
                    histogram[Luminance(image.GetPixel(x, y)) / 16 * 16]++;
            var mode = Array.IndexOf(histogram, histogram.Max()) + 8;
            if (histogram.Max() < width * height * 0.6)
            {
                result.Add(box);
                continue;
            }

            var ink = new int[width];
            for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++)
                    if (Math.Abs(Luminance(image.GetPixel(x, y)) - mode) > 48)
                        ink[x - left]++;

            // A column boundary must be a continuous blank gutter, not an internal stroke gap.
            var gutters = new List<(int Start, int End)>();
            for (var x = 0; x < width; x++)
            {
                if (ink[x] > height * 0.015) continue;
                var start = x;
                while (x + 1 < width && ink[x + 1] <= height * 0.015) x++;
                if (x - start + 1 >= 2) gutters.Add((start, x + 1));
            }
            var cuts = gutters.Where(g => g.Start >= 8 && width - g.End >= 8)
                .Select(g => (g.Start + g.End) / 2).ToList();
            if (cuts.Count == 0)
            {
                result.Add(box);
                continue;
            }

            var edges = new[] { 0 }.Concat(cuts).Append(width).ToArray();
            var parts = new List<TextBox>();
            var ambiguous = false;
            for (var i = 0; i < edges.Length - 1; i++)
            {
                var a = edges[i];
                var b = edges[i + 1];
                var occupied = Enumerable.Range(a, b - a).Where(x => ink[x] > height * 0.015).ToArray();
                if (occupied.Length == 0) continue;
                a = Math.Max(a, occupied[0] - 2);
                b = Math.Min(b, occupied[^1] + 3);
                // Ignore a sliver already covered by another native column detection.
                if (boxes.Any(other => !ReferenceEquals(other, box) &&
                    Overlap(other, left + a, top, left + b, bottom) > 0.65)) continue;
                // Do not silently discard a narrow punctuation/ruby fragment when splitting.
                if (occupied.Length < 8)
                {
                    ambiguous = true;
                    break;
                }
                parts.Add(new TextBox
                {
                    Score = box.Score,
                    BoxPoints = [new(left + a, top), new(left + b, top),
                        new(left + b, bottom), new(left + a, bottom)],
                });
            }
            if (!ambiguous && parts.Count >= 2) result.AddRange(parts);
            else result.Add(box);
        }
        return result;
    }

    private static int Luminance(SKColor c) => (c.Red * 77 + c.Green * 150 + c.Blue * 29) >> 8;

    private static double Overlap(TextBox box, int left, int top, int right, int bottom)
    {
        var p = box.BoxPoints;
        var w = Math.Max(0, Math.Min(right, p.Max(v => v.X)) - Math.Max(left, p.Min(v => v.X)));
        var h = Math.Max(0, Math.Min(bottom, p.Max(v => v.Y)) - Math.Max(top, p.Min(v => v.Y)));
        return (double)w * h / ((right - left) * (bottom - top));
    }
}
