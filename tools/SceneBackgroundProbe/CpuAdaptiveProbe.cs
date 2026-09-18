using OverTranslate.Services.Realtime;
using TextRegion = OverTranslate.Services.Realtime.CpuTextRegion;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using OpenCvSharp;

internal static class CpuAdaptiveProbe
{
    public static void Run(string root)
    {
        string output = Path.Combine(root, "artifacts/cpu-adaptive-background");
        Directory.CreateDirectory(output);
        var records = new List<object>();
        var names = new List<string>();
        foreach (string file in Directory.GetFiles(Path.Combine(root, ".ai/test-images/chat-room"), "*.png").Order())
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string boxes = Path.Combine(root, "artifacts/inpaint-research", name + "-ocr.json");
            if (!File.Exists(boxes)) throw new InvalidOperationException("OCR preparation did not produce the expected coordinates.");
            using var image = Cv2.ImRead(file);
            Evaluate(name, image, JsonSerializer.Deserialize<TextRegion[]>(File.ReadAllText(boxes))!);
        }
        foreach (var scene in new[]
        {
            ("crystals", "即時翻譯-遊戲翻譯框.png", 20, 520),
            ("foliage", "即時翻譯-遊戲翻譯框.png", 1190, 70),
            ("face", "即時翻譯-影片框.png", 660, 380),
        })
        {
            using var full = Cv2.ImRead(Path.Combine(root, "docs/images/originals", scene.Item2));
            using var crop = new Mat(full, new Rect(scene.Item3, scene.Item4, 640, 220));
            using var truth = crop.Clone();
            foreach (bool dark in new[] { false, true })
            {
                using var input = truth.Clone();
                using var glyphs = new Mat(input.Size(), MatType.CV_8UC1, Scalar.Black);
                const string text = "CPU background repair";
                var origin = new Point(35, 115);
                Cv2.PutText(input, text, origin, HersheyFonts.HersheySimplex, 1.15, dark ? Scalar.White : Scalar.Black, 5, LineTypes.AntiAlias);
                Cv2.PutText(input, text, origin, HersheyFonts.HersheySimplex, 1.15, dark ? Scalar.Black : new Scalar(90, 225, 255), 2, LineTypes.AntiAlias);
                Cv2.PutText(glyphs, text, origin, HersheyFonts.HersheySimplex, 1.15, Scalar.White, 5, LineTypes.AntiAlias);
                var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, 1.15, 5, out int baseline);
                Evaluate(scene.Item1 + (dark ? "-dark" : "-light"), input,
                    [new TextRegion(33, 115 - size.Height - 2, size.Width + 4, size.Height + baseline + 4, size.Height)], truth, glyphs);
            }
        }
        File.WriteAllText(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "index.html"), """
<!doctype html><meta charset="utf-8"><title>CPU 分區背景修補</title>
<style>body{font:16px system-ui;background:#17191d;color:#eee;margin:24px}select{padding:8px;font:inherit}main{display:flex;gap:20px;overflow:auto}figure{margin:0}figcaption{margin:10px 0}p{max-width:1100px;line-height:1.6}</style>
<h1>CPU 字形遮罩與分區修補</h1><p>無 GPU、無神經模型、無背景歷史。22 張 chat-room 原圖，加上遊戲／影片複雜底圖上的 6 組合成文字。合成案例有乾淨背景真值；chat-room 沒有。去字結果不覆蓋譯文，以便檢查缺陷。</p>
<select id="scene"></select> <select id="method"><option value="adaptive">新遮罩＋分區修補</option><option value="new-half">新遮罩＋半解析度</option><option value="baseline">舊遮罩＋半解析度基準</option><option value="legacy">舊版矩形修補（僅品質對照）</option><option value="mask-preview">新遮罩</option><option value="old-mask-preview">舊遮罩</option><option value="truth">乾淨底圖（僅合成案例）</option></select>
<main><figure><figcaption>來源</figcaption><img id="source"></figure><figure><figcaption id="label"></figcaption><img id="result"></figure></main>
<script>const names=NAMES;const scene=document.querySelector('#scene'),method=document.querySelector('#method'),source=document.querySelector('#source'),result=document.querySelector('#result'),label=document.querySelector('#label');names.forEach(n=>scene.add(new Option(n,n)));scene.value='en-10';function update(){source.src=scene.value+'-source.png';result.src=scene.value+'-'+method.value+'.png';label.textContent=method.selectedOptions[0].text;}scene.onchange=method.onchange=update;result.onerror=()=>label.textContent='此案例沒有乾淨底圖';update();</script>
""".Replace("NAMES", JsonSerializer.Serialize(names)));
        Verify();
        Console.WriteLine("Output: " + output);

        void Evaluate(string name, Mat source, TextRegion[] regions, Mat? truth = null, Mat? glyphs = null)
        {
            names.Add(name);
            string prefix = Path.Combine(output, name);
            Cv2.ImWrite(prefix + "-source.png", source);
            if (truth is not null) Cv2.ImWrite(prefix + "-truth.png", truth);
            using (var bitmap = new System.Drawing.Bitmap(prefix + "-source.png"))
            using (var legacy = RealtimeNaturalBackground.CreatePatch(bitmap,
                new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                regions.Select(r => RealtimeNaturalBackground.GlyphBounds(
                    new System.Windows.Rect(r.X, r.Y, r.Width, r.Height), r.GlyphHeight)).ToArray()))
                legacy?.Save(prefix + "-legacy.png");
            using var oldMask = GlyphMask.Build(source, regions);
            using var mask = CpuTextMask.Build(source, regions);
            Preview(mask, "mask-preview"); Preview(oldMask, "old-mask-preview");
            foreach (string method in new[] { "baseline", "new-half", "adaptive" })
            {
                using var first = Repair(method, source, mask, oldMask);
                Cv2.ImWrite(prefix + "-" + method + ".png", first.Image);
                var usedMask = method == "baseline" ? oldMask : mask;
                int changedOutside = Program.ChangedOutside(source, first.Image, usedMask);
                if (changedOutside != 0) throw new InvalidOperationException("Pixels outside mask changed.");
                var timings = new List<double>();
                long allocations = 0;
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                for (int i = 0; i < 7; i++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    var clock = Stopwatch.StartNew();
                    if (method == "baseline")
                    {
                        using var rebuilt = GlyphMask.Build(source, regions);
                        using var repaired = Program.RepairReduced(source, rebuilt);
                    }
                    else
                    {
                        using var rebuilt = CpuTextMask.Build(source, regions);
                        using var repaired = Repair(method, source, rebuilt, oldMask);
                    }
                    timings.Add(clock.Elapsed.TotalMilliseconds);
                    allocations += GC.GetAllocatedBytesForCurrentThread() - before;
                }
                double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds / 7;
                timings.Sort();
                double? coverage = null, mae = null, backgroundMae = null;
                if (glyphs is not null && truth is not null)
                {
                    using var binary = new Mat(); using var hit = new Mat(); using var delta = new Mat(); using var background = new Mat();
                    Cv2.Threshold(glyphs, binary, 0, 255, ThresholdTypes.Binary);
                    Cv2.BitwiseAnd(binary, usedMask, hit);
                    coverage = (double)Cv2.CountNonZero(hit) / Cv2.CountNonZero(binary);
                    Cv2.Absdiff(truth, first.Image, delta);
                    Scalar error = Cv2.Mean(delta, binary);
                    mae = (error.Val0 + error.Val1 + error.Val2) / 3;
                    Cv2.BitwiseNot(binary, background);
                    error = Cv2.Mean(delta, background);
                    backgroundMae = (error.Val0 + error.Val1 + error.Val2) / 3;
                }
                process.Refresh();
                records.Add(new { name, method, medianMs = timings[3], maxMs = timings[^1], cpuMs,
                    allocatedBytes = allocations / 7, workingSetMiB = process.WorkingSet64 / 1048576.0,
                    maskPixels = Cv2.CountNonZero(usedMask), first.FullTiles, first.ReducedTiles,
                    changedOutside, glyphCoverage = coverage, glyphMae = mae, backgroundMae });
                Console.WriteLine($"{name,-16} {method,-9} {timings[3],6:F1}ms mask={Cv2.CountNonZero(usedMask),6} tiles={first.FullTiles}/{first.ReducedTiles} coverage={coverage:P1} MAE={mae:F1}");
            }
            void Preview(Mat selected, string label)
            {
                using var preview = source.Clone(); preview.SetTo(new Scalar(40, 40, 235), selected);
                Cv2.ImWrite(prefix + "-" + label + ".png", preview);
            }
        }
    }

    private static CpuRepair Repair(string method, Mat source, Mat mask, Mat oldMask) => method switch
    {
        "baseline" => new(Program.RepairReduced(source, oldMask), 0, 0),
        "new-half" => new(Program.RepairReduced(source, mask), 0, 0),
        _ => CpuHoleRepair.Repair(source, mask),
    };

    internal static void Verify()
    {
        // Nothing in the picture but a slope, so the fill that carries slopes is the one that answers
        // — and what it puts back has to be the slope, not the average of what surrounds the hole.
        using var slope = Scene(143, 213, speckle: 0);
        using var mask = new Mat(slope.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(mask, new Rect(92, 15, 8, 110), Scalar.White, -1); // Crosses a tile boundary.
        using var covered = slope.Clone();
        covered.SetTo(Scalar.White, mask);
        using var carried = CpuHoleRepair.Repair(covered, mask);
        if (carried.FullTiles != 0 || carried.ReducedTiles != 0) throw new Exception("A slope must not be inpainted.");
        if (Program.ChangedOutside(covered, carried.Image, mask) != 0) throw new Exception("Smooth writes escaped mask.");
        using var error = new Mat();
        Cv2.Absdiff(slope, carried.Image, error);
        if (Cv2.Mean(error, mask).Val0 > 4) throw new Exception("The slope was not carried across the hole.");

        // The same holes over a picture with detail in it, where inpainting answers and routes itself
        // by how thick each tile's holes are.
        using var detailed = Scene(143, 213, speckle: 40);
        using var speckled = detailed.Clone();
        speckled.SetTo(Scalar.White, mask);
        using var thin = CpuHoleRepair.Repair(speckled, mask);
        if (thin.FullTiles == 0 || thin.ReducedTiles != 0) throw new Exception("Thin holes must use full resolution.");
        if (Program.ChangedOutside(speckled, thin.Image, mask) != 0) throw new Exception("Tile writes escaped mask.");

        using var mixedMask = mask.Clone();
        Cv2.Rectangle(mixedMask, new Rect(145, 30, 25, 50), Scalar.White, -1);
        using var mixedSource = detailed.Clone();
        mixedSource.SetTo(Scalar.White, mixedMask);
        using var mixed = CpuHoleRepair.Repair(mixedSource, mixedMask);
        if (mixed.FullTiles == 0 || mixed.ReducedTiles == 0) throw new Exception("Mixed holes must exercise both resolutions.");
        if (Program.ChangedOutside(mixedSource, mixed.Image, mixedMask) != 0) throw new Exception("Mixed writes escaped mask.");

        using var empty = new Mat(slope.Size(), MatType.CV_8UC1, Scalar.Black);
        using var untouched = CpuHoleRepair.Repair(covered, empty);
        if (Cv2.Norm(covered, untouched.Image, NormTypes.INF) != 0) throw new Exception("Empty mask changed scene.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { using var cancelled = CpuHoleRepair.Repair(covered, mask, cancellation.Token); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        using var tiny = new Mat(1, 1, MatType.CV_8UC3, Scalar.White);
        using var tinyMask = CpuTextMask.Build(tiny, [new(0, 0, 1, 1, null)]);
        using var tinyRepair = CpuHoleRepair.Repair(tiny, tinyMask);
        Console.WriteLine("PASS slope carried, full/reduced routing, mixed resolution boundary, outside-mask isolation, empty mask, cancellation, 1px input.");
    }

    /// <summary>A slanted ramp, optionally speckled hard enough that it reads as detail and not shading.</summary>
    private static Mat Scene(int rows, int cols, int speckle)
    {
        var scene = new Mat(rows, cols, MatType.CV_8UC3);
        var indexer = scene.GetGenericIndexer<Vec3b>();
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < cols; x++)
        {
            double along = (x + y * 1.0) / (cols + rows);
            int noise = speckle == 0 ? 0 : (x * 911 + y * 104729) % (speckle * 2 + 1) - speckle;
            byte Level(int bias) => (byte)Math.Clamp(30 + 170 * along + bias + noise, 0, 255);
            indexer[y, x] = new Vec3b(Level(-20), Level(0), Level(20));
        }
        return scene;
    }
}
