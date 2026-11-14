using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using OverTranslate.Imaging;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

// How much of an outlined line the realtime erase leaves behind, against a background that is known.
//
// The corpora the erase was tuned on are captures, so what was under the text is a guess and "left
// behind" had to be defined around the mask that was being judged. Here the text is drawn onto a
// clean picture, so every pixel the text touched is known exactly, and so is what belongs there:
// the outline's width is the variable, and it is the one the reports are about — white dialogue
// text with a black edge, where the thicker the edge the more of it survives.
//
// Per case it prints the share of the text's pixels the mask does not cover, the share still
// visibly wrong after the repair, how much picture the mask takes that the text never touched, and
// how far the text's ink reaches past the recognition boxes the mask is built inside.
internal static class EraseLabProbe
{
    private static readonly string[] Lines =
    [
        "こういう場所にも手がかりが隠れているらしい。",
        "どんなジャンルの映画が人気なんだろう？",
    ];

    private sealed record Style(string Name, float Size, float Outline, float Shadow, Color Fill, Color Edge);

    internal static int Run(string[] args)
    {
        var panel = args.Contains("--panel");
        args = [.. args.Where(argument => argument != "--panel")];
        if (args.Length > 0 && args[0] == "--captures") return Captures(args[1..], panel);
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --erase-lab [--panel] <out-dir> <clean-background.png> [more.png ...]");
            return 1;
        }

        string output = args[0];
        Directory.CreateDirectory(output);
        var mode = panel ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
        using var engine = new OnnxOcrEngine();

        Style[] styles =
        [
            new("plain", 40, 0, 0, Color.White, Color.Black),
            new("edge2", 40, 2, 0, Color.White, Color.Black),
            new("edge4", 40, 4, 0, Color.White, Color.Black),
            new("edge6", 40, 6, 0, Color.White, Color.Black),
            new("edge8", 40, 8, 0, Color.White, Color.Black),
            new("edge4-shadow", 40, 4, 4, Color.White, Color.Black),
            new("small-edge3", 26, 3, 0, Color.White, Color.Black),
            new("small-edge5", 26, 5, 0, Color.White, Color.Black),
            new("big-edge8", 60, 8, 0, Color.White, Color.Black),
            new("colour-edge5", 40, 5, 0, Color.White, Color.FromArgb(200, 40, 90)),
            new("dark-white-edge5", 40, 5, 0, Color.FromArgb(30, 30, 40), Color.White),
        ];

        var totals = styles.ToDictionary(style => style.Name, _ => new List<Score>());
        foreach (var path in args.Skip(1))
        {
            using var loaded = new Bitmap(path);
            using var clean = loaded.Clone(new Rectangle(0, 0, loaded.Width, loaded.Height), PixelFormat.Format24bppRgb);
            foreach (var style in styles)
            {
                using var drawn = Draw(clean, style);
                var (size, _) = RealtimeDetectorSize.For(drawn.Width, drawn.Height, mode);
                var blocks = engine.TryRecognizeAsync(drawn, "JA", size).GetAwaiter().GetResult() ?? [];
                var regions = blocks.SelectMany(b => (b.SourceLineBounds is { Count: > 0 } lines ? lines : [b.Bounds])
                    .Select(r => new CpuTextRegion(r.X, r.Y, r.Width, r.Height, b.RenderGlyphHeight))).ToArray();

                using var source = ToImage(drawn);
                using var truth = ToImage(clean);
                using var mask = CpuTextMask.Build(source, regions);
                using var repaired = CpuHoleRepair.Repair(source, mask);
                var score = Measure(truth, source, mask, repaired.Image, regions);
                totals[style.Name].Add(score);

                string name = $"{Path.GetFileNameWithoutExtension(path)}-{style.Name}";
                Console.WriteLine($"{name,-34} boxes={regions.Length} glyph={regions.Select(r => r.GlyphHeight ?? r.Height).DefaultIfEmpty().Average():0} " + score);
                Save(Path.Combine(output, name + ".png"), drawn, mask, repaired.Image, regions);
            }
        }

        Console.WriteLine();
        foreach (var (name, scores) in totals)
            Console.WriteLine($"MEAN {name,-18} uncovered={scores.Average(s => s.Uncovered):0.0}% left={scores.Average(s => s.Left):0.0}% stained={scores.Average(s => s.Stained):0.0}% mask={scores.Average(s => s.Masked):0.00}% " +
                $"extra={scores.Average(s => s.Extra):0.00}% beyond(top/bottom/side)={scores.Average(s => s.Above):0.0}/{scores.Average(s => s.Below):0.0}/{scores.Average(s => s.Beside):0.0}px");
        return 0;
    }

    // The same erase over real captures, where nothing is known about what was under the text: the
    // share of the frame the mask takes and how long the mask and the repair took, to be compared
    // between two builds, and a sheet per capture to look at.
    private static int Captures(string[] args, bool panel)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --erase-lab --captures [--panel] <lang> <out-dir> <capture.png> [more.png ...]");
            return 1;
        }

        string language = args[0], output = args[1];
        Directory.CreateDirectory(output);
        var mode = panel ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
        using var engine = new OnnxOcrEngine();
        double masks = 0, repairs = 0, share = 0;
        int count = 0;
        foreach (var path in args.Skip(2))
        {
            using var loaded = new Bitmap(path);
            using var frame = loaded.Clone(new Rectangle(0, 0, loaded.Width, loaded.Height), PixelFormat.Format24bppRgb);
            var (size, _) = RealtimeDetectorSize.For(frame.Width, frame.Height, mode);
            var blocks = engine.TryRecognizeAsync(frame, language, size).GetAwaiter().GetResult() ?? [];
            var regions = blocks.SelectMany(b => (b.SourceLineBounds is { Count: > 0 } lines ? lines : [b.Bounds])
                .Select(r => new CpuTextRegion(r.X, r.Y, r.Width, r.Height, b.RenderGlyphHeight))).ToArray();
            using var source = ToImage(frame);

            // The fastest of five: the process has just run an inference on every core.
            double maskMs = double.MaxValue, repairMs = double.MaxValue;
            ImageBuffer? mask = null;
            CpuRepair? repaired = null;
            for (int run = 0; run < 5; run++)
            {
                mask?.Dispose();
                repaired?.Dispose();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                mask = CpuTextMask.Build(source, regions);
                maskMs = Math.Min(maskMs, watch.Elapsed.TotalMilliseconds);
                watch.Restart();
                repaired = CpuHoleRepair.Repair(source, mask);
                repairMs = Math.Min(repairMs, watch.Elapsed.TotalMilliseconds);
            }
            using (mask)
            using (repaired)
            {
                long set = 0;
                for (int y = 0; y < mask!.Height; y++)
                    foreach (byte value in mask.RowSpan(y)[..mask.Width]) if (value != 0) set++;
                double covered = set * 100.0 / (mask.Width * (double)mask.Height);
                Console.WriteLine($"{Path.GetFileName(path),-52} lines={regions.Length,3} mask={covered,6:0.000}% maskMs={maskMs,5:0.0} repairMs={repairMs,6:0.0}");
                Save(Path.Combine(output, Path.GetFileNameWithoutExtension(path) + ".png"), frame, mask, repaired!.Image, regions);
                masks += maskMs; repairs += repairMs; share += covered; count++;
            }
        }
        Console.WriteLine($"MEAN n={count} mask={share / Math.Max(1, count):0.000}% maskMs={masks / Math.Max(1, count):0.00} repairMs={repairs / Math.Max(1, count):0.00}");
        return 0;
    }

    private sealed record Score(double Uncovered, double Left, double Stained, double Masked, double Extra, double Above, double Below, double Beside)
    {
        public override string ToString() =>
            $"uncovered={Uncovered,5:0.0}% left={Left,5:0.0}% stained={Stained,5:0.0}% mask={Masked,5:0.00}% extra={Extra,5:0.00}% beyond(top/bottom/side)={Above:0.0}/{Below:0.0}/{Beside:0.0}px";
    }

    private static Bitmap Draw(Bitmap clean, Style style)
    {
        var drawn = (Bitmap)clean.Clone();
        using var graphics = Graphics.FromImage(drawn);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var family = new FontFamily("Yu Gothic UI");
        float y = Math.Max(8, (clean.Height - Lines.Length * style.Size * 1.6f) / 2);
        foreach (var line in Lines)
        {
            using var path = new GraphicsPath();
            path.AddString(line, family, (int)FontStyle.Bold, style.Size, new PointF(40, y), StringFormat.GenericTypographic);
            if (style.Shadow > 0)
            {
                using var shadow = (GraphicsPath)path.Clone();
                using var move = new Matrix();
                move.Translate(style.Shadow, style.Shadow);
                shadow.Transform(move);
                using var shade = new Pen(Color.FromArgb(200, style.Edge), Math.Max(1, style.Outline * 2)) { LineJoin = LineJoin.Round };
                graphics.DrawPath(shade, shadow);
                using var under = new SolidBrush(Color.FromArgb(200, style.Edge));
                graphics.FillPath(under, shadow);
            }
            if (style.Outline > 0)
            {
                using var pen = new Pen(style.Edge, style.Outline * 2) { LineJoin = LineJoin.Round };
                graphics.DrawPath(pen, path);
            }
            using var fill = new SolidBrush(style.Fill);
            graphics.FillPath(fill, path);
            y += style.Size * 1.6f;
        }
        return drawn;
    }

    private static Score Measure(ImageBuffer truth, ImageBuffer source, ImageBuffer mask, ImageBuffer repaired, CpuTextRegion[] regions)
    {
        int width = truth.Width, height = truth.Height;
        var ink = new bool[width * height];
        long inked = 0, uncovered = 0, left = 0, masked = 0, stained = 0;
        int top = int.MaxValue, bottom = int.MinValue, leftmost = int.MaxValue, rightmost = int.MinValue;
        for (int y = 0; y < height; y++)
        {
            var t = truth.RowSpan(y); var s = source.RowSpan(y); var r = repaired.RowSpan(y); var m = mask.RowSpan(y);
            for (int x = 0; x < width; x++)
            {
                int drawn = Math.Max(Math.Abs(t[x * 3] - s[x * 3]), Math.Max(Math.Abs(t[x * 3 + 1] - s[x * 3 + 1]), Math.Abs(t[x * 3 + 2] - s[x * 3 + 2])));
                if (m[x] != 0) masked++;
                // Anywhere the result is visibly not the picture, ink or not: a fill that read a
                // surviving row of outline carries its colour into pixels the text never touched.
                if (Math.Abs(Luma(r, x) - Luma(t, x)) > 30) stained++;
                if (drawn <= 24) continue;
                ink[y * width + x] = true;
                inked++;
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                leftmost = Math.Min(leftmost, x); rightmost = Math.Max(rightmost, x);
                if (m[x] == 0) uncovered++;
                // Still wrong in the direction the text was wrong: an outline that is still dark, a
                // fill that is still bright. A repair that merely differs from the picture is the
                // fill's own error and is not what a reader sees as leftover text.
                int was = Luma(s, x) - Luma(t, x), now = Luma(r, x) - Luma(t, x);
                if (Math.Abs(was) > 30 && Math.Sign(was) == Math.Sign(now) && Math.Abs(now) > 30) left++;
            }
        }

        // What the mask takes that no ink was within three pixels of.
        long extra = 0;
        for (int y = 0; y < height; y++)
        {
            var m = mask.RowSpan(y);
            for (int x = 0; x < width; x++)
            {
                if (m[x] == 0) continue;
                bool near = false;
                for (int dy = -3; dy <= 3 && !near; dy++)
                for (int dx = -3; dx <= 3 && !near; dx++)
                {
                    int yy = y + dy, xx = x + dx;
                    if (yy >= 0 && yy < height && xx >= 0 && xx < width && ink[yy * width + xx]) near = true;
                }
                if (!near) extra++;
            }
        }

        double above = 0, below = 0, beside = 0;
        if (regions.Length > 0 && inked > 0)
        {
            above = Math.Max(0, regions.Min(r => r.Y) - top);
            below = Math.Max(0, bottom - regions.Max(r => r.Y + r.Height));
            beside = Math.Max(Math.Max(0, regions.Min(r => r.X) - leftmost), Math.Max(0, rightmost - regions.Max(r => r.X + r.Width)));
        }
        double all = Math.Max(1, inked);
        return new(uncovered * 100.0 / all, left * 100.0 / all, stained * 100.0 / all, masked * 100.0 / (width * (double)height), extra * 100.0 / (width * (double)height), above, below, beside);
    }

    private static int Luma(Span<byte> row, int x) => (row[x * 3] * 29 + row[x * 3 + 1] * 150 + row[x * 3 + 2] * 77) >> 8;

    private static ImageBuffer ToImage(Bitmap bitmap)
    {
        var image = ImageBuffer.Uninitialized(bitmap.Width, bitmap.Height, PixelType.U8C3);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[bitmap.Width * 3];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                row.CopyTo(image.RowSpan(y));
            }
        }
        finally { bitmap.UnlockBits(data); }
        return image;
    }

    private static Bitmap ToBitmap(ImageBuffer image)
    {
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[bitmap.Width * 3];
            for (int y = 0; y < bitmap.Height; y++)
            {
                image.RowSpan(y)[..row.Length].CopyTo(row);
                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    // The drawn frame, the same with the mask in red and the boxes in green, and the repair.
    private static void Save(string path, Bitmap drawn, ImageBuffer mask, ImageBuffer repaired, CpuTextRegion[] regions)
    {
        using var sheet = new Bitmap(drawn.Width, drawn.Height * 3, PixelFormat.Format24bppRgb);
        using var overlay = (Bitmap)drawn.Clone();
        for (int y = 0; y < overlay.Height; y++)
        {
            var m = mask.RowSpan(y);
            for (int x = 0; x < overlay.Width; x++)
                if (m[x] != 0)
                {
                    var c = overlay.GetPixel(x, y);
                    overlay.SetPixel(x, y, Color.FromArgb((c.R + 255) / 2, c.G / 2, c.B / 2));
                }
        }
        using (var g = Graphics.FromImage(overlay))
        using (var pen = new Pen(Color.Lime, 1))
            foreach (var r in regions) g.DrawRectangle(pen, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
        using var fixedUp = ToBitmap(repaired);
        using (var g = Graphics.FromImage(sheet))
        {
            g.DrawImageUnscaled(drawn, 0, 0);
            g.DrawImageUnscaled(overlay, 0, drawn.Height);
            g.DrawImageUnscaled(fixedUp, 0, drawn.Height * 2);
        }
        sheet.Save(path, ImageFormat.Png);
    }
}
