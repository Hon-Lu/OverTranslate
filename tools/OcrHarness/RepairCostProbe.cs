using System.Diagnostics;
using System.Drawing;
using System.IO;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using SkiaSharp;

// What the row repair costs on a live frame, which is the half of the question detection accuracy
// does not answer: the realtime loop runs about four passes a second, so a cost the screenshot flow
// can hide inside a one-off capture is a cost this path pays continuously.
//
// Two numbers, because the repair has two shapes. A frame whose background is not one flat dark
// colour leaves in the pre-filter, which is a strided sample of the image and nothing else; a frame
// that passes it walks every row's scanlines. Reporting the average over a mixed corpus would hide
// exactly the case worth knowing about, so this reports them apart.
internal static class RepairCostProbe
{
    internal static int Run(string[] args)
    {
        var panel = args.Contains("--panel");
        // The screenshot flow's own size instead of the realtime one. The repair's verdicts are not
        // comparable across sizes — the boxes it judges are different boxes — so a change to the
        // rule has to be checked at both.
        var screenshot = args.Contains("--screenshot");
        args = [.. args.Where(argument => argument is not ("--panel" or "--screenshot"))];
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --repair-cost [--panel] <lang> <image.png> [more.png ...]");
            return 1;
        }

        var language = args[0];
        var mode = panel ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
        using var engine = new OnnxOcrEngine();

        var passed = new List<double>();
        var rejected = new List<double>();

        foreach (var path in args.Skip(1))
        {
            using var bitmap = new Bitmap(path);
            using var source = ToSkia(bitmap);
            var (realtime, _) = RealtimeDetectorSize.For(bitmap.Width, bitmap.Height, mode);
            int? primary = screenshot ? null : realtime;

            List<SKRect> boxes = [];
            // Detection timed beside the repair, because the repair's cost only means something
            // next to the pass it is added to. Three runs, fastest kept: the first one on a given
            // model also pays for loading it.
            var detectMs = double.MaxValue;
            for (var run = 0; run < 3; run++)
            {
                var watch = Stopwatch.StartNew();
                // Without the repair, because these are the boxes Find is asked about. A session
                // that repaired on its way out would hand back rows that are already whole, and
                // asking Find about those answers zero for work it really did.
                using var session = engine.BeginDetection(bitmap, language, primary, repairRows: false);
                detectMs = Math.Min(detectMs, watch.Elapsed.TotalMilliseconds);
                boxes = [.. session.Boxes.Select(box => new SKRect(
                    (float)box.Bounds.Left, (float)box.Bounds.Top,
                    (float)box.Bounds.Right, (float)box.Bounds.Bottom))];
            }

            // The fastest of twenty runs, not the average of ten. This process has just finished an
            // ONNX inference on every core, so a mean here is mostly scheduling and clock noise —
            // the same image measured twice came back 3.7ms and 12.6ms for identical work. The
            // minimum is the run that was not interrupted, which is the number the frame budget
            // has to accommodate.
            var ms = double.MaxValue;
            var repairs = 0;
            for (var run = 0; run < 20; run++)
            {
                var watch = Stopwatch.StartNew();
                repairs = ChromaticBoxRepair.Find(source, boxes).Count;
                ms = Math.Min(ms, watch.Elapsed.TotalMilliseconds);
            }

            foreach (var repair in ChromaticBoxRepair.Find(source, boxes))
                Console.WriteLine($"    repair owners=[{string.Join(",", repair.Owners)}] " +
                    $"{repair.Bounds}  replacing " +
                    string.Join(" | ", repair.Owners.Select(i => $"{boxes[i].Left:0},{boxes[i].Top:0} " +
                        $"{boxes[i].Width:0}x{boxes[i].Height:0}")));

            (repairs > 0 || Passes(source) ? passed : rejected).Add(ms);
            Console.WriteLine($"{Path.GetFileName(path),-52} {bitmap.Width}x{bitmap.Height} " +
                $"boxes={boxes.Count,3} {(Passes(source) ? "pre-filter passes" : "pre-filter rejects")} " +
                $"repairs={repairs} detect={detectMs:0}ms repair={ms:0.0}ms " +
                $"(+{ms / detectMs * 100:0.0}%)");
        }

        Console.WriteLine();
        Report("pre-filter rejects", rejected);
        Report("pre-filter passes ", passed);
        return 0;
    }

    private static void Report(string label, List<double> times)
    {
        if (times.Count == 0) { Console.WriteLine($"{label}: none"); return; }
        times.Sort();
        Console.WriteLine($"{label}: n={times.Count} median={times[times.Count / 2]:0.00}ms " +
            $"max={times[^1]:0.00}ms");
    }

    private static bool Passes(SKBitmap source)
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
        var high = Math.Max(bg.Red, Math.Max(bg.Green, bg.Blue));
        return (double)histogram[mode] / samples >= .6 && high <= 96
            && high - Math.Min(bg.Red, Math.Min(bg.Green, bg.Blue)) <= 32;
    }

    private static SKBitmap ToSkia(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        return SKBitmap.Decode(stream);
    }
}
