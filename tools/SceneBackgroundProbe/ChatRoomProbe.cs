using System.Diagnostics;
using System.Drawing;
using System.IO;
using OverTranslate.Services.Realtime;
using Rect = System.Windows.Rect;

internal static class ChatRoomProbe
{
    public static void Run(string root, string label)
    {
        if (label is not ("before" or "after")) throw new ArgumentException("Use before or after.");
        string output = Path.Combine(root, "artifacts/chat-room-background");
        Directory.CreateDirectory(output);
        using var source = new Bitmap(Path.Combine(root, ".ai/test-images/chat-room/en-10.png"));
        // Manually annotated visible glyph bounds, not OCR output or clean background ground truth.
        Rect[] lines = [new(15,18,67,18), new(24,45,206,19), new(15,77,69,18), new(24,103,49,19),
            new(15,137,67,18), new(24,163,428,18), new(24,189,470,18), new(24,215,474,18),
            new(24,241,439,18), new(15,275,67,18), new(24,301,428,18), new(24,327,506,18),
            new(24,353,255,18), new(15,387,42,18), new(24,413,478,18), new(24,439,493,19),
            new(184,499,190,21)];
        using var patch = RealtimeNaturalBackground.CreatePatch(source, new Rectangle(0,0,source.Width,source.Height), lines);
        patch!.Save(Path.Combine(output, label + ".png"));
        var timings = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            var watch = Stopwatch.StartNew();
            using var sample = RealtimeNaturalBackground.CreatePatch(source, new Rectangle(0,0,source.Width,source.Height), lines);
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        Console.WriteLine($"{label}: median {timings[15]:F2}ms, P95 {timings[28]:F2}ms; {output}");
        string beforePath = Path.Combine(output, "before.png");
        if (label == "after" && File.Exists(beforePath))
        {
            using var before = new Bitmap(beforePath);
            using var comparison = new Bitmap(source.Width * 3, source.Height);
            comparison.SetResolution(96,96);
            using var g = Graphics.FromImage(comparison);
            foreach (var pair in new[] {(source,0),(before,1),(patch,2)})
                g.DrawImage(pair.Item1, new Rectangle(pair.Item2 * source.Width,0,source.Width,source.Height),
                    new Rectangle(0,0,source.Width,source.Height), GraphicsUnit.Pixel);
            comparison.Save(Path.Combine(output, "comparison.png"));
        }
    }
}
