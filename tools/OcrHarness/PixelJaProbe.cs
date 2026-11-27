using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using SkiaSharp;

// Pixel-art Japanese: every line the engine read on each picture, per workload, with the score and
// whether the realtime path would have thrown it away and why. Scored against a transcript by
// .ai/pixel-ja-ocr/score.py.
//
//   --pixel-ja <lang> <list.txt> <out.tsv> [--workloads shot,rt-sub,rt-panel] [--scale N]
//
// list: one image per line, optionally <TAB>X,Y,W,H for a region. --scale N enlarges the picture by
// N with nearest neighbour first (a game shown bigger), coordinates are then in the enlarged frame.
// shot is the screenshot flow's engine read (nothing dropped there); rt-* is the realtime engine read
// at the mode's primary size, then the session's own drops: collapsed, too short, short and
// unconvincing (the confidence floor).
internal static class PixelJaProbe
{
    internal static async Task<int> Run(string[] args)
    {
        var workloadSpec = "shot,rt-sub,rt-panel";
        var scale = 1;
        var shifts = 1;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--workloads") workloadSpec = args[++i];
            else if (args[i] == "--keep-all") OnnxOcrEngine.KeepEveryReading = true;
            else if (args[i] == "--shifts") shifts = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--scale") scale = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else rest.Add(args[i]);
        }
        if (rest.Count < 3)
        {
            Console.Error.WriteLine("usage: --pixel-ja <lang> <list.txt> <out.tsv> [--workloads ...] [--scale N]");
            return 1;
        }

        var language = rest[0];
        var workloads = workloadSpec.Split(',');
        var list = File.ReadAllLines(rest[1], Encoding.UTF8)
            .Where(l => l.Trim().Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .ToList();

        using var engine = new OnnxOcrEngine();
        using var output = new StreamWriter(rest[2], false, new UTF8Encoding(false));
        output.WriteLine("image\tshift\tregion\tworkload\tsize\tgrid\tleft\ttop\twidth\theight\tscore\tdrop\tms\ttext");
        foreach (var row in list)
        {
            var path = row[0];
            using var original = new Bitmap(path);
            using var image = scale == 1 ? (Bitmap)original.Clone() : Enlarge(original, scale);
            var whole = row.Length > 1 && row[1].Trim().Length > 0
                ? ParseRect(row[1], scale)
                : new Rectangle(0, 0, image.Width, image.Height);
            for (var shift = 0; shift < shifts * shifts; shift++)
            {
            int sx = shift % shifts, sy = shift / shifts;
            var roi = Rectangle.Intersect(
                new Rectangle(whole.X + sx, whole.Y + sy, whole.Width - sx, whole.Height - sy),
                new Rectangle(Point.Empty, image.Size));
            using var crop = image.Clone(roi, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var grid = GridOf(crop);
            var region = row.Length > 1 ? row[1] : "full";

            foreach (var workload in workloads)
            {
                int? size = workload switch
                {
                    "shot" => null,
                    "rt-sub" => RealtimeDetectorSize.For(crop.Width, crop.Height, RealtimeBlockMode.Subtitle).Primary,
                    "rt-panel" => RealtimeDetectorSize.For(crop.Width, crop.Height, RealtimeBlockMode.Panel).Primary,
                    _ => throw new ArgumentException(workload),
                };
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var blocks = (size is null
                    ? await engine.RecognizeAsync(crop, language)
                    : await engine.TryRecognizeAsync(crop, language, size)) ?? [];
                var ms = clock.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
                foreach (var b in blocks)
                {
                    var drop = "";
                    if (b.Confidence < 0.6) drop = "engine-floor";
                    else if (size is not null)
                    {
                        if (CollapsedDetection.IsCollapsed(b.Bounds.Height, crop.Height, b.Text)) drop = "collapsed";
                        else if (ShortReadingDetection.IsTooShort(b.Text)) drop = "too-short";
                        else if (ShortReadingDetection.IsUnconvincingShortText(b.Text, b.Confidence)) drop = "floor";
                    }
                    output.WriteLine(string.Join('\t',
                        Path.GetFileName(path), $"{sx},{sy}", region, workload, size?.ToString(CultureInfo.InvariantCulture) ?? "shot",
                        grid,
                        (int)b.Bounds.Left + roi.X, (int)b.Bounds.Top + roi.Y, (int)b.Bounds.Width, (int)b.Bounds.Height,
                        (b.Confidence ?? 0).ToString("0.000", CultureInfo.InvariantCulture), drop, ms,
                        b.Text.Replace('\t', ' ').Replace('\n', ' ')));
                }
            }
            Console.WriteLine($"{Path.GetFileName(path)} {region} {sx},{sy} grid={grid}");
            }
            output.Flush();
        }
        return 0;
    }

    private static string GridOf(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        using var sk = SKBitmap.Decode(stream);
        using var bgra = sk.Copy(SKColorType.Bgra8888);
        return PixelArtSmoothing.Find(bgra) is { } g ? $"{g.BlockX}x{g.BlockY}" : "none";
    }

    private static Bitmap Enlarge(Bitmap source, int n)
    {
        var result = new Bitmap(source.Width * n, source.Height * n, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(source, 0, 0, source.Width * n, source.Height * n);
        return result;
    }

    private static Rectangle ParseRect(string text, int scale)
    {
        var p = text.Split(',').Select(int.Parse).ToArray();
        return new Rectangle(p[0] * scale, p[1] * scale, p[2] * scale, p[3] * scale);
    }
}
