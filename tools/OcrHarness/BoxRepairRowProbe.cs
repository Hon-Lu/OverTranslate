using System.Drawing;
using System.IO;
using OverTranslate.Services.Ocr;
using SkiaSharp;

// What ChromaticBoxRepair sees on a capture, row by row, and what it decided.
//
// This exists because reasoning about the repair from its output is guesswork: a capture where
// nothing fires looks identical to one the pre-filter never let in. Reading the rejection off a
// reimplementation is worse — a replica built from the harness's post-filter boxes predicted two
// repairs on a capture where the shipped code makes none, because Find is handed every detector
// box and the harness prints only the ones that survived the confidence filter.
//
// So this prints MEASUREMENTS, never verdicts. The row walk below is the same grouping Find starts
// with (boxes that share height with the widest one on their line), and everything after it is a
// number next to the constant it is tested against. The verdict comes from calling the real Find,
// whose repairs are listed at the end. If the numbers look like they should have produced a repair
// and the list is empty, the numbers are what to doubt.
//
// ONE CAVEAT, and it will mislead anyone who forgets it: the boxes come from a detection session,
// which repairs on its way out. On a capture where nothing fires they are Find's input; on one
// where something did, they are its OUTPUT, and asking Find about them a second time answers zero
// because the row is already whole. A merged line in the reading with "0 repair(s)" here means the
// repair happened, not that it was refused.
internal static class BoxRepairRowProbe
{
    internal static int Run(string[] args)
    {
        var language = args[0];
        using var engine = new OnnxOcrEngine();

        foreach (var path in args.Skip(1))
        {
            using var bitmap = new Bitmap(path);
            using var source = ToSkia(bitmap);

            // Pre-filter boxes in the capture's own coordinates: what Apply hands Find. On a
            // capture where nothing is repaired these are also the boxes the session reports.
            using var session = engine.BeginDetection(bitmap, language);
            var boxes = session.Boxes
                .Select(box => new SKRect(
                    (float)box.Bounds.Left, (float)box.Bounds.Top,
                    (float)box.Bounds.Right, (float)box.Bounds.Bottom))
                .ToList();

            Console.WriteLine();
            Console.WriteLine("=".PadRight(78, '='));
            Console.WriteLine($"IMAGE: {path}  {source.Width}x{source.Height}  boxes={boxes.Count}");

            if (!Background(source, out var bg, out var share))
            {
                Console.WriteLine($"  pre-filter REJECTS: bg={bg} share={share:0.00} " +
                    "(needs >= .60, high <= 96, spread <= 32)");
                continue;
            }
            Console.WriteLine($"  pre-filter passes: bg={bg} share={share:0.00}");

            Rows(source, bg, boxes);

            var repairs = ChromaticBoxRepair.Find(source, boxes);
            Console.WriteLine($"  Find returned {repairs.Count} repair(s)");
            foreach (var repair in repairs)
                Console.WriteLine($"    owners=[{string.Join(",", repair.Owners)}] -> {repair.Bounds}");
        }
        return 0;
    }

    private static void Rows(SKBitmap source, SKColor bg, IReadOnlyList<SKRect> boxes)
    {
        bool Ink(SKColor c) => Math.Max(Math.Abs(c.Red - bg.Red),
            Math.Max(Math.Abs(c.Green - bg.Green), Math.Abs(c.Blue - bg.Blue))) > 40;
        bool Color(SKColor c)
        {
            var high = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
            var low = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
            return high - low >= 32 && low * 2 >= high;
        }

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

            var top = Math.Max(0, (int)Math.Floor(owners.Min(i => boxes[i].Top)));
            var bottom = Math.Min(source.Height, (int)Math.Ceiling(owners.Max(i => boxes[i].Bottom)));
            var boxLeft = Math.Max(0, (int)Math.Floor(owners.Min(i => boxes[i].Left)));
            var boxRight = Math.Min(source.Width, (int)Math.Ceiling(owners.Max(i => boxes[i].Right)));
            Console.WriteLine($"  row seed=[{seed}] owners={owners.Length} " +
                $"y={top}..{bottom} boxes x={boxLeft}..{boxRight}");
            foreach (var i in owners)
                Console.WriteLine($"        [{i}] {boxes[i].Left:0},{boxes[i].Top:0} {boxes[i].Width:0}x{boxes[i].Height:0}");
            if (bottom - top < 8 || boxRight - boxLeft < 8) { Console.WriteLine("      too small"); continue; }

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
            var height = inkBottom - inkTop;
            Console.WriteLine($"      ink height {height} vs 18 needed");
            if (height < 18) continue;

            var reach = Math.Max(2, height);
            var left = boxLeft;
            for (var blank = 0; left > 0 && blank < reach; left--) blank = inked[left - 1] ? 0 : blank + 1;
            while (left < boxLeft && !inked[left]) left++;
            var right = boxRight;
            for (var blank = 0; right < width && blank < reach; right++) blank = inked[right] ? 0 : blank + 1;
            while (right > boxRight && !inked[right - 1]) right--;

            var gap = 0; var maxGap = 0;
            for (var x = left; x < right; x++) { gap = inked[x] ? 0 : gap + 1; maxGap = Math.Max(maxGap, gap); }
            Console.WriteLine($"      reach x={left}..{right}   width {right - left} vs {height * 6} needed   " +
                $"fill {(double)ink / Math.Max(1, (boxRight - boxLeft) * height):0.00} vs 0.65 max   " +
                $"maxGap {maxGap} vs {height} max   " +
                $"widest {owners.Max(i => boxes[i].Width):0} vs {height * 2.5:0} needed");

            int missing = 0, mTop = bottom, mBottom = top, mLeft = right, mRight = left;
            for (var yy = top; yy < bottom; yy++)
            for (var x = left; x < right; x++)
            {
                var c = source.GetPixel(x, yy);
                if (!Ink(c) || !Color(c) || owners.Any(i => boxes[i].Contains(x, yy))) continue;
                missing++;
                mLeft = Math.Min(mLeft, x); mRight = Math.Max(mRight, x + 1);
                mTop = Math.Min(mTop, yy); mBottom = Math.Max(mBottom, yy + 1);
            }
            Console.WriteLine($"      uncovered coloured ink {missing} vs {height} needed" +
                (missing == 0 ? "" : $", spans {mRight - mLeft}x{mBottom - mTop} at {mLeft},{mTop}" +
                    $" (needs {height * .2:0} wide, {height * .3:0} tall)"));
        }
    }

    private static bool Background(SKBitmap source, out SKColor bg, out double share)
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
        bg = new SKColor((byte)((mode >> 8) * 16 + 8), (byte)(((mode >> 4) & 15) * 16 + 8), (byte)((mode & 15) * 16 + 8));
        share = (double)histogram[mode] / samples;
        var high = Math.Max(bg.Red, Math.Max(bg.Green, bg.Blue));
        return share >= .6 && high <= 96 && high - Math.Min(bg.Red, Math.Min(bg.Green, bg.Blue)) <= 32;
    }

    private static SKBitmap ToSkia(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        return SKBitmap.Decode(stream);
    }
}
