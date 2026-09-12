using SkiaSharp;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Rejoins a row of coloured text that the detector returned in pieces, before anything crops from
/// those pieces.
/// </summary>
/// <remarks>
/// <para>The case this exists for is a Japanese page title set in colour on a flat dark background.
/// The detector's probability map fades across the thin coloured strokes, so the row comes back as
/// several boxes with gaps between them, and the glyphs in the gaps are never recognised at all —
/// <c>BanG Dream!</c> / <c>（バンドリ</c> / <c>J!)</c> / <c>）公式サイ</c> where the page reads
/// <c>BanG Dream!（バンドリ！）公式サイト</c>. Widening the recognition crop does not help: the
/// trailing <c>ト</c> peaks at about .015 on the probability map, far below the .2 box threshold,
/// so there is no box to widen. Reading the whole row as one box does read it correctly.</para>
///
/// <para>Three earlier attempts at this symptom were measured and rejected, and why each failed is
/// why this one is shaped the way it is. Recognising a second, greyscale pass and splicing its text
/// over the first could silently drop a low-confidence original. Converting the whole capture to
/// luminance before detection fired on 27 of 395 captures and changed 18 of them, because "flat
/// dark background with some colour in it" is the definition of every dark-mode web page. Switching
/// the detector to the official ImageNet normalisation changed 195 of 413. All three moved boxes
/// everywhere; this one changes nothing unless a row is actually broken.</para>
///
/// <para>So the decision is local. The whole-image test below is only a cheap pre-filter — passing
/// it does nothing on its own — and every condition after it is measured on one row of ink: the row
/// must be mostly coloured, wide, continuous, already carry a substantial detector box, and hold
/// coloured ink that no box covers. Across 413 captures exactly two rows satisfy all of them, and
/// both were broken titles.</para>
///
/// <para>Screenshot captures only. The realtime path passes an explicit detector size and never
/// reaches here: a frame missed there is repaired by the next one 250ms later, and none of this has
/// been measured against a realtime corpus.</para>
/// </remarks>
internal static class ChromaticBoxRepair
{
    /// <param name="Owners">Indices into the box list that the repaired rectangle replaces.</param>
    internal record Repair(int[] Owners, SKRect Bounds);

    /// <summary>
    /// The rows worth repairing, measured on the capture's own pixels in its own coordinates.
    /// </summary>
    internal static List<Repair> Find(SKBitmap source, IReadOnlyList<SKRect> boxes)
    {
        // The pre-filter: one large, flat, neutral, dark background colour. Strided, so it costs
        // about the same on a 4K capture as on a small one, and everything expensive is behind it.
        var histogram = new int[4096];
        var step = Math.Max(1, Math.Max(source.Width, source.Height) / 512);
        var samples = 0;
        for (var y = 0; y < source.Height; y += step)
        for (var x = 0; x < source.Width; x += step)
        {
            var c = source.GetPixel(x, y);
            histogram[(c.Red >> 4) * 256 + (c.Green >> 4) * 16 + (c.Blue >> 4)]++;
            samples++;
        }
        var mode = Array.IndexOf(histogram, histogram.Max());
        var bg = new SKColor((byte)((mode >> 8) * 16 + 8), (byte)(((mode >> 4) & 15) * 16 + 8), (byte)((mode & 15) * 16 + 8));
        var bgHigh = Math.Max(bg.Red, Math.Max(bg.Green, bg.Blue));
        if (histogram[mode] < samples * .6 || bgHigh > 96 || bgHigh - Math.Min(bg.Red, Math.Min(bg.Green, bg.Blue)) > 32) return [];
        bool Ink(SKColor c) => Math.Max(Math.Abs(c.Red - bg.Red), Math.Max(Math.Abs(c.Green - bg.Green), Math.Abs(c.Blue - bg.Blue))) > 40;
        // Coloured, but not a saturated graphic: an icon or a logo is kept out by the second half.
        bool Color(SKColor c)
        {
            var high = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
            var low = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
            return high - low >= 32 && low * 2 >= high;
        }
        var counts = new int[source.Height];
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++) if (Ink(source.GetPixel(x, y))) counts[y]++;
        var repairs = new List<Repair>();
        for (var y = 0; y < source.Height; y++)
        {
            if (counts[y] < 4) continue;
            var top = y;
            while (y < source.Height && counts[y] >= 4) y++;
            var bottom = y;
            var height = bottom - top;
            if (height < 18) continue;
            var ink = 0; var color = 0; var left = source.Width; var right = 0;
            var occupied = new bool[source.Width];
            for (var yy = top; yy < bottom; yy++)
            for (var x = 0; x < source.Width; x++)
            {
                var c = source.GetPixel(x, yy);
                if (!Ink(c)) continue;
                ink++; if (Color(c)) color++;
                occupied[x] = true;
                left = Math.Min(left, x); right = Math.Max(right, x + 1);
            }
            // A line of text, mostly coloured, and not a filled panel: the last term rejects a row
            // whose ink covers most of its own area.
            if (right - left < height * 6 || color < ink * .6 || ink > (right - left) * height * .65) continue;
            var owners = Enumerable.Range(0, boxes.Count).Where(i =>
                Math.Min(boxes[i].Bottom, bottom) - Math.Max(boxes[i].Top, top) >= Math.Min(boxes[i].Height, height) * .6 &&
                boxes[i].Right > left && boxes[i].Left < right).OrderBy(i => boxes[i].Left).ToArray();
            if (owners.Length == 0 || owners.Any(i => repairs.Any(r => r.Owners.Contains(i)))) continue;
            // Something real was detected on this row. Without it, a row of ink the detector refused
            // outright — scenery, a logo, a progress bar — would be handed to recognition as text.
            if (owners.Max(i => boxes[i].Width) < height * 2.5) continue;
            // The ink runs across the gaps. A row of separate items with real space between them has
            // a gap wider than a line height somewhere, and must not be glued into one box.
            var gap = 0; var maxGap = 0;
            for (var x = left; x < right; x++) { gap = occupied[x] ? 0 : gap + 1; maxGap = Math.Max(maxGap, gap); }
            if (maxGap > height) continue;
            if (owners.Any(i => boxes[i].Top < top - height * .6 || boxes[i].Bottom > bottom + height * .6)) continue;
            // The evidence that something is actually missing: coloured ink inside the row that no
            // existing box covers, and enough of it to be glyphs rather than antialiasing.
            var missing = 0; var missingTop = bottom; var missingBottom = top; var missingLeft = right; var missingRight = left;
            for (var yy = top; yy < bottom; yy++)
            for (var x = left; x < right; x++)
            {
                var c = source.GetPixel(x, yy);
                if (!Ink(c) || !Color(c) || owners.Any(i => boxes[i].Contains(x, yy))) continue;
                missing++; missingLeft = Math.Min(missingLeft, x); missingRight = Math.Max(missingRight, x + 1);
                missingTop = Math.Min(missingTop, yy); missingBottom = Math.Max(missingBottom, yy + 1);
            }
            if (missing < height || missingBottom - missingTop < height * .5 || missingRight - missingLeft < height * .2) continue;
            var margin = Math.Max(2, height * .1f);
            var rect = new SKRect(Math.Max(0, Math.Min(left - margin, owners.Min(i => boxes[i].Left))),
                Math.Max(0, Math.Min(top - margin, owners.Min(i => boxes[i].Top))),
                Math.Min(source.Width, Math.Max(right + margin, owners.Max(i => boxes[i].Right))),
                Math.Min(source.Height, Math.Max(bottom + margin, owners.Max(i => boxes[i].Bottom))));
            // A repair may not absorb another row or an unrelated overlapping detector box.
            if (Enumerable.Range(0, boxes.Count).Any(i => !owners.Contains(i) && SKRect.Intersect(rect, boxes[i]).Width > 0 && SKRect.Intersect(rect, boxes[i]).Height > 0)) continue;
            repairs.Add(new(owners, rect));
        }
        return repairs;
    }

    /// <summary>
    /// The detector's boxes with each repaired row's pieces replaced by the single box spanning
    /// them, in the detector's own coordinates so recognition can crop from them directly.
    /// </summary>
    /// <remarks>
    /// Returns the list it was handed whenever nothing qualifies, which is the overwhelmingly common
    /// case and the one that has to stay free: no allocation, no reordering, no coordinate round
    /// trip. A box outside a repair comes back as the same instance in the same position.
    /// </remarks>
    internal static IReadOnlyList<RapidOcrNet.TextBox> Apply(
        SKBitmap source,
        IReadOnlyList<SKRect> sourceBounds,
        IReadOnlyList<RapidOcrNet.TextBox> detectorBoxes,
        double ratioX,
        double ratioY,
        int padding)
    {
        var repairs = Find(source, sourceBounds);
        if (repairs.Count == 0) return detectorBoxes;

        var result = new List<RapidOcrNet.TextBox>(detectorBoxes.Count);
        for (var i = 0; i < detectorBoxes.Count; i++)
        {
            var repair = repairs.FirstOrDefault(r => r.Owners.Contains(i));
            if (repair is null) { result.Add(detectorBoxes[i]); continue; }
            // Emitted once, in the place of its leftmost piece, so reading order does not move.
            if (i != repair.Owners.Min()) continue;

            // Back into detector space: the frame's own scale, then the padding the library added
            // around it. Floor and ceiling rather than rounding, because a repaired box landing a
            // pixel inside its own glyphs is the clipping this exists to stop.
            var left = (int)Math.Floor(repair.Bounds.Left * ratioX + padding);
            var right = (int)Math.Ceiling(repair.Bounds.Right * ratioX + padding);
            var top = (int)Math.Floor(repair.Bounds.Top * ratioY + padding);
            var bottom = (int)Math.Ceiling(repair.Bounds.Bottom * ratioY + padding);
            result.Add(new RapidOcrNet.TextBox
            {
                // The lowest of the pieces it replaces. A merged box is no more certain than the
                // least certain thing it was built from, and downstream filters read this.
                Score = repair.Owners.Min(id => detectorBoxes[id].Score),
                BoxPoints =
                [
                    new SKPointI(left, top),
                    new SKPointI(right, top),
                    new SKPointI(right, bottom),
                    new SKPointI(left, bottom),
                ],
            });
        }
        return result;
    }
}
