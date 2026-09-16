using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using CvRect = OpenCvSharp.Rect;
using CvSize = OpenCvSharp.Size;
using MediaColor = System.Windows.Media.Color;
using Sd = System.Drawing;
using WpfRect = System.Windows.Rect;

/// <summary>
/// Throwaway: composes the candidate capture-translation bubble backgrounds onto real screenshots
/// so the blur, wash and feather amounts are chosen by eye before any of it reaches the app.
/// </summary>
internal static class Program
{
    // OverlayWindow's own expansion of the OCR box. Everything here works in captured pixels, as
    // the sampler and the repair do; the overlay's DPI divide is a placement concern, not this one.
    private const double BubbleExpand = 2;
    private const string Placeholder = "這是一段用來檢查可讀性的譯文範例文字";

    /// <summary>
    /// How much wider than the source the translation is pretended to be, from --overflow[=n].
    /// The question it exists for: a translation is routinely longer than what it replaces, so the
    /// bubble grows past the thing the source was printed on, and what the background does out
    /// there is not visible on a capture where every bubble happens to fit.
    /// </summary>
    private const double DefaultOverflow = 1.45;
    private static double _overflow = 1;

    private sealed record Variant(string Name, string Mode);

    // What the application will actually draw, against what it drew before. The parameter sweep
    // that chose the blur, wash and feather lived here too; it has served its purpose and gone,
    // and "shipping" now runs CaptureBubbleBackdrop itself rather than a copy of it.
    private static readonly Variant[] Variants =
    [
        new("00-original", "original"),
        new("10-solid", "solid"),
        new("40-shipping", "shipping"),
    ];

    private sealed record ProbeBlock(string Text, WpfRect Bounds, IReadOnlyList<WpfRect> Lines, double? GlyphHeight);
    private sealed record CachedBlock(string Text, double[] Bounds, double[][] Lines, double? GlyphHeight);
    /// <param name="Wash">What the capture sampled: the old flat card, and the tint on a plate.</param>
    /// <param name="Flat">What the overlay paints when no plate was built — see CaptureBubbleBackdrop.Card.</param>
    private sealed record Card(CvRect Rect, MediaColor Wash, MediaColor Text, double GlyphHeight,
        double FontSize, string Content, CvRect PlateRect, byte[]? Plate, MediaColor Flat, MediaColor FlatText);

    private static OcrService? _ocr;

    [STAThread]
    private static void Main(string[] args)
    {
        Cv2.SetNumThreads(4);
        string root = FindRoot();
        string output = Path.Combine(root, "artifacts/capture-bubble");
        Directory.CreateDirectory(output);
        try
        {
            _overflow = args.FirstOrDefault(argument => argument.StartsWith("--overflow")) is { } flag
                ? (flag.Contains('=')
                    ? double.Parse(flag.Split('=')[1], CultureInfo.InvariantCulture)
                    : DefaultOverflow)
                : 1;
            var images = args.Where(argument => !argument.StartsWith("--")).ToArray();
            foreach (string image in images.Length > 0 ? images : DefaultImages(root))
            {
                Console.WriteLine(Path.GetFileName(image));
                Process(output, image);
            }
        }
        finally { _ocr?.Dispose(); }
    }

    private static string[] DefaultImages(string root) =>
    [
        Path.Combine(root, ".ai/test-images/screen-panel-en-ja/OverTranslate_20260816_010025451.png"),
        Path.Combine(root, ".ai/test-images/screen-panel-en-ja/OverTranslate_20260816_010326136.png"),
        Path.Combine(root, ".ai/test-images/region-comic-en/comic-en.png"),
        Path.Combine(root, ".ai/test-images/region-comic-en/comic-en5.png"),
        Path.Combine(root, ".ai/test-images/region-web-ja/bang-jp.png"),
        Path.Combine(root, ".ai/test-images/web-v2/en1.png"),
    ];

    private static void Process(string output, string path)
    {
        string folder = Path.Combine(output,
            Path.GetFileName(Path.GetDirectoryName(path)!) + "-" + Path.GetFileNameWithoutExtension(path)
            + (_overflow > 1 ? $"-overflow{_overflow:0.##}" : ""));
        Directory.CreateDirectory(folder);

        using var loaded = new Sd.Bitmap(path);
        using var frame = loaded.Clone(new Sd.Rectangle(0, 0, loaded.Width, loaded.Height),
            Sd.Imaging.PixelFormat.Format24bppRgb);

        var blocks = Recognise(folder, frame);
        if (blocks.Count == 0) { Console.WriteLine("  no text"); return; }
        Console.WriteLine($"  {blocks.Count} blocks");

        using var source = ToMat(frame);
        var regions = blocks
            .SelectMany(b => b.Lines.Select(l => new CpuTextRegion(l.X, l.Y, l.Width, l.Height, b.GlyphHeight)))
            .ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var translated = blocks
            .Select(b => new TranslatedBlock(b.Text, "x", b.Bounds, b.Lines, b.GlyphHeight))
            .ToList();
        var backdrop = CaptureBubbleBackdrop.Create(frame, translated);
        Console.WriteLine($"  backdrop {clock.ElapsedMilliseconds}ms for {source.Width}x{source.Height}");

        var cards = blocks.Select(b => Compose(frame, b, backdrop, source.Width, source.Height)).ToArray();

        foreach (var variant in Variants)
        {
            using var canvas = source.Clone();
            if (variant.Mode != "original")
                foreach (var card in cards) Paint(canvas, card, variant);
            using var image = ToBitmap(canvas);
            if (variant.Mode != "original") DrawText(image, cards, variant);
            image.Save(Path.Combine(folder, variant.Name + ".png"), Sd.Imaging.ImageFormat.Png);
        }
    }

    private static Card Compose(
        Sd.Bitmap frame, ProbeBlock block, CaptureBubbleBackdrop? backdrop, int width, int height)
    {
        var b = block.Bounds;
        var size = new CvSize(width, height);
        var rect = Clip(b.X - BubbleExpand, b.Y - BubbleExpand,
            b.X + b.Width * _overflow + BubbleExpand, b.Bottom + BubbleExpand, size);
        var (wash, text) = SourceTextColorSampler.ForCaptureOverlay(frame, b, block.Lines, false);
        double glyph = block.GlyphHeight ?? (block.Lines.Count > 0 ? block.Lines.Min(l => l.Height) : b.Height);
        double font = SourceFontScale.Calculate(glyph, true);

        // The overlay grows the background element by the feather and leaves the text where it
        // was; this mirrors that. Not clipped to the frame: a plate comes back at the size asked
        // for even where the capture does not reach, so the paste below is what has to cope.
        double feather = CaptureBubbleBackdrop.Feather(glyph);
        var plateRect = new CvRect(
            (int)Math.Floor(rect.X - feather), (int)Math.Floor(rect.Y - feather),
            (int)Math.Ceiling(rect.Right + feather) - (int)Math.Floor(rect.X - feather),
            (int)Math.Ceiling(rect.Bottom + feather) - (int)Math.Floor(rect.Y - feather));
        byte[]? plate = null;
        var area = new WpfRect(rect.X - feather, rect.Y - feather, rect.Width + feather * 2, rect.Height + feather * 2);
        if (backdrop?.Plate(area, wash, text, glyph, font) is { } built)
        {
            text = built.Text;
            var image = (System.Windows.Media.Imaging.BitmapSource)built.Brush.ImageSource;
            plate = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(plate, image.PixelWidth * 4, 0);
            plateRect = new CvRect(plateRect.X, plateRect.Y, image.PixelWidth, image.PixelHeight);
        }
        var flat = backdrop?.Card(new WpfRect(rect.X, rect.Y, rect.Width, rect.Height), text);

        return new(rect, wash, text, glyph, font,
            Fill(b.Width * _overflow, font), plateRect, plate,
            flat?.Background ?? wash, flat?.Text ?? text);
    }

    private static string Fill(double width, double fontSize)
    {
        int count = Math.Max(1, (int)Math.Round(width / Math.Max(1, fontSize)));
        var builder = new StringBuilder();
        while (builder.Length < count) builder.Append(Placeholder);
        return builder.ToString(0, count);
    }

    private static void Paint(Mat canvas, Card card, Variant variant)
    {
        if (variant.Mode == "solid")
        {
            Cv2.Rectangle(canvas, card.Rect, new Scalar(card.Wash.B, card.Wash.G, card.Wash.R), -1);
            return;
        }

        // Source-over, which is all WPF does with the same brush. Everything that decides how this
        // looks - the repair, the blur, the wash, the contrast lift, the edge ramp - happened
        // inside CaptureBubbleBackdrop, so what comes out here is what the overlay will show.
        // No plate means the backdrop judged a flat card the better answer, and the overlay then
        // draws exactly the card it drew before this existed.
        if (card.Plate is not { } plate)
        {
            var flat = variant.Mode == "solid" ? card.Wash : card.Flat;
            Cv2.Rectangle(canvas, card.Rect, new Scalar(flat.B, flat.G, flat.R), -1);
            return;
        }
        var rect = card.PlateRect;
        for (int y = Math.Max(0, -rect.Y); y < rect.Height && rect.Y + y < canvas.Height; y++)
        {
            for (int x = Math.Max(0, -rect.X); x < rect.Width && rect.X + x < canvas.Width; x++)
            {
                int i = (y * rect.Width + x) * 4;
                double alpha = plate[i + 3] / 255.0;
                if (alpha <= 0) continue;
                var pixel = canvas.At<Vec3b>(rect.Y + y, rect.X + x);
                canvas.Set(rect.Y + y, rect.X + x, new Vec3b(
                    (byte)Math.Round(pixel.Item0 * (1 - alpha) + plate[i] * alpha),
                    (byte)Math.Round(pixel.Item1 * (1 - alpha) + plate[i + 1] * alpha),
                    (byte)Math.Round(pixel.Item2 * (1 - alpha) + plate[i + 2] * alpha)));
            }
        }
    }

    private static void DrawText(Sd.Bitmap image, IReadOnlyList<Card> cards, Variant variant)
    {
        using var graphics = Sd.Graphics.FromImage(image);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var format = new Sd.StringFormat
        {
            Alignment = Sd.StringAlignment.Near,
            LineAlignment = Sd.StringAlignment.Center,
            FormatFlags = Sd.StringFormatFlags.NoWrap,
            Trimming = Sd.StringTrimming.None,
        };
        foreach (var card in cards)
        {
            using var font = new Sd.Font("Microsoft JhengHei", (float)card.FontSize, Sd.FontStyle.Bold, Sd.GraphicsUnit.Pixel);
            // The old card kept the sampled colour; the shipping one answers its own.
            var colour = card.Plate is not null || variant.Mode == "solid" ? card.Text : card.FlatText;
            using var brush = new Sd.SolidBrush(Sd.Color.FromArgb(colour.R, colour.G, colour.B));
            graphics.DrawString(card.Content, font, brush,
                new Sd.RectangleF(card.Rect.X + 3, card.Rect.Y, card.Rect.Width - 6, card.Rect.Height), format);
        }
    }

    private static List<ProbeBlock> Recognise(string folder, Sd.Bitmap frame)
    {
        string cache = Path.Combine(folder, "ocr.json");
        if (File.Exists(cache))
            return JsonSerializer.Deserialize<List<CachedBlock>>(File.ReadAllText(cache))!.Select(Restore).ToList();

        _ocr ??= new OcrService();
        var blocks = _ocr.RecognizeAsync(frame, "AUTO").GetAwaiter().GetResult();
        var cached = blocks.Select(b => new CachedBlock(b.Text,
            [b.Bounds.X, b.Bounds.Y, b.Bounds.Width, b.Bounds.Height],
            [.. b.Lines.Select(l => new[] { l.X, l.Y, l.Width, l.Height })],
            b.RenderGlyphHeight)).ToList();
        File.WriteAllText(cache, JsonSerializer.Serialize(cached));
        return cached.Select(Restore).ToList();
    }

    private static ProbeBlock Restore(CachedBlock block) => new(block.Text,
        new WpfRect(block.Bounds[0], block.Bounds[1], block.Bounds[2], block.Bounds[3]),
        [.. block.Lines.Select(l => new WpfRect(l[0], l[1], l[2], l[3]))], block.GlyphHeight);

    private static CvRect Clip(double left, double top, double right, double bottom, CvSize size)
    {
        int x = (int)Math.Clamp(Math.Floor(left), 0, size.Width);
        int y = (int)Math.Clamp(Math.Floor(top), 0, size.Height);
        return new(x, y,
            (int)Math.Clamp(Math.Ceiling(right), x, size.Width) - x,
            (int)Math.Clamp(Math.Ceiling(bottom), y, size.Height) - y);
    }

    private static Mat ToMat(Sd.Bitmap bitmap)
    {
        var mat = new Mat(bitmap.Height, bitmap.Width, MatType.CV_8UC3);
        Transfer(bitmap, mat, toMat: true);
        return mat;
    }

    private static Sd.Bitmap ToBitmap(Mat mat)
    {
        var bitmap = new Sd.Bitmap(mat.Width, mat.Height, Sd.Imaging.PixelFormat.Format24bppRgb);
        Transfer(bitmap, mat, toMat: false);
        return bitmap;
    }

    private static void Transfer(Sd.Bitmap bitmap, Mat mat, bool toMat)
    {
        var bounds = new Sd.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds,
            toMat ? Sd.Imaging.ImageLockMode.ReadOnly : Sd.Imaging.ImageLockMode.WriteOnly,
            Sd.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[bitmap.Width * 3];
            for (int y = 0; y < bounds.Height; y++)
            {
                var pixels = IntPtr.Add(data.Scan0, y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(toMat ? pixels : mat.Ptr(y), row, 0, row.Length);
                System.Runtime.InteropServices.Marshal.Copy(row, 0, toMat ? mat.Ptr(y) : pixels, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
            && !File.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run inside the repository.");
    }
}
