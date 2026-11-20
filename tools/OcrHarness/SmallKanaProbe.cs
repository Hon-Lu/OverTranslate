using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

// #412: horizontal Japanese reads a small kana (っ ゃ ゅ ょ ぁ…) as its full-size twin when the
// recognition crop hugs the line. Every image is detected once per workload and the same boxes are
// recognised once per variant — SmallKanaReread off (base) or on with a margin ratio (k0.15) — so a
// difference between variants is the reread alone. Rows are written per recognised line, with the
// recognition time of the whole session in `ms`.
//
//   --small-kana <lang> <list.txt> <out.tsv> [--shift N] [--variants base,k0.10,k0.15,...]
//            [--workloads sub,fb68,fb100,f60,shot]
//
// list: one image per line, optionally <TAB>X,Y,W,H for the ROI. --shift N also reads the ROI moved
// 1..N px down and right (the Stardew case only shows on some framings). sub/fb68/fb100 are the
// subtitle mode's detector sizes, f60 the 60% 辨識解析度, shot the screenshot flow.
internal static class SmallKanaProbe
{
    internal static int Run(string[] args)
    {
        var shift = 0;
        var variantSpec = "base,k0.15";
        var workloadSpec = "sub,fb68,fb100,f60,shot";
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--shift") shift = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--variants") variantSpec = args[++i];
            else if (args[i] == "--workloads") workloadSpec = args[++i];
            else rest.Add(args[i]);
        }

        if (rest.Count < 3)
        {
            Console.Error.WriteLine("usage: --small-kana <lang> <list.txt> <out.tsv> [--shift N] [--variants ...] [--workloads ...]");
            return 1;
        }

        var language = rest[0];
        var variants = variantSpec.Split(',').Select(v => (Name: v, Ratio: ParseVariant(v))).ToList();
        var workloads = workloadSpec.Split(',').ToHashSet();
        var list = File.ReadAllLines(rest[1], Encoding.UTF8)
            .Where(l => l.Trim().Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .ToList();

        using var engine = new OnnxOcrEngine();
        using var output = new StreamWriter(rest[2], false, new UTF8Encoding(false));
        output.WriteLine("image\tshift\tworkload\tsize\tscale\tvariant\tleft\ttop\twidth\theight\tscore\tms\ttext");
        var done = 0;
        foreach (var row in list)
        {
            var path = row[0];
            using var image = new Bitmap(path);
            var roi = row.Length > 1 && row[1].Trim().Length > 0
                ? ParseRect(row[1])
                : new Rectangle(0, 0, image.Width, image.Height);
            for (var s = 0; s <= shift; s++)
            {
                var rect = Rectangle.Intersect(
                    new Rectangle(roi.X + s, roi.Y + s, roi.Width - s, roi.Height - s),
                    new Rectangle(Point.Empty, image.Size));
                using var crop = image.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var native = Math.Max(crop.Width, crop.Height);
                foreach (var (workload, size) in Sizes(crop.Width, crop.Height, workloads))
                {
                    var scale = size is { } n ? Math.Min(1.0, (double)n / native)
                        : Math.Min(1.0, (double)OnnxOcrEngine.ScreenshotDetectSize / native);
                    using var session = engine.BeginDetection(crop, language, size, repairRows: true);
                    var indices = Enumerable.Range(0, session.Boxes.Count).ToList();
                    foreach (var (name, kana) in variants)
                    {
                        SmallKanaReread.Enabled = kana is not null;
                        if (kana is { } ratio) SmallKanaReread.MarginRatio = ratio;
                        try
                        {
                            var clock = System.Diagnostics.Stopwatch.StartNew();
                            session.Recognize(indices, out var recognised);
                            var ms = clock.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);
                            foreach (var b in recognised)
                            {
                                var xs = b.BoxPoints.Select(p => p.X).ToArray();
                                var ys = b.BoxPoints.Select(p => p.Y).ToArray();
                                var text = string.Concat(b.Chars ?? []).Replace('\t', ' ');
                                var score = b.CharScores is { Length: > 0 } sc ? sc.Average() : 0f;
                                output.WriteLine(string.Join('\t',
                                    Path.GetFileName(path), s, workload, size?.ToString(CultureInfo.InvariantCulture) ?? "shot",
                                    scale.ToString("0.###", CultureInfo.InvariantCulture), name,
                                    xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min(),
                                    score.ToString("0.###", CultureInfo.InvariantCulture), ms, text));
                            }
                        }
                        finally
                        {
                            SmallKanaReread.Enabled = true;
                            SmallKanaReread.MarginRatio = DefaultRatio;
                        }
                    }
                }
            }

            done++;
            if (done % 10 == 0) Console.WriteLine($"{done}/{list.Count}");
            output.Flush();
        }

        Console.WriteLine($"{done}/{list.Count} done");
        return 0;
    }

    private static readonly double DefaultRatio = SmallKanaReread.MarginRatio;

    // base: the small-kana reread off. kX: on, with X of the crop's height as background rows.
    private static double? ParseVariant(string name) =>
        name == "base" ? null
        : name.StartsWith('k') ? double.Parse(name[1..], CultureInfo.InvariantCulture)
        : throw new ArgumentException(name);

    private static IEnumerable<(string Workload, int? Size)> Sizes(int width, int height, HashSet<string> wanted)
    {
        var seen = new HashSet<int?>();
        var (primary, fallbacks) = RealtimeDetectorSize.For(width, height, RealtimeBlockMode.Subtitle);
        var candidates = new List<(string, int?)> { ("sub", primary) };
        var native = Math.Max(width, height);
        foreach (var f in fallbacks)
            candidates.Add((f >= native ? "fb100" : "fb68", f));
        var (f60, _) = RealtimeDetectorSize.For(width, height, RealtimeBlockMode.Subtitle, fraction: 0.60);
        candidates.Add(("f60", f60));
        candidates.Add(("shot", null));
        foreach (var (w, s) in candidates)
            if (wanted.Contains(w) && seen.Add(s))
                yield return (w, s);
    }

    private static Rectangle ParseRect(string text)
    {
        var p = text.Split(',').Select(int.Parse).ToArray();
        return new Rectangle(p[0], p[1], p[2], p[3]);
    }
}
