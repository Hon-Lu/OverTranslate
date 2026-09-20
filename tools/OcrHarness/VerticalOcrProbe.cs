using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Rect = System.Windows.Rect;

internal static class VerticalOcrProbe
{
    // Offline comparison: old whole-frame rotation versus the production native-column path.
    internal static async Task<int> Run(string[] paths)
    {
        if (paths.Length < 2)
        {
            Console.Error.WriteLine("usage: --vertical-ocr <output-directory> <image> [images ...]");
            return 1;
        }
        var directory = paths[0];
        Directory.CreateDirectory(directory);
        using var engine = new OnnxOcrEngine();
        using (var blank = new Bitmap(32, 32)) await engine.RecognizeAsync(blank, "JA");
        var results = new List<object>();
        var index = 0;
        foreach (var path in paths.Skip(1))
        {
            using var source = new Bitmap(path);
            foreach (int? size in new int?[] { null, 960 })
            {
                using var rotated = new Bitmap(source);
                rotated.RotateFlip(RotateFlipType.Rotate270FlipNone);
                var timer = Stopwatch.StartNew();
                var oldRaw = size is null
                    ? await engine.RecognizeAsync(rotated, "JA")
                    : (await engine.TryRecognizeAsync(rotated, "JA", size))!;
                if (size is not null)
                    oldRaw = OcrService.RejectUnconvincingBlocks(oldRaw)
                        .Where(b => !OverTranslate.Services.Realtime.CollapsedDetection.IsCollapsed(
                            b.Bounds.Height, rotated.Height, b.Text)).ToList();
                var old = LegacyGroups(
                    OcrTextBlockGrouper.Group(oldRaw, GroupingProfile.Vertical), source.Width);
                var oldMs = timer.ElapsedMilliseconds;
                timer.Restart();
                var raw = size is null
                    ? await engine.RecognizeAsync(source, "JA", verticalText: true)
                    : (await engine.TryRecognizeAsync(source, "JA", size, verticalText: true))!;
                var current = OcrService.GroupVertical(raw, source.Width, realtime: size is not null, bitmap: source);
                results.Add(new { path, size, oldMs, currentMs = timer.ElapsedMilliseconds, old, raw, current });
                Draw(source, raw, current, Path.Combine(directory,
                    $"{index:D2}-{Path.GetFileNameWithoutExtension(path)}-{size?.ToString() ?? "screenshot"}.png"));
                Console.Error.WriteLine($"{Path.GetFileName(path)} size={size}: old={old.Count}/{old.Sum(b => b.Text.Length)} chars, new={current.Count}/{current.Sum(b => b.Text.Length)} chars");
            }
            index++;
        }
        File.WriteAllText(Path.Combine(directory, "results.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })
                .Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        return 0;
    }

    // Pre-refactor grouping geometry, frozen here so the comparison does not change with
    // native-column grouping. Production no longer has a rotated-frame coordinate system.
    private static List<OcrTextBlock> LegacyGroups(List<OcrTextBlock> rows, int originalWidth)
    {
        Rect Map(Rect r) => new(originalWidth - r.Bottom, r.X, r.Height, r.Width);
        var remaining = rows.Select(b => b with { Bounds = Map(b.Bounds), LayoutBounds = Map(b.LayoutBounds) })
            .Where(b => b.Text.Count(c => !char.IsWhiteSpace(c)) <= 1 ||
                        b.LayoutBounds.Width <= b.LayoutBounds.Height * 1.4)
            .OrderByDescending(b => b.LayoutBounds.X).ToList();
        var results = new List<OcrTextBlock>();
        while (remaining.Count > 0)
        {
            var group = new List<OcrTextBlock> { remaining[0] };
            remaining.RemoveAt(0);
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var i = remaining.Count - 1; i >= 0; i--)
                {
                    if (!group.Any(b => Adjacent(b.LayoutBounds, remaining[i].LayoutBounds))) continue;
                    group.Add(remaining[i]);
                    remaining.RemoveAt(i);
                    grew = true;
                }
            }
            results.Add(new OcrTextBlock(
                string.Concat(group.OrderByDescending(b => b.LayoutBounds.X).Select(b => b.Text)),
                group.Select(b => b.Bounds).Aggregate(Rect.Union)));
        }
        return results;

        static bool Adjacent(Rect a, Rect b)
        {
            var width = Math.Max(a.Width, b.Width);
            var gap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
            return Math.Abs(a.Y - b.Y) <= width * 0.6 && gap <= width * 0.6;
        }
    }

    private static void Draw(Bitmap source, List<OcrTextBlock> raw, List<OcrTextBlock> groups, string path)
    {
        using var preview = new Bitmap(source);
        using var canvas = Graphics.FromImage(preview);
        using var rawPen = new Pen(Color.Cyan, 1) { DashStyle = DashStyle.Dash };
        using var groupPen = new Pen(Color.OrangeRed, 2);
        using var font = new Font("Arial", 12);
        foreach (var block in raw) Box(block.Bounds, rawPen);
        for (var i = 0; i < groups.Count; i++)
        {
            var b = groups[i].Bounds;
            Box(b, groupPen);
            canvas.DrawString($"#{i + 1}", font, Brushes.Red, (float)b.Left, (float)b.Top);
        }
        preview.Save(path, ImageFormat.Png);
        void Box(Rect r, Pen pen) => canvas.DrawRectangle(pen, (float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);
    }
}
