using SkiaSharp;
using TextBox = RapidOcrNet.TextBox;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Puts back a ―― in the middle of a horizontal line that the recogniser read straight past.
/// </summary>
/// <remarks>
/// <para>MEASURED on the capture in #398 (でもあんたは――いや、いまのなしっ, a 20px glyph on a white
/// dialogue box): the detector's box holds the whole line, dash included, and the recogniser gives
/// the dash's columns to the blank class at 0.95-1.00 — the reading is もあんたはいや、 with nothing
/// between は and い. Thicker (up to 6px), cut in two halves, or shortened to one ―, it still reads
/// nothing; the model does not know a thin bar between two words as a character. Nothing that
/// changes the crop gets it back, so it is put back by its shape, as <see cref="HorizontalLineEnds"/>
/// already does for a ―― at either end of a line.</para>
///
/// <para>The pixels say where a bar is; only the recogniser can say it read nothing there. So the
/// line is read again in two halves, cut at the bar, in the same batch as the line itself, and the
/// dash goes in only where the two halves read exactly the whole line between them — the left half
/// what the line starts with, the right half the rest. A bar the recogniser did read (一, ー, a -)
/// is in the line's reading and in neither half, and the halves no longer add up to it.</para>
///
/// <para>The bar has to be what <see cref="HorizontalLineEnds.IsDash"/> calls a dash: one stroke at
/// least one and a half glyphs long, no taller than a quarter of one, in the middle of the line's
/// glyphs, alone in its columns (a strike-through crosses the glyphs above and below it), and with
/// the line's ink on both sides of it. A single ― a glyph long is not taken, for the reason IsDash
/// gives: that stroke is as likely 一 or ー.</para>
/// </remarks>
internal static class HorizontalInnerDashes
{
    /// <summary>Set false to measure the shipped behaviour without this (OcrHarness only).</summary>
    internal static bool Enabled = true;

    /// <summary>What each search found, for OcrHarness. Null in the app.</summary>
    internal static Action<string>? Trace;

    /// <summary>
    /// Each dash lying between two of the line's glyphs inside <paramref name="box"/>, in the
    /// image's pixels, left to right. Empty when there is none, which is nearly every line.
    /// </summary>
    internal static IReadOnlyList<SKRectI> Find(SKBitmap image, SKRectI box)
    {
        var left = Math.Max(0, box.Left);
        var right = Math.Min(image.Width, box.Right);
        var top = Math.Max(0, box.Top);
        var bottom = Math.Min(image.Height, box.Bottom);
        var width = right - left;
        var height = bottom - top;
        if (height < 10 || width < 4 || height > width * 2.5) return [];
        if (!HorizontalLineEnds.Colour.Of(image, left, top, right, bottom, out var ink)) return [];

        // The line's glyph band: the longest run of rows carrying its ink. The longest, not the
        // first to the last, because the box's padding often holds the foot of the line above —
        // in #398 it is 5 rows of it over an 18px line, and a band that took them in would make the
        // glyph a third taller than it is and the dash too short for one.
        var rowFloor = Math.Max(2, width / 50);
        var inked = new bool[height];
        for (var y = top; y < bottom; y++)
        {
            var count = 0;
            for (var x = left; x < right && count < rowFloor; x++)
                if (ink.Is(image.GetPixel(x, y))) count++;
            inked[y - top] = count >= rowFloor;
        }

        int bandTop = -1, bandBottom = -1;
        for (var i = 0; i < height;)
        {
            if (!inked[i]) { i++; continue; }
            var start = i;
            var end = i + 1;
            var j = i + 1;
            // One blank row inside a line is a gap between strokes, not the end of the line.
            while (j < height && (inked[j] || j + 1 < height && inked[j + 1]))
            {
                if (inked[j]) end = j + 1;
                j++;
            }
            if (end - start > bandBottom - bandTop)
                (bandTop, bandBottom) = (top + start, top + end);
            i = j;
        }

        var glyph = bandBottom - bandTop;
        if (bandTop < 0 || glyph < 8 || width < glyph * 3) return [];
        var centre = (bandTop + bandBottom) / 2.0;
        var reach = glyph / 6;
        var found = new List<SKRectI>();

        for (var y = (int)Math.Floor(centre - reach); y <= (int)Math.Ceiling(centre + reach); y++)
        {
            if (y < top || y >= bottom) continue;
            var x = left;
            while (x < right)
            {
                if (!ink.Is(image.GetPixel(x, y))) { x++; continue; }
                var start = x;
                while (x < right && ink.Is(image.GetPixel(x, y))) x++;
                if (x - start < glyph * 1.5) continue;
                if (found.Any(f => f.Left < x && start < f.Right)) continue;

                if (Bar(image, ink, start, y, bandTop, bandBottom, glyph, centre) is not { } bar) continue;
                if (!HasInk(image, ink, left, bar.Left, bandTop, bandBottom) ||
                    !HasInk(image, ink, bar.Right, right, bandTop, bandBottom))
                {
                    Trace?.Invoke($"box {left},{top}-{right},{bottom}: bar {bar} at the line's end");
                    continue;
                }

                Trace?.Invoke($"box {left},{top}-{right},{bottom}: dash {bar} band {bandTop}-{bandBottom}");
                found.Add(bar);
            }
        }

        found.Sort((a, b) => a.Left.CompareTo(b.Left));
        return found;
    }

    // The connected ink through (x, y), if it is a dash and nothing else of the line's ink shares
    // its columns inside the band.
    private static SKRectI? Bar(
        SKBitmap image, HorizontalLineEnds.Colour ink, int x, int y, int bandTop, int bandBottom, int glyph,
        double centre)
    {
        var wTop = Math.Max(0, bandTop - glyph / 2);
        var wBottom = Math.Min(image.Height, bandBottom + glyph / 2);
        var w = image.Width;
        var seen = new HashSet<int>();
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));
        seen.Add(y * w + x);
        int minX = x, maxX = x, minY = y, maxY = y;
        while (stack.Count > 0)
        {
            var (px, py) = stack.Pop();
            minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
            // A dash is a few rows tall; anything that spreads this far is a picture or a rule.
            if (seen.Count > glyph * glyph * 4) return null;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || nx >= w || ny < wTop || ny >= wBottom) continue;
                    if (seen.Contains(ny * w + nx) || !ink.Is(image.GetPixel(nx, ny))) continue;
                    seen.Add(ny * w + nx);
                    stack.Push((nx, ny));
                }
        }

        var shape = new SKRectI(minX, minY, maxX + 1, maxY + 1);
        if (!HorizontalLineEnds.IsDash(shape, centre, glyph)) return null;

        // Alone in its columns: a strike-through or an underline joined to the glyphs it runs
        // under has other ink of the line above or below it.
        for (var cx = shape.Left; cx < shape.Right; cx++)
            for (var cy = bandTop; cy < bandBottom; cy++)
                if (!seen.Contains(cy * w + cx) && ink.Is(image.GetPixel(cx, cy)))
                    return null;
        return shape;
    }

    private static bool HasInk(SKBitmap image, HorizontalLineEnds.Colour ink, int from, int to, int bandTop, int bandBottom)
    {
        for (var x = Math.Max(0, from); x < Math.Min(image.Width, to); x++)
            for (var y = bandTop; y < bandBottom; y++)
                if (ink.Is(image.GetPixel(x, y))) return true;
        return false;
    }

    /// <summary>
    /// The detector-space box cut at a source-space x: the part left of it, or the part right of it.
    /// </summary>
    internal static TextBox Cut(TextBox box, int x, bool keepLeft)
    {
        var p = box.BoxPoints;
        // Corners in the library's order: top-left, top-right, bottom-right, bottom-left.
        var top = At(p[0], p[1], x);
        var bottom = At(p[3], p[2], x);
        return new TextBox
        {
            Score = box.Score,
            BoxPoints = keepLeft ? [p[0], top, bottom, p[3]] : [top, p[1], p[2], bottom],
        };

        static SKPointI At(SKPointI from, SKPointI to, int x)
        {
            var dx = to.X - from.X;
            var y = dx == 0 ? from.Y : from.Y + (double)(to.Y - from.Y) * (x - from.X) / dx;
            return new SKPointI(x, (int)Math.Round(y));
        }
    }

    /// <summary>
    /// The line's reading with ―― put in at each dash whose two halves read the whole line between
    /// them, or null when there is none to put in.
    /// </summary>
    /// <param name="halves">For each dash, left to right: what the part before it and the part after it read.</param>
    internal static (string[] Chars, float[] Scores)? WithDashes(
        string[]? chars, float[]? scores, IReadOnlyList<(string Before, string After)> halves)
    {
        if (chars is not { Length: > 0 } || scores is null || chars.Length != scores.Length) return null;
        var line = string.Concat(chars);

        var at = new List<int>();
        foreach (var (before, after) in halves)
        {
            // Both halves read something, and between them exactly the line: the recogniser read
            // nothing where the bar is.
            if (before.Length == 0 || after.Length == 0 || before.Length + after.Length != line.Length ||
                !line.StartsWith(before, StringComparison.Ordinal) || !line.EndsWith(after, StringComparison.Ordinal))
            {
                Trace?.Invoke($"halves \"{before}\" + \"{after}\" are not \"{line}\"");
                continue;
            }

            // The character the left half ends on.
            int index = 0, length = 0;
            while (index < chars.Length && length < before.Length) length += chars[index++].Length;
            if (length != before.Length || at.Contains(index)) continue;
            // Already a dash either side: the recogniser has this one.
            if (IsDashLike(chars[index - 1]) || IsDashLike(chars[index])) continue;
            at.Add(index);
        }
        if (at.Count == 0) return null;

        var words = scores.Average();
        var newChars = chars.ToList();
        var newScores = scores.ToList();
        foreach (var index in at.OrderByDescending(i => i))
        {
            newChars.InsertRange(index, ["―", "―"]);
            newScores.InsertRange(index, [words, words]);
        }
        Trace?.Invoke($"dash put in \"{string.Concat(newChars)}\"");
        return ([.. newChars], [.. newScores]);

        static bool IsDashLike(string c) => c is "―" or "—" or "－" or "-" or "ー" or "一" or "～" or "~" or "‐" or "─" or "━";
    }
}
