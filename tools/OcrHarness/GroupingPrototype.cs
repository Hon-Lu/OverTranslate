using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

// Reusable prototype workbench: fixed inputs through the real grouper, never copied rules.
// capture cache.json image.png ... (or @list.txt); replay cache.json output.json.
internal static class GroupingPrototype
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal record Block(string Text, double[] Bounds, double[] LayoutBounds,
        OcrLayoutScript Script, double? Glyph, double? Render, double? Confidence, double? Ink = null, double[][]? Lines = null)
    {
        public OcrTextBlock Restore() => new(Text, RectOf(Bounds), SourceLineBounds: Lines?.Select(RectOf).ToArray(), RenderGlyphHeight: Render,
            Confidence: Confidence, LayoutScript: Script, LayoutBounds: RectOf(LayoutBounds),
            LayoutGlyphHeight: Glyph) { LayoutInkHeight = Ink };
        public static Block From(OcrTextBlock b) => new(b.Text, Box(b.Bounds), Box(b.LayoutBounds),
            b.LayoutScript, b.LayoutGlyphHeight, b.RenderGlyphHeight, b.Confidence, b.LayoutInkHeight, b.SourceLineBounds?.Select(Box).ToArray());
    }
    internal record Capture(string Image, string Flow, string Language, Block[] Blocks);
    internal record Result(string Image, string Profile, string[][] Groups, Block[] Output,
        OcrTextBlockGrouper.NextLineDecision[] Decisions);

    internal static async Task<int> Run(string[] args)
    {
        if (args.Length < 2 || (args[0] != "ink" && args.Length < 3))
            throw new ArgumentException("capture cache.json images/@list; enrich/replay input.json output.json; ink input.json");
        if (args[0] == "capture")
        {
            if (File.Exists(args[1])) throw new IOException("Refusing to overwrite cached OCR inputs.");
            using var engine = new OnnxOcrEngine();
            var captures = new List<Capture>();
            var paths = args.Skip(2).SelectMany(s => s.StartsWith('@') ? File.ReadAllLines(s[1..]) : [s]);
            foreach (var entry in paths.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                // Optional tab-separated flow and language; defaults match screenshot --group-explain.
                var fields = entry.Trim().Split('\t');
                var path = fields[0].Replace('\\', '/');
                var flow = fields.Length > 1 ? fields[1] : "screenshot";
                var language = fields.Length > 2 ? fields[2] : "EN";
                using var bitmap = new Bitmap(path);
                var raw = flow is "realtime" or "panel"
                    ? await engine.TryRecognizeAsync(bitmap, language,
                        RealtimeDetectorSize.For(bitmap.Width, bitmap.Height, (flow == "panel" ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle)).Primary)
                    : await engine.RecognizeAsync(bitmap, language);
                if (raw is null) throw new InvalidOperationException($"OCR unavailable: {path}");
                if (flow is "realtime" or "panel") raw = OcrService.RejectUnconvincingBlocks(raw);
                captures.Add(new(path, flow, language, raw.Select(Block.From).ToArray()));
                Console.WriteLine($"CAPTURE {path}: {raw.Count}");
            }
            Save(args[1], captures);
            return 0;
        }
        if (args[0] == "enrich")
        {
            var captured = JsonSerializer.Deserialize<Capture[]>(File.ReadAllText(args[1]))!;
            Save(args[2], captured.Select(input =>
            {
                if (input.Flow is "realtime" or "panel") return input;
                using var bitmap = new Bitmap(input.Image);
                var blocks = TextInkMetrics.Annotate(bitmap, input.Blocks.Select(b => b.Restore()).ToArray());
                return input with { Blocks = blocks.Select(Block.From).ToArray() };
            }).ToArray());
            return 0;
        }
        if (args[0] == "ink")
        {
            foreach (var input in JsonSerializer.Deserialize<Capture[]>(File.ReadAllText(args[1]))!)
            {
                using var bitmap = new Bitmap(input.Image);
                using var pixels = OnnxOcrEngine.ConvertToSkBitmap(bitmap);
                for (var i = 0; i < input.Blocks.Length; i++)
                {
                    var b = input.Blocks[i];
                    var height = TextInkMetrics.Measure(pixels, RectOf(b.LayoutBounds));
                    Console.WriteLine($"INK {input.Image} b{i}: {height?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"} box={b.LayoutBounds[3]} {b.Text}");
                }
            }
            return 0;
        }
        if (args[0] != "replay") throw new ArgumentException(args[0]);
        var inputs = JsonSerializer.Deserialize<Capture[]>(File.ReadAllText(args[1]))!;
        var results = new List<Result>();
        foreach (var input in inputs)
        {
            var modes = input.Flow == "panel" ? new[] { "panel" } :
                input.Flow == "realtime" ? ["realtime"] : ["general", "interface"];
            foreach (var mode in modes)
            {
                var profile = mode switch { "general" => GroupingProfile.General,
                    "interface" => GroupingProfile.Interface, _ => GroupingProfile.Realtime };
                var blocks = input.Blocks.Select(b => b.Restore()).ToArray();
                var trace = new GroupingTrace();
                var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
                var output = OcrTextBlockGrouper.Group(blocks, profile, decisions, trace);
                var sources = trace.Lines.ToDictionary(l => l.Id, l => l.SourceIds);
                var groups = trace.Groups.Select(g => g.SelectMany(l => sources[l]).ToArray()).ToArray();
                results.Add(new(input.Image, mode, groups, output.Select(Block.From).ToArray(), decisions.ToArray()));
                Console.WriteLine($"REPLAY {mode} {input.Image}: {string.Join(" | ", groups.Select(g => string.Join(",", g)))}");
            }
        }
        Save(args[2], results);
        return 0;
    }

    private static Rect RectOf(double[] r) => new(r[0], r[1], r[2], r[3]);
    private static double[] Box(Rect r) => [r.X, r.Y, r.Width, r.Height];
    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json).ReplaceLineEndings("\r\n"),
            new System.Text.UTF8Encoding(false));
    }
}
