using SkiaSharp;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Rejoins a row of text that the detector returned in pieces, before anything crops from those
/// pieces.
/// </summary>
/// <remarks>
/// <para>The case this exists for is Japanese text on a flat dark background — a page title set in
/// colour, or the grey body text under it. The detector's probability map fades across the thin
/// strokes, so the row comes back as several boxes with gaps between them, and the glyphs in the
/// gaps are never recognised at all —
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
/// it does nothing on its own — and every condition after it is measured on one row: a row being
/// the boxes that share height with the widest one on their line, cut where their ink stops, each
/// stretch then having to be wide, continuous, substantial, and to hold coloured ink that no box
/// covers. Across 500 captures that changes six: three rows read whole that used to come back in
/// pieces, one exclamation mark that comes back full-width as the page has it, one frame of noise a
/// subtitle dump should not have read at all whose noise changes shape, and one repository header
/// where a name and the badge a pixel beside it come back without the space between them.</para>
///
/// <para>Framing is the thing to measure it on, because a user drags a different rectangle every
/// time. Over 144 framings of one dark-mode search result the title came back whole in 13% of them
/// before any of this and 89% after, with nothing that read whole before reading worse.</para>
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
        // Ink with a hue in it, but not a saturated graphic — the second half keeps an icon or a
        // logo out. This used to gate the whole row, which is why the rule only ever reached
        // coloured titles; it now gates the evidence alone, where it costs a real capture nothing.
        // Subpixel rendering leaves a hue on every stroke edge, so on the two captures a user sent
        // 40% of the ink reads as coloured, against 8-18% for the same pages rendered headless.
        // What it still excludes is flat theme furniture drawn in the neutral palette: bullets,
        // chevrons, panel borders, a hamburger. Those are what a row-agnostic version swallowed.
        bool Color(SKColor c)
        {
            var high = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
            var low = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
            return high - low >= 32 && low * 2 >= high;
        }
        var repairs = new List<Repair>();
        var claimed = new bool[boxes.Count];
        // A row is what the detector's own boxes say it is. The horizontal ink projection this used
        // to walk cannot see one on a search results page: a thumbnail beside the text puts ink on
        // every scanline of the whole result, so the projection returns a single 137px band where
        // the eye sees four rows, and the band then fails every test meant for a line of text.
        // Grouping by the height boxes share with the widest one on their line has no such blind
        // spot, and it is the same measurement the row used to be checked with afterwards.
        foreach (var seed in Enumerable.Range(0, boxes.Count).OrderByDescending(i => boxes[i].Width))
        {
            if (claimed[seed]) continue;
            var owners = Enumerable.Range(0, boxes.Count)
                .Where(i => !claimed[i] &&
                    Math.Min(boxes[i].Bottom, boxes[seed].Bottom) - Math.Max(boxes[i].Top, boxes[seed].Top)
                        >= Math.Min(boxes[i].Height, boxes[seed].Height) * .6f)
                .OrderBy(i => boxes[i].Left)
                .ToArray();
            // Claimed whether or not this row goes on to qualify. A box belongs to one line, and
            // letting a rejected row hand its boxes back would offer them to a narrower seed that
            // spans less of the line and knows less about it.
            foreach (var i in owners) claimed[i] = true;

            // Cut the line where its ink stops. A search result puts a thumbnail to the right of
            // the text, and the caption detected on it shares height with the title without
            // belonging to anything on that line — 200px of background between them. Refusing the
            // whole line over that gutter is what the continuity test used to do, and it cost the
            // title: across 144 framings of one capture the title came back whole 22% of the time.
            // Cutting keeps the part that really is one broken line and lets the stray box be a
            // line of its own, where it repairs nothing.
            foreach (var line in Split(source, boxes, owners, boxes[seed].Height, Ink))
            {
                var top = Math.Max(0, (int)Math.Floor(line.Min(i => boxes[i].Top)));
                var bottom = Math.Min(source.Height, (int)Math.Ceiling(line.Max(i => boxes[i].Bottom)));
                var boxLeft = Math.Max(0, (int)Math.Floor(line.Min(i => boxes[i].Left)));
                var boxRight = Math.Min(source.Width, (int)Math.Ceiling(line.Max(i => boxes[i].Right)));
                if (bottom - top < 8 || boxRight - boxLeft < 8) continue;

                // Which columns of the row carry ink. Over the row's own scanlines rather than the
                // whole capture, though the cost of either disappears into the inference beside it:
                // 28 captures read in 67.9s with this and 68.7s without, which is noise.
                var width = source.Width;
                var inked = new bool[width];
                var inkTop = bottom; var inkBottom = top; var ink = 0;
                for (var yy = top; yy < bottom; yy++)
                for (var x = 0; x < width; x++)
                {
                    if (!Ink(source.GetPixel(x, yy))) continue;
                    inked[x] = true;
                    if (x < boxLeft || x >= boxRight) continue;
                    ink++;
                    inkTop = Math.Min(inkTop, yy); inkBottom = Math.Max(inkBottom, yy + 1);
                }
                // The line's own scale, as before: the ink between the box edges, not the box heights,
                // which carry the detector's padding.
                var height = inkBottom - inkTop;
                if (height < 18) continue;

                // Glyphs the detector stopped short of sit outside the outermost box, so the row has to
                // reach past it — but only while the ink keeps going at the spacing of one line. A run
                // of blank wider than a line height is the end of the row, not a space inside it, which
                // is the same rule the continuity test below applies between the boxes. Three quarters
                // of that was not enough: the exclamation mark ending バンドリ！ガールズバンドパーティ！
                // sits 17px past the box on a 25px line, and stopping short of it dropped it.
                var reach = Math.Max(2, height);
                var left = boxLeft;
                for (var blank = 0; left > 0 && blank < reach; left--) blank = inked[left - 1] ? 0 : blank + 1;
                while (left < boxLeft && !inked[left]) left++;
                var right = boxRight;
                for (var blank = 0; right < width && blank < reach; right++) blank = inked[right] ? 0 : blank + 1;
                while (right > boxRight && !inked[right - 1]) right--;

                // A line of text, wide and not a filled panel: the last term rejects a row whose ink
                // covers most of its own area.
                if (right - left < height * 6 || ink > (boxRight - boxLeft) * height * .65) continue;
                // Something real was detected on this row. Without it, a row of ink the detector refused
                // outright — scenery, a logo, a progress bar — would be handed to recognition as text.
                if (line.Max(i => boxes[i].Width) < height * 2.5) continue;
                // The ink runs across the gaps. A row of separate items with real space between them has
                // a gap wider than a line height somewhere, and must not be glued into one box — which
                // is also what keeps a box on the far side of a page away from one it never belonged to.
                var gap = 0; var maxGap = 0;
                for (var x = left; x < right; x++) { gap = inked[x] ? 0 : gap + 1; maxGap = Math.Max(maxGap, gap); }
                if (maxGap > height) continue;

                // The evidence that something is actually missing: coloured ink inside the row that no
                // existing box covers, and enough of it to be glyphs rather than antialiasing. Dropping
                // the colour test here was measured and rejected — bullets, chevrons and a hamburger
                // got swallowed, and a chevron between a setting's label and its value glued the two
                // into one line.
                var missing = 0; var missingTop = bottom; var missingBottom = top;
                var missingLeft = right; var missingRight = left;
                for (var yy = top; yy < bottom; yy++)
                for (var x = left; x < right; x++)
                {
                    var c = source.GetPixel(x, yy);
                    if (!Ink(c) || !Color(c) || line.Any(i => boxes[i].Contains(x, yy))) continue;
                    missing++; missingLeft = Math.Min(missingLeft, x); missingRight = Math.Max(missingRight, x + 1);
                    missingTop = Math.Min(missingTop, yy); missingBottom = Math.Max(missingBottom, yy + 1);
                }
                // Tall enough to be a glyph rather than a rule or an underline — but only just. The
                // half-a-line-height this asked for before threw away the commonest missing glyph in
                // Japanese: the long vowel mark is a horizontal bar, 8px on a 20px line, and the row it
                // was found in read 公式ゲームト / ラ with the レー between them dropped entirely.
                if (missing < height || missingBottom - missingTop < height * .3
                    || missingRight - missingLeft < height * .2) continue;

                // Around the ink, not around the boxes. A detector box carries the library's own
                // padding, and a rectangle built from those edges reaches into the line above: on one
                // search result it overlapped the URL box by half a pixel, which the guard below reads
                // as absorbing another row and refuses — a title that used to be repaired stopped being.
                var margin = Math.Max(2, height * .1f);
                var rect = new SKRect(
                    Math.Max(0, left - margin), Math.Max(0, inkTop - margin),
                    Math.Min(source.Width, right + margin), Math.Min(source.Height, inkBottom + margin));
                // A repair may not absorb another row or an unrelated overlapping detector box.
                if (Enumerable.Range(0, boxes.Count).Any(i => !line.Contains(i) && SKRect.Intersect(rect, boxes[i]).Width > 0 && SKRect.Intersect(rect, boxes[i]).Height > 0)) continue;
                repairs.Add(new(line, rect));
            }
        }
        // Emitted in reading order, because Apply places each repair at its leftmost piece and the
        // seeds above are visited widest-first.
        repairs.Sort((a, b) => a.Owners.Min().CompareTo(b.Owners.Min()));
        return repairs;
    }

    /// <summary>
    /// One line's boxes cut into the stretches whose ink actually runs together.
    /// </summary>
    /// <remarks>
    /// <para>The gap between two pieces of a broken row holds the glyphs that were missed, so it is
    /// full of ink; the gap between a title and the caption on the thumbnail beside it is 200px of
    /// background. Cutting there, rather than refusing the row over it, is what keeps the part that
    /// really is one broken line — measured over 144 framings of one dark-mode search result, the
    /// title came back whole in 22% of them when the row was refused and 89% when it was cut.</para>
    ///
    /// <para>Cutting at gaps that hold nothing worth rescuing as well — a name and the badge one
    /// pixel beside it — was measured and rejected: detector boxes abut and overlap often enough
    /// that it cut 46 of those framings apart too, and the title fell back to 57%.</para>
    /// </remarks>
    private static List<int[]> Split(
        SKBitmap source, IReadOnlyList<SKRect> boxes, int[] owners, float scale, Func<SKColor, bool> ink)
    {
        var lines = new List<int[]>();
        var run = new List<int> { owners[0] };
        for (var k = 1; k < owners.Length; k++)
        {
            var previous = boxes[owners[k - 1]];
            var next = boxes[owners[k]];
            var top = Math.Max(0, (int)Math.Floor(Math.Min(previous.Top, next.Top)));
            var bottom = Math.Min(source.Height, (int)Math.Ceiling(Math.Max(previous.Bottom, next.Bottom)));
            var from = Math.Max(0, (int)Math.Ceiling(previous.Right));
            var to = Math.Min(source.Width, (int)Math.Floor(next.Left));
            var blank = 0; var widest = 0;
            for (var x = from; x < to; x++)
            {
                var any = false;
                for (var y = top; y < bottom && !any; y++) any = ink(source.GetPixel(x, y));
                blank = any ? 0 : blank + 1;
                widest = Math.Max(widest, blank);
            }
            if (widest > scale) { lines.Add([.. run]); run.Clear(); }
            run.Add(owners[k]);
        }
        lines.Add([.. run]);
        return lines;
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
