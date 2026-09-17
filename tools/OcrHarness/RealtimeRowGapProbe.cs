using System.Drawing;
using System.IO;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using SkiaSharp;

// Whether the realtime path shows the symptom ChromaticBoxRepair exists for: a row of text the
// detector returns in pieces, with the glyphs between the pieces recognised by nothing.
//
// The screenshot flow is the only one that repairs, and the repair's own remarks say why the live
// one was left out — "a frame missed there is repaired by the next one 250ms later". That is an
// argument about frames changing, not about geometry, so it says nothing about a subtitle that
// stands still for four seconds. This prints the geometry instead.
//
// For every row (the same grouping Find starts with: boxes that share height with the widest one
// on their line) it prints the pieces the realtime size detected, the gaps between them, and what
// the same row reads as when the whole strip is cropped out and read on its own. A row where the
// crop reads MORE characters than the pieces did is a row losing text between the boxes.
//
// The crop is a lower bound, not a verdict: it re-detects, so a row the detector breaks up again
// inside the crop reads no better and shows up here as no loss. It cannot report a loss that is
// not there, which is what a first pass has to get right.
internal static class RealtimeRowGapProbe
{
    internal static async Task<int> Run(string[] args)
    {
        var panel = args.Contains("--panel");
        args = [.. args.Where(argument => argument != "--panel")];
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --realtime-row-gaps [--panel] <lang> <image.png> [more.png ...]");
            return 1;
        }

        var language = args[0];
        var mode = panel ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
        using var engine = new OnnxOcrEngine();

        var rowsSeen = 0;
        var rowsSplit = 0;
        var rowsLosing = 0;
        var imagesLosing = 0;

        foreach (var path in args.Skip(1))
        {
            using var bitmap = new Bitmap(path);
            using var source = ToSkia(bitmap);
            var (primary, _) = RealtimeDetectorSize.For(bitmap.Width, bitmap.Height, mode);

            List<SKRect> boxes;
            string[] texts;
            // Unrepaired: this mode is about what the detector did on its own, and a session that
            // repaired on its way out would be showing rows that are already whole.
            using (var session = engine.BeginDetection(bitmap, language, primary, repairRows: false))
            {
                boxes = session.Boxes
                    .Select(box => new SKRect(
                        (float)box.Bounds.Left, (float)box.Bounds.Top,
                        (float)box.Bounds.Right, (float)box.Bounds.Bottom))
                    .ToList();
                texts = new string[boxes.Count];
                for (var i = 0; i < boxes.Count; i++)
                    texts[i] = string.Concat(session.Recognize([i]).Select(block => block.Text));
            }

            Console.WriteLine();
            Console.WriteLine("=".PadRight(78, '='));
            Console.WriteLine($"IMAGE: {Path.GetFileName(path)}  {bitmap.Width}x{bitmap.Height}  " +
                $"detect={primary}  mode={mode}  boxes={boxes.Count}");
            Console.WriteLine($"  dark-flat background pre-filter: {Background(source)}");

            var lost = false;
            foreach (var row in Rows(boxes).SelectMany(row => Split(boxes, row)))
            {
                rowsSeen++;
                var pieces = string.Concat(row.Select(i => texts[i]));
                if (row.Length < 2)
                {
                    Console.WriteLine($"  row y={boxes[row[0]].Top:0} x={boxes[row[0]].Left:0}..{boxes[row[0]].Right:0} " +
                        $"single box \"{pieces}\"");
                    continue;
                }

                rowsSplit++;
                var gaps = Enumerable.Range(1, row.Length - 1)
                    .Select(k => (int)(boxes[row[k]].Left - boxes[row[k - 1]].Right))
                    .ToArray();
                var height = row.Max(i => boxes[i].Height);

                var joined = await Joined(engine, bitmap, row.Select(i => boxes[i]), language);
                var delta = Length(joined) - Length(pieces);
                if (delta > 0) { rowsLosing++; lost = true; }

                Console.WriteLine($"  row y={row.Min(i => boxes[i].Top):0} boxes={row.Length} " +
                    $"h={height:0} gaps=[{string.Join(",", gaps)}] " +
                    (delta > 0 ? $"LOSES {delta}" : "ok"));
                Console.WriteLine($"      pieces  \"{pieces}\"");
                Console.WriteLine($"      joined  \"{joined}\"");
            }

            if (lost) imagesLosing++;
        }

        Console.WriteLine();
        Console.WriteLine($"TOTAL rows={rowsSeen} multiBox={rowsSplit} losing={rowsLosing} " +
            $"images losing={imagesLosing}/{args.Length - 1}");
        return 0;
    }

    // The row as one crop, read at its own native size. Native rather than the realtime fraction
    // because the crop is a fraction of the region already; the question is what the pixels say,
    // not what a second downscale of them says.
    private static async Task<string> Joined(
        OnnxOcrEngine engine, Bitmap bitmap, IEnumerable<SKRect> row, string language)
    {
        var rects = row.ToList();
        var margin = (int)Math.Max(4, rects.Max(r => r.Height) * .2f);
        var left = (int)Math.Floor(rects.Min(r => r.Left)) - margin;
        var top = (int)Math.Floor(rects.Min(r => r.Top)) - margin;
        var right = (int)Math.Ceiling(rects.Max(r => r.Right)) + margin;
        var bottom = (int)Math.Ceiling(rects.Max(r => r.Bottom)) + margin;
        var crop = Rectangle.Intersect(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            Rectangle.FromLTRB(left, top, right, bottom));
        if (crop.Width < 8 || crop.Height < 8) return "";

        using var strip = bitmap.Clone(crop, bitmap.PixelFormat);
        var blocks = await engine.TryRecognizeAsync(
            strip, language, Math.Max(strip.Width, strip.Height));
        return string.Concat(blocks?.Select(block => block.Text) ?? []);
    }

    // Same grouping Find starts with, and deliberately not a reimplementation of anything after it.
    private static List<int[]> Rows(IReadOnlyList<SKRect> boxes)
    {
        var rows = new List<int[]>();
        var claimed = new bool[boxes.Count];
        foreach (var seed in Enumerable.Range(0, boxes.Count).OrderByDescending(i => boxes[i].Width))
        {
            if (claimed[seed]) continue;
            var owners = Enumerable.Range(0, boxes.Count)
                .Where(i => !claimed[i] &&
                    Math.Min(boxes[i].Bottom, boxes[seed].Bottom) - Math.Max(boxes[i].Top, boxes[seed].Top)
                        >= Math.Min(boxes[i].Height, boxes[seed].Height) * .6f)
                .OrderBy(i => boxes[i].Left)
                .ToArray();
            foreach (var i in owners) claimed[i] = true;
            rows.Add(owners);
        }
        rows.Sort((a, b) => boxes[a[0]].Top.CompareTo(boxes[b[0]].Top));
        return rows;
    }

    // A row cut where the boxes stop running together. Two icons at opposite ends of a game screen
    // share a line and nothing else, and a crop spanning them swallows the dialogue between — which
    // is a fault in the measurement, not text the realtime path lost. The cut is at a gap wider than
    // a line height, the same distance ChromaticBoxRepair refuses a row over.
    private static List<int[]> Split(IReadOnlyList<SKRect> boxes, int[] row)
    {
        var height = row.Max(i => boxes[i].Height);
        var segments = new List<int[]>();
        var run = new List<int> { row[0] };
        for (var k = 1; k < row.Length; k++)
        {
            if (boxes[row[k]].Left - boxes[row[k - 1]].Right > height)
            {
                segments.Add([.. run]);
                run.Clear();
            }
            run.Add(row[k]);
        }
        segments.Add([.. run]);
        return segments;
    }

    // Characters that carry meaning, so that a row differing only in spaces does not read as a loss.
    private static int Length(string text) => text.Count(c => !char.IsWhiteSpace(c));

    // ChromaticBoxRepair's own pre-filter, reported rather than enforced: one large, flat, neutral,
    // dark background colour. A realtime region is a video frame or a game screen, and whether it
    // ever looks like this is half the question of whether that rule could transfer at all.
    private static string Background(SKBitmap source)
    {
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
        var bg = new SKColor(
            (byte)((mode >> 8) * 16 + 8), (byte)(((mode >> 4) & 15) * 16 + 8), (byte)((mode & 15) * 16 + 8));
        var share = (double)histogram[mode] / samples;
        var high = Math.Max(bg.Red, Math.Max(bg.Green, bg.Blue));
        var spread = high - Math.Min(bg.Red, Math.Min(bg.Green, bg.Blue));
        var passes = share >= .6 && high <= 96 && spread <= 32;
        return $"{(passes ? "PASSES" : "rejects")} bg={bg} share={share:0.00} high={high} spread={spread}";
    }

    private static SKBitmap ToSkia(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        return SKBitmap.Decode(stream);
    }
}
