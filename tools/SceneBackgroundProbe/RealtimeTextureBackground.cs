using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Realtime;

internal sealed record TextureRepairStats(
    double Milliseconds, int MaskPixels, int TemporalPixels, int SynthesizedPixels,
    int BudgetFallbackPixels, int UnresolvedPixels, int MotionX, int MotionY, double MotionError);

internal sealed record TextureRepairResult(Bitmap Overlay, TextureRepairStats Stats) : IDisposable
{
    public void Dispose() => Overlay.Dispose();
}

/// <summary>
/// Repairs glyph-sized holes rather than repainting subtitle rectangles. Original scene pixels
/// outside the mask remain transparent in the overlay, so they continue to move at the source rate.
/// One instance belongs to one region and is used by one worker, never concurrently.
/// </summary>
internal sealed class RealtimeTextureBackground
{
    private int[]? _previous;
    private int[]? _repaired;
    private byte[]? _previousMask;
    private byte[]? _observed;
    private int _width, _height;
    private int[]? _inputBuffer, _repairBuffer, _overlayBuffer, _integralBuffer;
    private byte[]? _maskBuffer, _observedBuffer, _missingBuffer;
    private bool[]? _queuedBuffer;
    private readonly List<int> _glyphSeeds = [];
    private readonly Queue<int> _frontier = new();

    private static T[] Buffer<T>(T[]? buffer, int length) =>
        buffer is not null && buffer.Length == length ? buffer : new T[length];

    public void Reset()
    {
        _previous = _repaired = null;
        _previousMask = _observed = null;
        _width = _height = 0;
        _inputBuffer = _repairBuffer = _overlayBuffer = _integralBuffer = null;
        _maskBuffer = _observedBuffer = _missingBuffer = null;
        _queuedBuffer = null;
        _frontier.Clear();
        _glyphSeeds.Clear();
    }

    public TextureRepairResult Repair(Bitmap frame, IReadOnlyList<Rect> sourceLines,
        CancellationToken token = default, byte[]? suppliedMask = null)
    {
        token.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        int width = frame.Width, height = frame.Height;
        int[] original = Read(frame, _inputBuffer);
        // suppliedMask is an instrument for measuring reconstruction independently of segmentation.
        // The normal and live probes use automatic masks, not ground-truth glyph masks.
        byte[] mask;
        if (suppliedMask is null) mask = DetectGlyphs(frame, original, sourceLines, _maskBuffer, _glyphSeeds);
        else
        {
            if (suppliedMask.Length != original.Length) throw new ArgumentException("Mask geometry differs from frame.");
            mask = Buffer(_maskBuffer, original.Length);
            suppliedMask.CopyTo(mask, 0);
        }
        if (mask.Length != original.Length) throw new ArgumentException("Mask geometry differs from frame.");
        int[] result = Buffer(_repairBuffer, original.Length);
        original.CopyTo(result, 0);
        var missing = _missingBuffer = Buffer(_missingBuffer, mask.Length);
        mask.CopyTo(missing, 0);
        var observed = Buffer(_observedBuffer, mask.Length);
        for (int i = 0; i < mask.Length; i++) observed[i] = mask[i] == 0 ? (byte)1 : (byte)0;
        int masked = mask.Count(value => value != 0), temporal = 0, synthesized = 0;
        var motion = (X: 0, Y: 0, Error: double.PositiveInfinity);

        if (masked > 0 && _previous is not null && _width == width && _height == height)
        {
            motion = FindMotion(original, mask, _previous, _previousMask!, width, height, token);
            if (motion.Error < 14)
            {
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (mask[i] == 0) continue;
                    int px = x + motion.X, py = y + motion.Y;
                    if ((uint)px >= width || (uint)py >= height) continue;
                    int p = py * width + px;
                    // Prefer genuinely observed pixels. On a stationary scene a previous spatial
                    // repair can be retained, which avoids resynthesizing a different texture.
                    if (!MotionAgreesNear(original, mask, _previous, _previousMask!,
                        x, y, motion.X, motion.Y, width, height)) continue;
                    if (_previousMask![p] == 0 || _observed![p] != 0)
                    {
                        result[i] = _previousMask[p] == 0 ? _previous[p] : _repaired![p];
                        observed[i] = 1;
                    }
                    else if (motion.X == 0 && motion.Y == 0 && motion.Error < 3 && mask[i] != 0)
                        result[i] = _repaired![p];
                    else continue;
                    missing[i] = 0;
                    temporal++;
                }
            }
        }

        if (masked > temporal)
            synthesized = Synthesize(original, result, mask, missing, width, height, clock, token);
        int fallback = masked > temporal + synthesized ? FillRemaining(result, missing, width, height, token) : 0;
        int unresolved = masked - temporal - synthesized - fallback;
        token.ThrowIfCancellationRequested();

        // History keeps original and repaired samples separately: reconstructed pixels must never
        // masquerade as an observation when moving content reveals the real background.
        _inputBuffer = _previous;
        _repairBuffer = _repaired;
        _maskBuffer = _previousMask;
        _observedBuffer = _observed;
        _previous = original;
        _repaired = result;
        _previousMask = mask;
        _observed = observed;
        _width = width;
        _height = height;
        var overlayPixels = _overlayBuffer = Buffer(_overlayBuffer, result.Length);
        Array.Clear(overlayPixels);
        for (int i = 0; i < result.Length; i++)
            if (mask[i] != 0 && missing[i] == 0) overlayPixels[i] = result[i] | unchecked((int)0xff000000);
        var overlay = Write(overlayPixels, width, height);
        return new(overlay, new(clock.Elapsed.TotalMilliseconds, masked, temporal, synthesized,
            fallback, unresolved, motion.X, motion.Y, motion.Error));
    }

    internal static byte[] DetectGlyphs(Bitmap frame, int[] pixels, IReadOnlyList<Rect> lines,
        byte[]? buffer = null, List<int>? seedBuffer = null)
    {
        int width = frame.Width, height = frame.Height;
        var mask = Buffer(buffer, pixels.Length);
        Array.Clear(mask);
        foreach (var line in lines)
        {
            if (line.IsEmpty || line.Width <= 0 || line.Height <= 0) continue;
            var sample = SourceTextColorSampler.Sample(frame, line);
            var fg = sample?.Text ?? System.Windows.Media.Colors.White;
            int radius = Math.Clamp((int)(line.Height * 0.18), 3, 9);
            int dilation = Math.Clamp((int)Math.Ceiling(line.Height * 0.09), 3, 5);
            int left = Math.Clamp((int)Math.Floor(line.Left) - dilation, 0, width);
            int top = Math.Clamp((int)Math.Floor(line.Top) - dilation, 0, height);
            int right = Math.Clamp((int)Math.Ceiling(line.Right) + dilation, 0, width);
            int bottom = Math.Clamp((int)Math.Ceiling(line.Bottom) + dilation, 0, height);
            var seeds = seedBuffer ?? new List<int>();
            seeds.Clear();
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                int i = y * width + x, color = pixels[i];
                if (Math.Abs(R(color) - fg.R) + Math.Abs(G(color) - fg.G) + Math.Abs(B(color) - fg.B) > 60)
                    continue;
                int contrast = 0;
                for (int dy = -radius; dy <= radius; dy += radius)
                for (int dx = -radius; dx <= radius; dx += radius)
                {
                    int nx = Math.Clamp(x + dx, 0, width - 1), ny = Math.Clamp(y + dy, 0, height - 1);
                    contrast = Math.Max(contrast, Difference(color, pixels[ny * width + nx]));
                }
                if (contrast >= 35) seeds.Add(i);
            }
            foreach (int i in seeds)
            {
                int x = i % width, y = i / width;
                for (int dy = -dilation; dy <= dilation; dy++)
                for (int dx = -dilation; dx <= dilation; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx < width && (uint)ny < height && dx * dx + dy * dy <= dilation * dilation + 1)
                        mask[ny * width + nx] = 255;
                }
            }
        }
        return mask;
    }

    private static bool MotionAgreesNear(int[] current, byte[] mask, int[] previous, byte[] previousMask,
        int x, int y, int dx, int dy, int width, int height)
    {
        int sum = 0, count = 0;
        for (int oy = -16; oy <= 16; oy += 8)
        for (int ox = -16; ox <= 16; ox += 8)
        {
            int cx = x + ox, cy = y + oy, px = cx + dx, py = cy + dy;
            if ((uint)cx >= width || (uint)cy >= height || (uint)px >= width || (uint)py >= height) continue;
            int c = cy * width + cx, p = py * width + px;
            if (mask[c] != 0 || previousMask[p] != 0) continue;
            sum += Difference(current[c], previous[p]);
            count++;
        }
        return count >= 2 && sum < count * 18;
    }
    private static (int X, int Y, double Error) FindMotion(int[] current, byte[] mask,
        int[] previous, byte[] previousMask, int width, int height, CancellationToken token)
    {
        int bestX = 0, bestY = 0;
        double best = double.PositiveInfinity;
        int stepX = Math.Max(4, width / 28), stepY = Math.Max(4, height / 12);
        double Error(int dx, int dy)
        {
            long sum = 0;
            int count = 0;
            for (int y = 12; y < height - 12; y += stepY)
            for (int x = 12; x < width - 12; x += stepX)
            {
                int px = x + dx, py = y + dy, i = y * width + x;
                if ((uint)px >= width || (uint)py >= height || mask[i] != 0) continue;
                int p = py * width + px;
                if (previousMask[p] != 0) continue;
                sum += Difference(current[i], previous[p]);
                count++;
            }
            return count < 24 ? double.PositiveInfinity : (double)sum / count;
        }
        // Search coarse-to-fine. A fresh deterministic search avoids temporal drift at scene cuts.
        for (int dy = -24; dy <= 24; dy += 4)
        {
            token.ThrowIfCancellationRequested();
            for (int dx = -24; dx <= 24; dx += 4)
            {
                double error = Error(dx, dy);
                if (error < best) { best = error; bestX = dx; bestY = dy; }
            }
        }
        int cx = bestX, cy = bestY;
        for (int dy = cy - 3; dy <= cy + 3; dy++)
        for (int dx = cx - 3; dx <= cx + 3; dx++)
        {
            double error = Error(dx, dy);
            if (error < best || (error == best && dx * dx + dy * dy < bestX * bestX + bestY * bestY))
            { best = error; bestX = dx; bestY = dy; }
        }
        return (bestX, bestY, best);
    }

    private int Synthesize(int[] original, int[] result, byte[] mask, byte[] missing,
        int width, int height, Stopwatch clock, CancellationToken token)
    {
        // An integral mask makes rejecting a contaminated donor patch constant-time.
        int stride = width + 1;
        var integral = _integralBuffer = Buffer(_integralBuffer, stride * (height + 1));
        Array.Clear(integral, 0, stride);
        for (int y = 0; y < height; y++)
        {
            int row = 0;
            integral[(y + 1) * stride] = 0;
            for (int x = 0; x < width; x++)
            {
                if (mask[y * width + x] != 0) row++;
                integral[(y + 1) * stride + x + 1] = integral[y * stride + x + 1] + row;
            }
        }
        bool Donor(int x, int y)
        {
            if (x < 4 || y < 4 || x >= width - 4 || y >= height - 4) return false;
            int l = x - 4, t = y - 4, r = x + 5, b = y + 5;
            return integral[b * stride + r] - integral[t * stride + r] -
                integral[b * stride + l] + integral[t * stride + l] == 0;
        }

        // Fill from the known boundary inward. A patch writes only holes, never intact scenery.
        var frontier = _frontier;
        frontier.Clear();
        var queued = _queuedBuffer = Buffer(_queuedBuffer, missing.Length);
        Array.Clear(queued);
        for (int y = 1; y < height - 1; y++)
        for (int x = 1; x < width - 1; x++)
        {
            int i = y * width + x;
            if (missing[i] != 0 && (missing[i - 1] == 0 || missing[i + 1] == 0 ||
                missing[i - width] == 0 || missing[i + width] == 0))
            { frontier.Enqueue(i); queued[i] = true; }
        }
        int filled = 0;
        while (frontier.TryDequeue(out int index))
        {
            if (missing[index] == 0) continue;
            token.ThrowIfCancellationRequested();
            // Leave room for capture, bitmap upload and presentation within a 50–100ms frame budget.
            if (clock.ElapsedMilliseconds > 55) break;
            int x = index % width, y = index / width;
            int bestX = -1, bestY = -1;
            double best = double.PositiveInfinity;
            double Score(int sx, int sy)
            {
                if (!Donor(sx, sy)) return double.PositiveInfinity;
                int sum = 0, count = 0;
                for (int dy = -4; dy <= 4; dy += 2)
                for (int dx = -4; dx <= 4; dx += 2)
                {
                    int tx = x + dx, ty = y + dy;
                    if ((uint)tx >= width || (uint)ty >= height) continue;
                    int t = ty * width + tx;
                    if (missing[t] != 0) continue;
                    int weight = mask[t] == 0 ? 3 : 1;
                    sum += Difference(result[t], original[(sy + dy) * width + sx + dx]) * weight;
                    count += weight;
                }
                return count < 3 ? double.PositiveInfinity : (double)sum / count +
                    (Math.Abs(sx - x) + Math.Abs(sy - y)) * 0.025;
            }
            for (int sy = Math.Max(4, y - 36); sy <= Math.Min(height - 5, y + 36); sy += 4)
            for (int sx = Math.Max(4, x - 36); sx <= Math.Min(width - 5, x + 36); sx += 4)
            {
                double score = Score(sx, sy);
                if (score < best) { best = score; bestX = sx; bestY = sy; }
            }
            if (bestX < 0) continue;
            int bx = bestX, by = bestY;
            for (int sy = by - 3; sy <= by + 3; sy++)
            for (int sx = bx - 3; sx <= bx + 3; sx++)
            {
                double score = Score(sx, sy);
                if (score < best) { best = score; bestX = sx; bestY = sy; }
            }
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int tx = x + dx, ty = y + dy;
                if ((uint)tx >= width || (uint)ty >= height) continue;
                int t = ty * width + tx;
                if (missing[t] == 0) continue;
                result[t] = original[(bestY + dy) * width + bestX + dx];
                missing[t] = 0;
                filled++;
                Enqueue(tx - 1, ty); Enqueue(tx + 1, ty); Enqueue(tx, ty - 1); Enqueue(tx, ty + 1);
            }
            void Enqueue(int nx, int ny)
            {
                if ((uint)nx >= width || (uint)ny >= height) return;
                int n = ny * width + nx;
                if (missing[n] != 0 && !queued[n]) { queued[n] = true; frontier.Enqueue(n); }
            }
        }
        return filled;
    }

    private int FillRemaining(int[] result, byte[] missing, int width, int height, CancellationToken token)
    {
        int count = 0;
        var queue = _frontier;
        queue.Clear();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * width + x;
            if (missing[i] == 0 && ((x > 0 && missing[i - 1] != 0) ||
                (x + 1 < width && missing[i + 1] != 0) || (y > 0 && missing[i - width] != 0) ||
                (y + 1 < height && missing[i + width] != 0))) queue.Enqueue(i);
        }
        while (queue.TryDequeue(out int i))
        {
            if ((count & 1023) == 0) token.ThrowIfCancellationRequested();
            int x = i % width, y = i / width;
            Fill(x - 1, y); Fill(x + 1, y); Fill(x, y - 1); Fill(x, y + 1);
            void Fill(int nx, int ny)
            {
                if ((uint)nx >= width || (uint)ny >= height) return;
                int n = ny * width + nx;
                if (missing[n] == 0) return;
                result[n] = result[i]; missing[n] = 0; count++; queue.Enqueue(n);
            }
        }
        return count;
    }

    internal static int[] Read(Bitmap frame, int[]? buffer = null)
    {
        var data = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = Buffer(buffer, frame.Width * frame.Height);
            for (int y = 0; y < frame.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * frame.Width, frame.Width);
            return pixels;
        }
        finally { frame.UnlockBits(data); }
    }

    internal static Bitmap Write(int[] pixels, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(pixels, y * width, IntPtr.Add(data.Scan0, y * data.Stride), width);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private static int R(int color) => (color >> 16) & 255;
    private static int G(int color) => (color >> 8) & 255;
    private static int B(int color) => color & 255;
    private static int Difference(int a, int b) =>
        (Math.Abs(R(a) - R(b)) + Math.Abs(G(a) - G(b)) + Math.Abs(B(a) - B(b))) / 3;
}
