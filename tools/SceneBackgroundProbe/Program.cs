using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using OverTranslate.Services.Realtime;
using Rect = System.Windows.Rect;

internal static class Program
{
    private const int Width = 640, Height = 220;
    private static readonly string Root = FindRoot();

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--chat-room") { ChatRoomProbe.Run(Root, args.ElementAtOrDefault(1) ?? "after"); return; }
        if (args.FirstOrDefault() == "--verify") { TextureChecks.Run(); return; }
        if (args.FirstOrDefault() == "--live")
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
                throw new PlatformNotSupportedException("WGC probe requires Windows 10 1903 or later.");
            LiveTextureProbe.Run(Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/texture-background"),
                int.TryParse(args.ElementAtOrDefault(2), out int seconds) ? Math.Clamp(seconds, 2, 120) : 10);
            return;
        }
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/texture-background");
        Directory.CreateDirectory(output);
        var records = new List<object>();
        var frames = new List<object>();
        foreach (var scene in new[]
        {
            (Name: "Game crystals", File: "即時翻譯-遊戲翻譯框.png", X: 20, Y: 520),
            (Name: "Game foliage", File: "即時翻譯-遊戲翻譯框.png", X: 1190, Y: 70),
            (Name: "Character contours", File: "即時翻譯-影片框.png", X: 660, Y: 380),
        })
        {
            using var source = new Bitmap(Path.Combine(Root, "docs/images/originals", scene.File));
            var repair = new RealtimeTextureBackground();
            for (int index = 0; index < 16; index++)
            {
                using var clean = source.Clone(new Rectangle(scene.X + index * 4, scene.Y, Width, Height), PixelFormat.Format32bppArgb);
                clean.SetResolution(96, 96);
                using var input = (Bitmap)clean.Clone();
                using var glyphs = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
                glyphs.SetResolution(96, 96);
                Rect bounds = AddSourceText(input);
                AddSourceText(glyphs);
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                using var result = repair.Repair(input, [bounds]);
                bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                using var restored = (Bitmap)input.Clone();
                using (var g = Graphics.FromImage(restored)) g.DrawImage(result.Overlay, new Rectangle(0, 0, Width, Height), new Rectangle(0, 0, Width, Height), GraphicsUnit.Pixel);
                using var translated = (Bitmap)restored.Clone();
                using (var g = Graphics.FromImage(translated))
                using (var path = new GraphicsPath())
                using (var outline = new Pen(Color.FromArgb(230, 15, 15, 15), 3) { LineJoin = LineJoin.Round })
                {
                    g.PageUnit = GraphicsUnit.Pixel;
        g.SmoothingMode = SmoothingMode.AntiAlias;
                    path.AddString("保留複雜背景紋理，讓譯文融入場景。", new FontFamily("Microsoft JhengHei"),
                        (int)FontStyle.Bold, 25, new PointF(60, 88), StringFormat.GenericDefault);
                    g.DrawPath(outline, path);
                    g.FillPath(Brushes.White, path);
                }
                using var comparison = new Bitmap(Width * 3, Height + 32);
                comparison.SetResolution(96, 96);
                using (var g = Graphics.FromImage(comparison))
                using (var font = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    g.Clear(Color.FromArgb(25, 27, 30));
                    g.DrawString(scene.Name + " / source", font, Brushes.White, 8, 8);
                    g.DrawString("Automatic mask + texture repair", font, Brushes.White, Width + 8, 8);
                    g.DrawString("Translation (no band)", font, Brushes.White, Width * 2 + 8, 8);
                    g.DrawImageUnscaled(input, 0, 32);
                    g.DrawImageUnscaled(restored, Width, 32);
                    g.DrawImageUnscaled(translated, Width * 2, 32);
                }
                string name = scene.Name.Replace(' ', '-').ToLowerInvariant() + "-" + index.ToString("D2");
                comparison.Save(Path.Combine(output, name + ".png"));
                if (index == 0)
                {
                    result.Overlay.Save(Path.Combine(output, name + "-overlay.png"));
                    clean.Save(Path.Combine(output, name + "-ground-truth.png"));
                }
                var expected = RealtimeTextureBackground.Read(glyphs);
                var overlay = RealtimeTextureBackground.Read(result.Overlay);
                var truth = RealtimeTextureBackground.Read(clean);
                var actual = RealtimeTextureBackground.Read(restored);
                int ink = 0, covered = 0, changedScene = 0;
                long error = 0;
                for (int i = 0; i < expected.Length; i++)
                {
                    if ((uint)expected[i] >> 24 > 16)
                    {
                        ink++;
                        if ((uint)overlay[i] >> 24 > 0) covered++;
                        error += Difference(truth[i], actual[i]);
                    }
                    else if ((uint)overlay[i] >> 24 > 0) changedScene++;
                }
                records.Add(new { scene.Name, frame = index, input = "real scene crop + planted outlined text",
                    motion = "synthetic horizontal camera pan, 4 pixels/frame", result.Stats,
                    allocatedBytes = bytes, glyphCoverage = (double)covered / Math.Max(1, ink),
                    glyphMae = (double)error / Math.Max(1, ink), changedScenePixels = changedScene });
                using var png = new MemoryStream();
                comparison.Save(png, ImageFormat.Png);
                frames.Add(new { scene = scene.Name, index, image = "data:image/png;base64," +
                    Convert.ToBase64String(png.ToArray()), milliseconds = result.Stats.Milliseconds });
            }
        }
        var options = new JsonSerializerOptions { WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        string metrics = JsonSerializer.Serialize(records, options);
        File.WriteAllText(Path.Combine(output, "metrics.json"), metrics, new UTF8Encoding(false));
        string data = JsonSerializer.Serialize(frames);
        string html = """
            <!doctype html><html lang="zh-Hant"><meta charset="utf-8">
            <title>複雜場景紋理重建 Probe</title>
            <style>body{background:#191b1e;color:#eee;font:16px system-ui;margin:24px}img{width:100%;display:block;margin-top:16px}button,select,input{font:inherit;margin-right:12px}p{max-width:1000px;line-height:1.7}</style>
            <h1>複雜場景紋理重建</h1>
            <p>左：原文字／中：只移除字形的紋理重建／右：譯文。背景來自儲存庫真實遊戲與動畫畫面。文字與水平移動為合成，用於取得已知背景比對；不是實際影片的端到端延遲測試。</p>
            <select id="scene"></select><button id="play">播放／暫停</button><input id="frame" type="range" min="0" max="15" value="0"><span id="info"></span><img id="view">
            <script>
            const frames=DATA;
            const scene=document.getElementById('scene'),range=document.getElementById('frame');
            [...new Set(frames.map(f=>f.scene))].forEach(s=>scene.add(new Option(s,s)));
            let playing=false;document.getElementById('play').onclick=()=>playing=!playing;
            function draw(){const f=frames.find(f=>f.scene===scene.value&&f.index===+range.value);document.getElementById('view').src=f.image;document.getElementById('info').textContent='幀 '+f.index+'；修補 '+f.milliseconds.toFixed(1)+' ms';}
            scene.onchange=()=>{range.value=0;draw()};range.oninput=draw;
            setInterval(()=>{if(playing){range.value=(+range.value+1)%16;draw()}},67);draw();
            </script></html>
            """.Replace("DATA", data);
        File.WriteAllText(Path.Combine(output, "index.html"), html, new UTF8Encoding(false));
        Console.WriteLine("Wrote " + records.Count + " frame measurements.");
        Console.WriteLine(Path.Combine(output, "index.html"));
    }

    private static Rect AddSourceText(Bitmap image)
    {
        using var g = Graphics.FromImage(image);
        using var path = new GraphicsPath();
        using var pen = new Pen(Color.Black, 3) { LineJoin = LineJoin.Round };
        g.PageUnit = GraphicsUnit.Pixel;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        path.AddString("Texture must survive behind subtitles.", new FontFamily("Segoe UI"),
            (int)FontStyle.Bold, 27, new PointF(45, 87), StringFormat.GenericDefault);
        var bounds = path.GetBounds(null, pen);
        g.DrawPath(pen, path);
        g.FillPath(Brushes.White, path);
        return new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static int Difference(int a, int b) =>
        (Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) +
         Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) +
         Math.Abs((a & 255) - (b & 255))) / 3;

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
    }
}
