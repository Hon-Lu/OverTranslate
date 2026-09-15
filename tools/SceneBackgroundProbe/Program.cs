using System.IO;
using System.Text.Json;
using OpenCvSharp;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using TextRegion = OverTranslate.Services.Realtime.CpuTextRegion;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Cv2.SetNumThreads(2);
        if (args.Contains("--verify")) { CpuAdaptiveProbe.Verify(); return; }
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
            && !File.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        string root = directory?.FullName ?? throw new InvalidOperationException("Run inside the repository.");
        PrepareOcr(root);
        CpuAdaptiveProbe.Run(root);
    }

    private static void PrepareOcr(string root)
    {
        string output = Path.Combine(root, "artifacts/inpaint-research");
        Directory.CreateDirectory(output);
        OnnxOcrEngine? ocr = null;
        try
        {
            foreach (string file in Directory.GetFiles(Path.Combine(root, ".ai/test-images/chat-room"), "*.png").Order())
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string cache = Path.Combine(output, name + "-ocr.json");
                string language = name.StartsWith("ko-") ? "KO" : "AUTO";
                if (File.Exists(cache) && File.Exists(cache + ".language") && File.ReadAllText(cache + ".language") == language) continue;
                ocr ??= new OnnxOcrEngine();
                using var bitmap = new System.Drawing.Bitmap(file);
                int size = RealtimeDetectorSize.For(bitmap.Width, bitmap.Height, RealtimeBlockMode.Panel).Primary;
                var blocks = ocr.TryRecognizeAsync(bitmap, language, size).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("OCR busy.");
                var regions = blocks.SelectMany(b => (b.SourceLineBounds is { Count: > 0 } lines ? lines : [b.Bounds])
                    .Select(r => new TextRegion(r.X, r.Y, r.Width, r.Height, b.RenderGlyphHeight))).ToArray();
                File.WriteAllText(cache, JsonSerializer.Serialize(regions));
                File.WriteAllText(cache + ".language", language);
            }
        }
        finally { ocr?.Dispose(); }
    }

    internal static Mat RepairReduced(Mat source, Mat mask) => CpuHoleRepair.RepairReduced(source, mask);

    internal static int ChangedOutside(Mat source, Mat result, Mat mask)
    {
        using var difference = new Mat();
        using var channels = new Mat();
        using var inverse = new Mat();
        Cv2.Absdiff(source, result, difference);
        Cv2.InRange(difference, Scalar.All(0), Scalar.All(0), channels);
        Cv2.BitwiseNot(channels, channels);
        Cv2.BitwiseNot(mask, inverse);
        Cv2.BitwiseAnd(channels, inverse, channels);
        return Cv2.CountNonZero(channels);
    }

}

internal static class GlyphMask
{
    public static Mat Build(Mat source, IReadOnlyList<TextRegion> regions)
    {
        var mask = new Mat(source.Size(), MatType.CV_8UC1, Scalar.Black);
        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
            foreach (var line in regions)
            {
                double height = Math.Clamp(line.GlyphHeight ?? line.Height, 6, line.Height);
                int pad = Math.Clamp((int)Math.Ceiling(height * .15), 2, 5);
                int left = Math.Clamp((int)Math.Floor(line.X) - pad, 0, source.Width);
                int top = Math.Clamp((int)Math.Floor(line.Y) - pad, 0, source.Height);
                int right = Math.Clamp((int)Math.Ceiling(line.X + line.Width) + pad, 0, source.Width);
                int bottom = Math.Clamp((int)Math.Ceiling(line.Y + line.Height) + pad, 0, source.Height);
                if (right <= left || bottom <= top) continue;
                var box = new Rect(left, top, right - left, bottom - top);
                using var roi = new Mat(gray, box);
                using var bright = new Mat();
                using var dark = new Mat();
                using var response = new Mat();
                using var binary = new Mat();
                int size = Math.Clamp((int)Math.Round(height * .55) | 1, 5, 25);
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(size, size));
                Cv2.MorphologyEx(roi, bright, MorphTypes.TopHat, kernel);
                Cv2.MorphologyEx(roi, dark, MorphTypes.BlackHat, kernel);
                Cv2.Max(bright, dark, response);
                double threshold = Cv2.Threshold(response, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                Cv2.Threshold(response, binary, Math.Max(16, threshold * .65), 255, ThresholdTypes.Binary);
                using var grow = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(pad * 2 + 1, pad * 2 + 1));
                Cv2.Dilate(binary, binary, grow);
                using var target = new Mat(mask, box);
                Cv2.BitwiseOr(target, binary, target);
            }
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }
}
