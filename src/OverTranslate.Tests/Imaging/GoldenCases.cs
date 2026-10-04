using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OverTranslate.Imaging;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using MediaColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Rect;

namespace OverTranslate.Tests.Imaging;

/// <summary>
/// Everything the golden test hashes, built from nothing but integer arithmetic so the inputs are
/// the same on every machine and every run.
/// </summary>
/// <remarks>
/// <para>The expected hashes in <c>Fixtures/Imaging/golden.json</c> were recorded with OpenCV
/// 4.13.0 doing the work (the build that still had both, with every family routed to OpenCV), and
/// then checked to come out identical with the managed routines. The test only reads that file:
/// nothing here needs OpenCV to run.</para>
///
/// <para>Two kinds of case. Scenes go through the whole repair the way the app calls it — a frame
/// and its recognition boxes in, the repaired frame and every bubble plate out — over the paths
/// the repair can take: both polarities, small and display-sized glyphs, a box against the frame
/// edge, a slope, busy texture, a mask too dense for full resolution. Routines call one function
/// each on the inputs where the replaced library's arithmetic is easiest to get wrong: views read
/// through to their parent, widths that are not a multiple of the vector width, values that land
/// exactly on a rounding tie.</para>
/// </remarks>
internal static unsafe class GoldenCases
{
    public static IEnumerable<(string Name, Func<byte[]> Run)> All() => Scenes().Concat(Routines());

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // ---- deterministic inputs -----------------------------------------------------------------

    /// <summary>SplitMix64: small, fast, and the same everywhere.</summary>
    private sealed class Random64(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public int Next(int below) => (int)(Next() % (ulong)below);
    }

    private static ImageBuffer RandomBytes(int width, int height, PixelType type, ulong seed, int below = 256)
    {
        var random = new Random64(seed);
        var image = new ImageBuffer(width, height, type);
        for (int y = 0; y < height; y++)
        {
            byte* row = image.Row(y);
            for (int x = 0; x < image.RowBytes; x++) row[x] = (byte)random.Next(below);
        }
        return image;
    }

    /// <summary>Floats on a grid of 1/64ths, so sums are exact and ties are common.</summary>
    private static ImageBuffer RandomFloats(int width, int height, PixelType type, ulong seed, int below = 255 * 64)
    {
        var random = new Random64(seed);
        var image = new ImageBuffer(width, height, type);
        for (int y = 0; y < height; y++)
        {
            float* row = image.Row<float>(y);
            for (int x = 0; x < width * image.Channels; x++) row[x] = random.Next(below) / 64f;
        }
        return image;
    }

    /// <summary>A smooth picture with texture on it: two slow waves, a ramp, and grain.</summary>
    private static ImageBuffer Picture(int width, int height, ulong seed, int grain, bool flat = false)
    {
        var random = new Random64(seed);
        var image = new ImageBuffer(width, height, PixelType.U8C3);
        int phase = random.Next(1024);
        for (int y = 0; y < height; y++)
        {
            byte* row = image.Row(y);
            for (int x = 0; x < width; x++)
            {
                // Integer waves: a triangle of period 160 across and 96 down.
                int across = Math.Abs((x * 4 + phase) % 320 - 160) - 80;
                int down = Math.Abs((y * 5 + phase) % 192 - 96) - 48;
                int ramp = x * 60 / Math.Max(1, width) + y * 40 / Math.Max(1, height);
                int wave = flat ? 0 : across / 2 + down / 2;
                for (int c = 0; c < 3; c++)
                {
                    int noise = grain > 0 ? random.Next(grain * 2 + 1) - grain : 0;
                    row[x * 3 + c] = (byte)Math.Clamp(70 + c * 25 + ramp + wave + noise, 0, 255);
                }
            }
        }
        return image;
    }

    /// <summary>Strokes like a line of text: bars, with an outline and a fade, antialiased by 4×4 coverage.</summary>
    private static void Glyphs(ImageBuffer image, Rectangle line, int glyph, bool bright, ulong seed)
    {
        var random = new Random64(seed);
        int stroke = Math.Max(2, glyph / 6), outline = Math.Max(1, glyph / 12);
        byte body = bright ? (byte)245 : (byte)20, edge = bright ? (byte)15 : (byte)235;
        var bars = new List<Rectangle>();
        for (int x = line.X; x + stroke < line.Right;)
        {
            int height = glyph / 2 + random.Next(glyph / 2 + 1);
            int top = line.Y + (line.Height - height) / 2;
            bars.Add(new Rectangle(x, top, stroke, height));
            if (random.Next(3) == 0) bars.Add(new Rectangle(x, top + height / 3, stroke * 3, stroke));
            x += stroke * 2 + random.Next(stroke * 3 + 1);
        }
        for (int y = Math.Max(0, line.Y - glyph); y < Math.Min(image.Height, line.Bottom + glyph); y++)
        {
            byte* row = image.Row(y);
            for (int x = Math.Max(0, line.X - glyph); x < Math.Min(image.Width, line.Right + glyph); x++)
            {
                int covered = 0, ringed = 0;
                for (int sy = 0; sy < 4; sy++)
                for (int sx = 0; sx < 4; sx++)
                {
                    int px = x * 4 + sx, py = y * 4 + sy;
                    bool inBody = false, inEdge = false;
                    foreach (var bar in bars)
                    {
                        if (px >= bar.X * 4 && px < bar.Right * 4 && py >= bar.Y * 4 && py < bar.Bottom * 4) inBody = true;
                        else if (px >= (bar.X - outline) * 4 && px < (bar.Right + outline) * 4
                            && py >= (bar.Y - outline) * 4 && py < (bar.Bottom + outline) * 4) inEdge = true;
                    }
                    if (inBody) covered++;
                    else if (inEdge) ringed++;
                }
                if (covered + ringed == 0) continue;
                for (int c = 0; c < 3; c++)
                    row[x * 3 + c] = (byte)((row[x * 3 + c] * (16 - covered - ringed) + body * covered + edge * ringed + 8) / 16);
            }
        }
    }

    // ---- scenes -------------------------------------------------------------------------------

    private record Scene(string Name, int Width, int Height, ulong Seed, int Grain, bool Flat, (Rectangle Line, int Glyph, bool Bright)[] Lines);

    private static readonly Scene[] SceneList =
    [
        new("subtitle-bright", 640, 220, 1, 6, false, [(new(150, 140, 340, 44), 40, true)]),
        new("subtitle-dark", 640, 220, 2, 6, false, [(new(120, 30, 380, 40), 36, false)]),
        new("two-lines", 640, 220, 3, 4, false, [(new(100, 90, 420, 34), 30, true), (new(130, 136, 360, 34), 30, true)]),
        new("panel-small", 420, 200, 4, 3, false,
            [(new(20, 20, 300, 16), 14, true), (new(20, 42, 260, 16), 14, true), (new(20, 64, 330, 16), 14, false), (new(20, 120, 200, 16), 14, false)]),
        new("display-large", 640, 300, 5, 5, false, [(new(60, 60, 500, 130), 110, true)]),
        new("against-edge", 400, 160, 6, 6, false, [(new(-10, 120, 260, 44), 40, true), (new(250, -6, 170, 30), 26, false)]),
        new("slope", 640, 220, 7, 0, true, [(new(150, 90, 340, 40), 36, true)]),
        new("busy", 480, 200, 8, 40, false, [(new(80, 80, 300, 40), 36, false)]),
        new("dense", 520, 240, 9, 2, false,
            [(new(40, 40, 440, 44), 44, true), (new(40, 86, 440, 44), 44, true), (new(40, 132, 440, 44), 44, true)]),
        new("far-apart", 1100, 320, 10, 5, false, [(new(30, 30, 200, 30), 26, true), (new(800, 260, 260, 34), 30, false)]),
        new("tiny", 3, 3, 11, 0, false, [(new(0, 0, 3, 3), 3, true)]),
    ];

    private static IEnumerable<(string, Func<byte[]>)> Scenes()
    {
        foreach (var scene in SceneList)
            yield return ("scene/" + scene.Name, () => RunScene(scene));
    }

    private static byte[] RunScene(Scene scene)
    {
        using var picture = Picture(scene.Width, scene.Height, scene.Seed, scene.Grain, scene.Flat);
        var blocks = new List<TranslatedBlock>();
        foreach (var (line, glyph, bright) in scene.Lines)
        {
            Glyphs(picture, line, glyph, bright, scene.Seed * 31 + (ulong)blocks.Count);
            var bounds = new WpfRect(line.X - 2.5, line.Y - 1.75, line.Width + 5, line.Height + 3.5);
            blocks.Add(new TranslatedBlock("x", "譯", bounds, null, glyph));
        }
        using var frame = ToBitmap(picture);
        using var repaired = RealtimeCpuBackground.Repair(frame, blocks);
        using var output = new MemoryStream();
        output.Write(Bytes(repaired));
        var backdrop = CaptureBubbleBackdrop.FromRepaired(repaired)!;
        var washes = new[] { MediaColor.FromRgb(0, 0, 0), MediaColor.FromRgb(255, 255, 255), MediaColor.FromRgb(40, 90, 160) };
        var texts = new[] { MediaColor.FromRgb(255, 255, 255), MediaColor.FromRgb(0, 0, 0), MediaColor.FromRgb(250, 220, 90) };
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            double glyph = b.RenderGlyphHeight ?? b.Bounds.Height;
            double feather = CaptureBubbleBackdrop.Feather(glyph);
            var area = new WpfRect(b.Bounds.X - feather, b.Bounds.Y - feather, b.Bounds.Width + feather * 2, b.Bounds.Height + feather * 2);
            if (backdrop.Plate(area, washes[i % 3], texts[i % 3], glyph, b.Bounds.Height) is not { } plate)
            {
                output.WriteByte(0);
                continue;
            }
            var source = (System.Windows.Media.Imaging.BitmapSource)plate.Brush.ImageSource;
            var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
            source.CopyPixels(pixels, source.PixelWidth * 4, 0);
            output.Write(BitConverter.GetBytes(source.PixelWidth));
            output.Write(BitConverter.GetBytes(source.PixelHeight));
            output.Write([plate.Text.R, plate.Text.G, plate.Text.B]);
            output.Write(pixels);
        }
        return output.ToArray();
    }

    private static Bitmap ToBitmap(ImageBuffer image)
    {
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < image.Height; y++)
                Buffer.MemoryCopy(image.Row(y), (byte*)data.Scan0 + (long)y * data.Stride, image.RowBytes, image.RowBytes);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static byte[] Bytes(Bitmap bitmap)
    {
        int rowBytes = bitmap.Width * 3;
        var result = new byte[rowBytes * bitmap.Height];
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, result, y * rowBytes, rowBytes);
        }
        finally { bitmap.UnlockBits(data); }
        return result;
    }

    private static byte[] Bytes(ImageBuffer image)
    {
        var result = new byte[(long)image.RowBytes * image.Height];
        for (int y = 0; y < image.Height; y++) image.RowSpan(y).CopyTo(result.AsSpan(y * image.RowBytes));
        return result;
    }

    private static byte[] Bytes(params double[] values) => [.. values.SelectMany(BitConverter.GetBytes)];

    // ---- routines -----------------------------------------------------------------------------

    /// <summary>A view of a random image, so the routine has a parent to read through to.</summary>
    private static (ImageBuffer Parent, ImageBuffer View) Viewed(int width, int height, PixelType type, Rectangle area, ulong seed, int below = 256)
    {
        var parent = type.IsFloat() ? RandomFloats(width, height, type, seed) : RandomBytes(width, height, type, seed, below);
        return (parent, new ImageBuffer(parent, area));
    }

    private static List<(string, Func<byte[]>)> Routines()
    {
        var cases = new List<(string, Func<byte[]>)>();
        cases.Add(("gray/u8", () =>
        {
            using var source = RandomBytes(37, 5, PixelType.U8C3, 101);
            using var gray = ColorConversion.ToGray(source);
            return Bytes(gray);
        }));
        cases.Add(("gray/f32", () =>
        {
            // 29 wide: three whole runs of eight and a tail of five, which round differently.
            using var source = RandomFloats(29, 4, PixelType.F32C3, 102);
            using var gray = ColorConversion.ToGray(source);
            return Bytes(gray);
        }));
        foreach (int size in new[] { 5, 25 })
        {
            cases.Add(($"hats/ellipse{size}-view", () =>
            {
                var (parent, view) = Viewed(160, 90, PixelType.U8C1, new Rectangle(17, 9, 120, 60), 103 + (ulong)size);
                using (parent)
                using (view)
                {
                    var element = StructuringElement.Ellipse(size, size);
                    using var light = ImageBuffer.Uninitialized(view.Size, PixelType.U8C1);
                    using var dark = ImageBuffer.Uninitialized(view.Size, PixelType.U8C1);
                    Morphology.TopHat(view, light, element);
                    Morphology.BlackHat(view, dark, element);
                    return [.. Bytes(light), .. Bytes(dark)];
                }
            }));
        }
        cases.Add(("dilate/cross-twice-view", () =>
        {
            var (parent, view) = Viewed(70, 50, PixelType.U8C1, new Rectangle(0, 3, 64, 40), 105, 2);
            using (parent)
            using (view)
            {
                using var target = new ImageBuffer(view.Size, PixelType.U8C1);
                Morphology.Dilate(view, target, StructuringElement.Ellipse(3, 3), 2);
                return Bytes(target);
            }
        }));
        cases.Add(("morphology/f32-rect", () =>
        {
            using var source = RandomFloats(53, 31, PixelType.F32C1, 106);
            using var eroded = new ImageBuffer(source.Size, PixelType.F32C1);
            using var dilated = new ImageBuffer(source.Size, PixelType.F32C1);
            Morphology.Erode(source, eroded, StructuringElement.Rectangle(3, 3));
            Morphology.Dilate(source, dilated, StructuringElement.Rectangle(13, 13));
            return [.. Bytes(eroded), .. Bytes(dilated)];
        }));
        var resizes = new (string Name, PixelType Type, Size From, Size To, Interpolation How)[]
        {
            ("area-u8c3-fraction", PixelType.U8C3, new(101, 37), new(50, 18), Interpolation.Area),
            ("area-u8c1-half", PixelType.U8C1, new(66, 34), new(33, 17), Interpolation.Area),
            ("area-f32c3-half", PixelType.F32C3, new(46, 24), new(23, 12), Interpolation.Area),
            ("area-f32c1-fraction", PixelType.F32C1, new(47, 25), new(15, 8), Interpolation.Area),
            ("linear-u8c3-double", PixelType.U8C3, new(45, 21), new(90, 42), Interpolation.Linear),
            ("linear-u8c1-fraction", PixelType.U8C1, new(31, 17), new(77, 40), Interpolation.Linear),
            ("linear-f32c3-up", PixelType.F32C3, new(12, 7), new(23, 13), Interpolation.Linear),
            ("nearest-f32c1-down", PixelType.F32C1, new(57, 41), new(14, 10), Interpolation.Nearest),
            ("nearest-u8c3-down", PixelType.U8C3, new(150, 61), new(25, 15), Interpolation.Nearest),
        };
        foreach (var r in resizes)
        {
            cases.Add(("resize/" + r.Name, () =>
            {
                using var source = r.Type.IsFloat() ? RandomFloats(r.From.Width, r.From.Height, r.Type, 107) : RandomBytes(r.From.Width, r.From.Height, r.Type, 107);
                using var target = Resize.To(source, r.To, r.How);
                return Bytes(target);
            }));
        }
        cases.Add(("blur/u8-5-view", () =>
        {
            var (parent, view) = Viewed(60, 40, PixelType.U8C1, new Rectangle(1, 2, 50, 30), 108);
            using (parent)
            using (view)
            {
                using var target = new ImageBuffer(view.Size, PixelType.U8C1);
                Filters.Blur(view, target, 5);
                return Bytes(target);
            }
        }));
        cases.Add(("blur/f32-3-in-place", () =>
        {
            using var image = RandomFloats(41, 23, PixelType.F32C1, 109);
            Filters.Blur(image, image, 3);
            return Bytes(image);
        }));
        cases.Add(("boxsum/33-view", () =>
        {
            var (parent, view) = Viewed(90, 70, PixelType.U8C1, new Rectangle(20, 10, 50, 40), 110);
            using (parent)
            using (view)
            using (var sum = Filters.BoxSum(view, 33))
                return Bytes(sum);
        }));
        cases.Add(("laplacian/f32", () =>
        {
            using var source = RandomFloats(33, 19, PixelType.F32C1, 111);
            using var target = Filters.Laplacian(source);
            return Bytes(target);
        }));
        foreach (double sigma in new[] { 1.0, 4.3, 16.0 })
        {
            cases.Add(($"gaussian/sigma{sigma}", () =>
            {
                using var source = RandomBytes(60, 40, PixelType.U8C3, 112);
                using var target = Filters.GaussianBlur(source, sigma);
                return Bytes(target);
            }));
        }
        cases.Add(("threshold/otsu-and-binary", () =>
        {
            using var source = RandomBytes(57, 23, PixelType.U8C1, 113, 90);
            double otsu = Threshold.Otsu(source);
            using var target = new ImageBuffer(source.Size, PixelType.U8C1);
            Threshold.Binary(source, target, Math.Max(18, otsu * .8) + .9, 255);
            return [.. Bytes(otsu), .. Bytes(target)];
        }));
        cases.Add(("threshold/f32", () =>
        {
            using var source = RandomFloats(40, 9, PixelType.F32C1, 114, 64);
            using var target = new ImageBuffer(source.Size, PixelType.F32C1);
            Threshold.Binary(source, target, .9, 1);
            return Bytes(target);
        }));
        cases.Add(("distance/chamfer3", () =>
        {
            using var mask = RandomBytes(48, 36, PixelType.U8C1, 115, 8);
            for (int y = 0; y < 36; y++)
            for (int x = 0; x < 48; x++)
                mask.Row(y)[x] = (byte)(mask.Row(y)[x] < 6 || (x > 10 && x < 30 && y > 5) ? 255 : 0);
            using var distance = DistanceTransform.Chamfer3(mask);
            // To a thousandth, not exactly: see DistanceTransform.Chamfer3 for the last-place
            // difference this cannot share with the library, and why it never decides anything.
            var rounded = new List<double>();
            for (int y = 0; y < 36; y++)
            for (int x = 0; x < 48; x++)
            {
                float v = distance.Row<float>(y)[x];
                if (Math.Abs(v - 5) < 1e-3) throw new InvalidOperationException("A distance this close to five would be decided by rounding.");
                rounded.Add(Math.Round(v, 3));
            }
            return Bytes([.. rounded]);
        }));
        cases.Add(("statistics/means-on-ties", () =>
        {
            // 49 pixels of 250: their average, taken the replaced library's way, is not 250.
            using var image = new ImageBuffer(7, 9, PixelType.U8C1);
            image.SetTo(250);
            using var mask = new ImageBuffer(7, 9, PixelType.U8C1);
            using (var counted = new ImageBuffer(mask, new Rectangle(0, 0, 7, 7))) counted.SetTo(255);
            using var source = RandomBytes(23, 11, PixelType.U8C1, 116);
            using var where = RandomBytes(23, 11, PixelType.U8C1, 117, 2);
            var (least, most) = Statistics.MinMax(source, where);
            return Bytes(Statistics.Mean(image, mask), Statistics.Mean(image), Statistics.Mean(source, where), least, most,
                Statistics.CountNonZero(where), Statistics.BoundingRect(where).Width);
        }));
        cases.Add(("arithmetic/add-to-bytes-on-halves", () =>
        {
            using var bytes = RandomBytes(19, 7, PixelType.U8C3, 118);
            using var floats = new ImageBuffer(19, 7, PixelType.F32C3);
            var random = new Random64(119);
            for (int y = 0; y < 7; y++)
            for (int x = 0; x < 19 * 3; x++)
                floats.Row<float>(y)[x] = (random.Next(41) - 20) * 0.5f;
            using var target = new ImageBuffer(bytes.Size, PixelType.U8C3);
            Arithmetic.AddToBytes(bytes, floats, target);
            return Bytes(target);
        }));
        cases.Add(("arithmetic/float-chain", () =>
        {
            using var a = RandomFloats(27, 5, PixelType.F32C1, 120);
            using var b = RandomFloats(27, 5, PixelType.F32C1, 121);
            using var t = new ImageBuffer(a.Size, PixelType.F32C1);
            Arithmetic.Subtract(a, b, t);
            Arithmetic.Max(t, 0.0, t);
            Arithmetic.Min(t, 9 * .7979, t);
            Arithmetic.Divide(t, .7979, t);
            Arithmetic.Multiply(t, a, t);
            Arithmetic.Max(b, 1e-3, b);
            Arithmetic.Divide(a, b, a, 255);
            Arithmetic.Subtract(1.0, a, a);
            Arithmetic.Add(a, t, a);
            return Bytes(a);
        }));
        cases.Add(("conversion/on-halves", () =>
        {
            using var floats = new ImageBuffer(16, 3, PixelType.F32C1);
            for (int x = 0; x < 16; x++)
            {
                floats.Row<float>(0)[x] = x * 0.5f - 1;
                floats.Row<float>(1)[x] = x / 15f;
                floats.Row<float>(2)[x] = 250 + x * 0.5f;
            }
            using var plain = Conversion.Convert(floats, PixelType.U8C1);
            using var scaled = Conversion.Convert(floats, PixelType.U8C1, 255);
            using var bytes = RandomBytes(16, 3, PixelType.U8C1, 122);
            using var back = Conversion.Convert(bytes, PixelType.F32C1, 1.0 / 255);
            return [.. Bytes(plain), .. Bytes(scaled), .. Bytes(back)];
        }));
        cases.Add(("inpaint/against-edges", () =>
        {
            using var source = RandomBytes(40, 30, PixelType.U8C3, 123);
            using var mask = new ImageBuffer(40, 30, PixelType.U8C1);
            for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
                mask.Row(y)[x] = (byte)((x < 4 && y > 10 && y < 20) || (x > 12 && x < 26 && y > 8 && y < 14) || (y == 29 && x > 30) ? 255 : 0);
            using var target = Inpaint.NavierStokes(source, mask, 3);
            return Bytes(target);
        }));
        return cases;
    }
}
