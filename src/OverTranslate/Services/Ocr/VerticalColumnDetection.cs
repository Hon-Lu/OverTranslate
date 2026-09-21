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

            // The slack the detector left around this box's own ink, so that a part which is as
            // long as the box comes back exactly as tall as the box was. Grouping compares a
            // column's length against its neighbours', and a part measured tight beside a box
            // measured loose is a difference in how they were cut, not in what they say.
            var boxRows = InkRows(image, left, right, top, bottom, mode);

            var edges = new[] { 0 }.Concat(cuts).Append(width).ToArray();
            var parts = new List<TextBox>();
            var ambiguous = false;
            for (var i = 0; i < edges.Length - 1; i++)
            {
                var a = edges[i];
                var b = edges[i + 1];
                var occupied = Enumerable.Range(a, b - a).Where(x => ink[x] > height * 0.015).ToArray();
                if (occupied.Length == 0) continue;
                var band = WidestBand(occupied, ink);
                a = Math.Max(a, band.First - 2);
                b = Math.Min(b, band.Last + 3);
                var (partTop, partBottom) = PartRows(
                    image, left + a, left + b, top, bottom, mode, boxRows);
                // Ignore a sliver already covered by another native column detection.
                if (boxes.Any(other => !ReferenceEquals(other, box) &&
                    Overlap(other, left + a, partTop, left + b, partBottom) > 0.65)) continue;
                // Do not silently discard a narrow punctuation/ruby fragment when splitting.
                if (occupied.Length < 8)
                {
                    ambiguous = true;
                    break;
                }
                parts.Add(new TextBox
                {
                    Score = box.Score,
                    BoxPoints = [new(left + a, partTop), new(left + b, partTop),
                        new(left + b, partBottom), new(left + a, partBottom)],
                });
            }
            if (!ambiguous && parts.Count >= 2) result.AddRange(parts);
            else result.Add(box);
        }
        return result;
    }

    /// <summary>
    /// The one run of ink a split part is really about, of the runs a blank gutter separates.
    /// </summary>
    /// <remarks>
    /// A part is cut between gutters, but not every gutter is allowed to be a cut — one within
    /// eight pixels of the box's edge is refused, because cutting there would leave a sliver rather
    /// than a column. The ink past that refused gutter still ends up inside the part, and it is not
    /// this part's writing.
    ///
    /// MEASURED on .ai/test-images/vertical-image-ja2/2026-09-20 19 14 59.png. The quad around
    /// お姉… and its reading ねえ is cut once; the right-hand part comes out 25 wide, holding the
    /// reading's 11 pixels of ink, a 7 pixel gutter, and then 6 pixels of the balloon's own outline
    /// running the full height of the box. That outline is what stops the part being trimmed down
    /// the page — every row has ink in it — so the reading keeps the box's full 112 rows, measures
    /// bigger per character than the sentence it annotates, and is merged into it as ねえお姉….
    ///
    /// A real column's ink has no gutter through it: its glyphs are stacked on one another and
    /// share an x range, so the profile along x is their union. Anything a blank band separates
    /// from the body of the ink is something else that happened to be inside the cut.
    /// </remarks>
    private static (int First, int Last) WidestBand(int[] occupied, int[] ink)
    {
        var best = (First: occupied[0], Last: occupied[0], Width: 0, Weight: 0L);
        var start = occupied[0];
        var weight = 0L;
        for (var i = 0; i < occupied.Length; i++)
        {
            weight += ink[occupied[i]];
            if (i + 1 < occupied.Length && occupied[i + 1] - occupied[i] <= 2) continue;

            // Widest rather than darkest: what is being rejected is a rule drawn down the side of
            // the cut, and a rule is a couple of pixels across and as tall as the box, so it can
            // hold more ink than the characters do. What it cannot be is as WIDE as they are — a
            // column is as wide as its glyphs whatever they are.
            var width = occupied[i] - start + 1;
            if (width > best.Width || (width == best.Width && weight > best.Weight))
                best = (start, occupied[i], width, weight);
            if (i + 1 < occupied.Length) start = occupied[i + 1];
            weight = 0;
        }

        return (best.First, best.Last);
    }

    /// <summary>Where a split part's own ink starts and stops down the page.</summary>
    /// <remarks>
    /// <para>The cut is made across the box, so without this every part comes out the full height
    /// of the box it came from — and a part's height is what says how big its characters are.
    /// MEASURED on <c>.ai/test-images/vertical-image-ja2/2026-09-20 19 14 59.png</c>: the quad
    /// around お姉… and its reading ねえ splits into a 30-wide part and a 25-wide part, both 112
    /// tall. The reading is two characters of ruby about 27px tall, so at 112 it measures 56px to
    /// the character against the sentence's 37, and <see cref="VerticalRubyColumns"/> — which is
    /// asking exactly this question — reads it as a column of its own and merges ねえ into the
    /// sentence. Trimmed to its ink it measures 13, and is dropped as the reading it is.</para>
    ///
    /// <para>The margin matches the one the horizontal trim takes, for the same reason: the
    /// threshold finds the strokes, not the antialiasing around them, and a crop that starts on the
    /// first dark pixel cuts into the glyph.</para>
    /// </remarks>
    private static (int First, int Last) InkRows(
        SKBitmap image, int left, int right, int top, int bottom, int mode)
    {
        var first = -1;
        var last = -1;
        for (var y = top; y < bottom; y++)
        {
            var found = false;
            for (var x = left; x < right && !found; x++)
                found = Math.Abs(Luminance(image.GetPixel(x, y)) - mode) > 48;
            if (!found) continue;
            if (first < 0) first = y;
            last = y;
        }

        return first < 0 ? (top, bottom - 1) : (first, last);
    }

    /// <summary>A split part's box, down the page, carrying the box's own slack.</summary>
    private static (int Top, int Bottom) PartRows(
        SKBitmap image, int left, int right, int top, int bottom, int mode,
        (int First, int Last) boxRows)
    {
        var (first, last) = InkRows(image, left, right, top, bottom, mode);
        var above = boxRows.First - top;
        var below = bottom - 1 - boxRows.Last;
        return (Math.Max(top, first - above), Math.Min(bottom, last + 1 + below));
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
