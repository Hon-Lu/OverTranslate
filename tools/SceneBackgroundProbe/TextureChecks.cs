using System.Drawing;
using OverTranslate.Services.Realtime;
using Rect = System.Windows.Rect;

internal static class TextureChecks
{
    public static void Run()
    {
        int passed = 0;
        Check("No text leaves every overlay pixel transparent", () =>
        {
            using var frame = Texture(160, 96);
            using var result = new RealtimeTextureBackground().Repair(frame, []);
            Require(result.Stats.MaskPixels == 0, "Unexpected mask.");
            Require(RealtimeTextureBackground.Read(result.Overlay).All(p => p == 0), "Intact scenery was copied.");
        });
        Check("Known translated background is recovered exactly from observation", () =>
        {
            var engine = new RealtimeTextureBackground();
            using var previous = Texture(160, 96);
            using var observed = engine.Repair(previous, []);
            using var clean = Texture(160, 96, 4);
            using var input = (Bitmap)clean.Clone();
            var mask = Hole(input, new Rectangle(60, 40, 14, 10));
            using var result = engine.Repair(input, [], suppliedMask: mask);
            Require(result.Stats.MotionX == 4 && result.Stats.MotionY == 0, "Motion was not recovered.");
            Require(result.Stats.TemporalPixels == 140, "Observed pixels were not preferred.");
            var truth = RealtimeTextureBackground.Read(clean);
            var actual = RealtimeTextureBackground.Read(result.Overlay);
            for (int i = 0; i < mask.Length; i++)
                Require(mask[i] == 0 ? actual[i] == 0 : actual[i] == truth[i], "Pixel differs from observed background.");
        });
        Check("Scene cuts cannot borrow old colours", () =>
        {
            var engine = new RealtimeTextureBackground();
            using var oldScene = new Bitmap(160, 96);
            using (var g = Graphics.FromImage(oldScene)) g.Clear(Color.Red);
            using var first = engine.Repair(oldScene, []);
            using var scene = new Bitmap(160, 96);
            using (var g = Graphics.FromImage(scene)) g.Clear(Color.Blue);
            var mask = Hole(scene, new Rectangle(70, 40, 9, 9));
            using var result = engine.Repair(scene, [], suppliedMask: mask);
            Require(result.Stats.TemporalPixels == 0, "Scene cut reused history.");
            Require(result.Overlay.GetPixel(74, 44).ToArgb() == Color.Blue.ToArgb(), "Old scene contaminated repair.");
        });
        Check("Dimensions can change without reusing incompatible buffers", () =>
        {
            var engine = new RealtimeTextureBackground();
            foreach (var size in new[] { (160, 96), (96, 160), (160, 96), (1, 1) })
            {
                using var frame = Texture(size.Item1, size.Item2);
                var mask = size.Item1 == 1 ? new byte[1] : Hole(frame, new Rectangle(30, 40, 8, 8));
                using var result = engine.Repair(frame, [], suppliedMask: mask);
                Require(result.Overlay.Width == size.Item1 && result.Overlay.Height == size.Item2, "Wrong output dimensions.");
                Require(result.Stats.UnresolvedPixels == 0, "Resize lost donors.");
            }
        });
        Check("A fully unknown frame is reported, not claimed as repaired", () =>
        {
            using var frame = new Bitmap(12, 12);
            using var result = new RealtimeTextureBackground().Repair(frame, [], suppliedMask: Enumerable.Repeat((byte)255, 144).ToArray());
            Require(result.Stats.UnresolvedPixels == 144, "Unknown pixels were not reported.");
            Require(RealtimeTextureBackground.Read(result.Overlay).All(p => p == 0), "Unknown source was replayed.");
        });
        Check("Cancelled work does not publish or replace history", () =>
        {
            var engine = new RealtimeTextureBackground();
            using var previous = Texture(160, 96);
            using var first = engine.Repair(previous, []);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool cancelled = false;
            try { using var unexpected = engine.Repair(previous, [], cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Cancellation was ignored.");
            using var input = Texture(160, 96, 4);
            var mask = Hole(input, new Rectangle(60, 40, 14, 10));
            using var result = engine.Repair(input, [], suppliedMask: mask);
            Require(result.Stats.TemporalPixels == 140, "Cancellation corrupted prior history.");
        });
        Check("Invalid masks are rejected", () =>
        {
            using var frame = Texture(160, 96);
            bool rejected = false;
            try { using var unexpected = new RealtimeTextureBackground().Repair(frame, [], suppliedMask: [255]); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "Invalid geometry was accepted.");
        });
        Check("Reset forgets every previous observation", () =>
        {
            var engine = new RealtimeTextureBackground();
            using var previous = Texture(160, 96);
            using var first = engine.Repair(previous, []);
            engine.Reset();
            using var input = Texture(160, 96, 4);
            var mask = Hole(input, new Rectangle(60, 40, 14, 10));
            using var result = engine.Repair(input, [], suppliedMask: mask);
            Require(result.Stats.TemporalPixels == 0, "Reset retained history.");
        });
        Check("Automatic masks stay local to the supplied OCR line", () =>
        {
            using var frame = new Bitmap(320, 120);
            using (var g = Graphics.FromImage(frame))
            using (var font = new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.Clear(Color.FromArgb(20, 35, 55));
                g.DrawString("First line", font, Brushes.White, 30, 25);
                g.DrawString("Other text", font, Brushes.White, 30, 85);
            }
            var mask = RealtimeTextureBackground.DetectGlyphs(frame, RealtimeTextureBackground.Read(frame),
                [new Rect(28, 24, 160, 32)]);
            Require(mask.Any(p => p != 0), "No glyphs found.");
            Require(mask.Skip(75 * 320).All(p => p == 0), "Untranslated line was masked.");
        });
        Console.WriteLine($"{passed} texture checks passed.");

        void Check(string name, Action action)
        {
            action();
            passed++;
            Console.WriteLine("PASS " + name);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] Hole(Bitmap bitmap, Rectangle hole)
    {
        using (var g = Graphics.FromImage(bitmap)) g.FillRectangle(Brushes.White, hole);
        var mask = new byte[bitmap.Width * bitmap.Height];
        for (int y = hole.Top; y < hole.Bottom; y++)
        for (int x = hole.Left; x < hole.Right; x++) mask[y * bitmap.Width + x] = 255;
        return mask;
    }

    private static Bitmap Texture(int width, int height, int offset = 0)
    {
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int n = ((x + offset) * 73 + y * 151 + (x + offset) * y * 7) & 255;
            pixels[y * width + x] = Color.FromArgb(30 + n / 2, 20 + n / 3, 70 + n / 2).ToArgb();
        }
        return RealtimeTextureBackground.Write(pixels, width, height);
    }
}
