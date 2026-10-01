// Area, bilinear and nearest-neighbour resampling for byte and float images of one or three
// channels. The arithmetic follows OpenCV 4.13.0 modules/imgproc/src/resize.cpp (Apache License
// 2.0, https://github.com/opencv/opencv/blob/4.13.0/modules/imgproc/src/resize.cpp) closely enough
// to be bit-exact with it on bytes: the 11-bit fixed-point bilinear weights, the vertical pass's
// ">>4, multiply-high, +2 >>2" rounding, the (a+b+c+d+2)>>2 halving and the fractional weight table
// of the general area case, all accumulated in the same order.
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace OverTranslate.Imaging;

internal enum Interpolation
{
    /// <summary>The average of the source pixels each target pixel covers. Shrinking only.</summary>
    Area,

    /// <summary>Bilinear, pixel centres aligned.</summary>
    Linear,

    /// <summary>The source pixel each target pixel's corner falls in.</summary>
    Nearest,
}

internal static unsafe class Resize
{
    private const int CoefficientBits = 11, CoefficientScale = 1 << CoefficientBits;
    private const double DoubleEpsilon = 2.220446049250313e-16;

    public static ImageBuffer To(ImageBuffer source, Size size, Interpolation interpolation)
    {
        var target = ImageBuffer.Uninitialized(size, source.Type);
        try
        {
            To(source, target, interpolation);
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    public static void To(ImageBuffer source, ImageBuffer target, Interpolation interpolation)
    {
        if (target.Type != source.Type) throw new ArgumentException("Resizing keeps the pixel type.");
        if (source.IsEmpty || target.IsEmpty) throw new ArgumentException("Nothing to resize.");
        if (OpenCvReference.Handles(OpenCvReference.Families.Resize)) { OpenCvReference.Resize(source, target, interpolation); return; }
        if (target.Width == source.Width && target.Height == source.Height)
        {
            source.CopyTo(target);
            return;
        }
        double inverseX = (double)target.Width / source.Width, inverseY = (double)target.Height / source.Height;
        if (interpolation == Interpolation.Nearest)
        {
            Nearest(source, target, inverseX, inverseY);
            return;
        }
        double scaleX = 1.0 / inverseX, scaleY = 1.0 / inverseY;
        int integerX = Saturate.ToInt(scaleX), integerY = Saturate.ToInt(scaleY);
        bool exact = Math.Abs(scaleX - integerX) < DoubleEpsilon && Math.Abs(scaleY - integerY) < DoubleEpsilon;
        // Halving exactly is the same with either; the replaced library takes the area path for it.
        if (interpolation == Interpolation.Linear && exact && integerX == 2 && integerY == 2)
            interpolation = Interpolation.Area;
        bool bytes = !source.Type.IsFloat();
        int channels = source.Channels;
        if (interpolation == Interpolation.Area)
        {
            if (scaleX < 1 || scaleY < 1) throw new NotSupportedException("Area resampling only shrinks.");
            if (exact)
            {
                if (bytes) AreaExact8u(source, target, channels, integerX, integerY);
                else AreaExact32f(source, target, channels, integerX, integerY);
            }
            else Area(source, target, channels, scaleX, scaleY, bytes);
            return;
        }
        if (bytes) Linear8u(source, target, channels, scaleX, scaleY);
        else Linear32f(source, target, channels, scaleX, scaleY);
    }

    // ---- Nearest ------------------------------------------------------------------------------

    private static void Nearest(ImageBuffer source, ImageBuffer target, double inverseX, double inverseY)
    {
        // The reciprocal of the target-over-source ratio, not source over target: the two differ
        // in the last place, and a floor right at a whole number is decided there.
        double fx = 1.0 / inverseX, fy = 1.0 / inverseY;
        int size = source.Type.PixelSize();
        using var scratch = new Scratch<int>(target.Width);
        int* columns = scratch.Pointer;
        for (int x = 0; x < target.Width; x++) columns[x] = Math.Min((int)Math.Floor(x * fx), source.Width - 1) * size;
        for (int y = 0; y < target.Height; y++)
        {
            byte* from = source.Row(Math.Min((int)Math.Floor(y * fy), source.Height - 1)), to = target.Row(y);
            if (size == 4)
                for (int x = 0; x < target.Width; x++) *(int*)(to + x * 4) = *(int*)(from + columns[x]);
            else
                for (int x = 0; x < target.Width; x++) Buffer.MemoryCopy(from + columns[x], to + x * size, size, size);
        }
    }

    // ---- Area, whole factor -------------------------------------------------------------------

    private static void AreaExact8u(ImageBuffer source, ImageBuffer target, int cn, int sx, int sy)
    {
        long sstep = source.Stride;
        int dw = target.Width * cn;
        float scale = 1f / (sx * sy);
        ParallelRows.For(target.Height, (long)dw * sx * sy, y =>
        {
            byte* d = target.Row(y);
            byte* a = source.Row(y * sy);
            if (sx == 2 && sy == 2)
            {
                byte* b = a + sstep;
                if (cn == 1)
                {
                    int x = 0;
                    // (a+b+c+d+2)>>2, a vector of outputs at a time: widen pairs, sum, round, narrow.
                    if (Vector256.IsHardwareAccelerated)
                    {
                        var low = Vector256.Create((ushort)0x00ff);
                        var two = Vector256.Create((ushort)2);
                        for (; x <= dw - 16; x += 16)
                        {
                            var r0 = Vector256.Load(a + 2 * x).AsUInt16();
                            var r1 = Vector256.Load(b + 2 * x).AsUInt16();
                            var sum = (r0 & low) + Vector256.ShiftRightLogical(r0, 8) + (r1 & low) + Vector256.ShiftRightLogical(r1, 8) + two;
                            var q = Vector256.ShiftRightLogical(sum, 2);
                            Vector128.Store(Vector256.Narrow(q, q).GetLower(), d + x);
                        }
                    }
                    if (Vector128.IsHardwareAccelerated)
                    {
                        var low = Vector128.Create((ushort)0x00ff);
                        var two = Vector128.Create((ushort)2);
                        for (; x <= dw - 8; x += 8)
                        {
                            var r0 = Vector128.Load(a + 2 * x).AsUInt16();
                            var r1 = Vector128.Load(b + 2 * x).AsUInt16();
                            var sum = (r0 & low) + Vector128.ShiftRightLogical(r0, 8) + (r1 & low) + Vector128.ShiftRightLogical(r1, 8) + two;
                            var q = Vector128.ShiftRightLogical(sum, 2);
                            Vector64.Store(Vector128.Narrow(q, q).GetLower(), d + x);
                        }
                    }
                    for (; x < dw; x++) d[x] = (byte)((a[2 * x] + a[2 * x + 1] + b[2 * x] + b[2 * x + 1] + 2) >> 2);
                }
                else
                {
                    int px = target.Width;
                    for (int p = 0; p < px; p++)
                    {
                        byte* pa = a + p * 2 * cn, pb = b + p * 2 * cn, dd = d + p * cn;
                        for (int c = 0; c < cn; c++)
                            dd[c] = (byte)((pa[c] + pa[c + cn] + pb[c] + pb[c + cn] + 2) >> 2);
                    }
                }
                return;
            }
            for (int x = 0; x < dw; x++)
            {
                int i = (x / cn) * sx * cn + x % cn;
                int sum = 0;
                for (int r = 0; r < sy; r++)
                for (int c = 0; c < sx; c++) sum += a[r * sstep + i + c * cn];
                d[x] = Saturate.ToByte(sum * scale);
            }
        });
    }

    private static void AreaExact32f(ImageBuffer source, ImageBuffer target, int cn, int sx, int sy)
    {
        long sstep = source.Stride;
        int dw = target.Width * cn;
        float scale = 1f / (sx * sy);
        int area = sx * sy;
        ParallelRows.For(target.Height, (long)dw * area, y =>
        {
            float* d = target.Row<float>(y);
            byte* row = source.Row(y * sy);
            float* a = (float*)row;
            if (sx == 2 && sy == 2 && cn == 1)
            {
                float* b = (float*)(row + sstep);
                for (int x = 0; x < dw; x++)
                    d[x] = ((a[2 * x] + a[2 * x + 1]) + (b[2 * x] + b[2 * x + 1])) * .25f;
                return;
            }
            float* v = stackalloc float[area];
            for (int x = 0; x < dw; x++)
            {
                int i = (x / cn) * sx * cn + x % cn;
                // Row by row, left to right, summed four at a time: the replaced table's order.
                int k = 0;
                for (int r = 0; r < sy; r++)
                for (int c = 0; c < sx; c++) v[k++] = ((float*)(row + r * sstep))[i + c * cn];
                float sum = 0;
                k = 0;
                for (; k <= area - 4; k += 4) sum += v[k] + v[k + 1] + v[k + 2] + v[k + 3];
                for (; k < area; k++) sum += v[k];
                d[x] = sum * scale;
            }
        });
    }

    // ---- Area, fractional factor --------------------------------------------------------------

    private struct Decimate
    {
        public int Source, Target;
        public float Alpha;
    }

    private static int AreaTable(int sourceSize, int targetSize, int cn, double scale, Decimate* table)
    {
        int k = 0;
        for (int dx = 0; dx < targetSize; dx++)
        {
            double fsx1 = dx * scale;
            double fsx2 = fsx1 + scale;
            double cellWidth = Math.Min(scale, sourceSize - fsx1);
            int sx1 = (int)Math.Ceiling(fsx1), sx2 = (int)Math.Floor(fsx2);
            sx2 = Math.Min(sx2, sourceSize - 1);
            sx1 = Math.Min(sx1, sx2);
            if (sx1 - fsx1 > 1e-3)
                table[k++] = new() { Target = dx * cn, Source = (sx1 - 1) * cn, Alpha = (float)((sx1 - fsx1) / cellWidth) };
            for (int sx = sx1; sx < sx2; sx++)
                table[k++] = new() { Target = dx * cn, Source = sx * cn, Alpha = (float)(1.0 / cellWidth) };
            if (fsx2 - sx2 > 1e-3)
                table[k++] = new() { Target = dx * cn, Source = sx2 * cn, Alpha = (float)(Math.Min(Math.Min(fsx2 - sx2, 1.0), cellWidth) / cellWidth) };
        }
        return k;
    }

    private static void Area(ImageBuffer source, ImageBuffer target, int cn, double scaleX, double scaleY, bool bytes)
    {
        int sw = source.Width, sh = source.Height, dw = target.Width * cn, dh = target.Height;
        using var tables = new Scratch<Decimate>(sw * 2 + 2 + sh * 2 + 2);
        Decimate* xtab = tables.Pointer, ytab = xtab + sw * 2 + 2;
        int xn = AreaTable(sw, target.Width, cn, scaleX, xtab);
        int yn = AreaTable(sh, dh, 1, scaleY, ytab);
        // The horizontal table regrouped per output element, in its original order. The replaced
        // routine adds each entry into a zeroed buffer, and 0 + x·a is exactly x·a, so summing an
        // element's own entries in order gives the same float without the scatter.
        using var indices = new Scratch<int>(dh + 1 + dw + 1 + (long)xn * cn);
        using var weights = new Scratch<float>((long)xn * cn);
        int* rowStart = indices.Pointer, start = rowStart + dh + 1, sources = start + dw + 1;
        float* alphas = weights.Pointer;
        for (int k = 0, dy = 0; k < yn; k++)
            if (k == 0 || ytab[k].Target != ytab[k - 1].Target) rowStart[dy++] = k;
        rowStart[dh] = yn;
        {
            int n = 0, k0 = 0;
            for (int px = 0; px < target.Width; px++)
            {
                int k1 = k0;
                while (k1 < xn && xtab[k1].Target == px * cn) k1++;
                for (int c = 0; c < cn; c++)
                {
                    start[px * cn + c] = n;
                    for (int k = k0; k < k1; k++)
                    {
                        sources[n] = xtab[k].Source + c;
                        alphas[n] = xtab[k].Alpha;
                        n++;
                    }
                }
                k0 = k1;
            }
            start[dw] = n;
        }
        nint yAt = (nint)ytab, rowsAt = (nint)rowStart, startAt = (nint)start, sourcesAt = (nint)sources, alphasAt = (nint)alphas;
        long work = (long)(dw * scaleX * scaleY);
        ParallelRows.For(dh, work, dy =>
        {
            var sumArray = System.Buffers.ArrayPool<float>.Shared.Rent(dw);
            fixed (float* sum = sumArray)
            {
                float* al = (float*)alphasAt;
                int* st = (int*)startAt, si = (int*)sourcesAt, first = (int*)rowsAt;
                Decimate* yt = (Decimate*)yAt;
                new Span<float>(sum, dw).Clear();
                for (int j = first[dy]; j < first[dy + 1]; j++)
                {
                    float beta = yt[j].Alpha;
                    byte* row = source.Row(yt[j].Source);
                    if (bytes)
                        for (int e = 0; e < dw; e++)
                        {
                            int k = st[e], end = st[e + 1];
                            float t = row[si[k]] * al[k];
                            for (k++; k < end; k++) t += row[si[k]] * al[k];
                            sum[e] += beta * t;
                        }
                    else
                    {
                        float* f = (float*)row;
                        for (int e = 0; e < dw; e++)
                        {
                            int k = st[e], end = st[e + 1];
                            float t = f[si[k]] * al[k];
                            for (k++; k < end; k++) t += f[si[k]] * al[k];
                            sum[e] += beta * t;
                        }
                    }
                }
                if (bytes)
                {
                    byte* d = target.Row(dy);
                    for (int x = 0; x < dw; x++) d[x] = Saturate.ToByte(sum[x]);
                }
                else new Span<float>(sum, dw).CopyTo(new Span<float>(target.Row(dy), dw));
            }
            System.Buffers.ArrayPool<float>.Shared.Return(sumArray);
        });
    }

    // ---- Bilinear -----------------------------------------------------------------------------

    /// <param name="precise">
    /// Source coordinates in double: what float images get, because the replaced library hands them
    /// to IPP, which maps coordinates in double — its own float mapping is off by up to 0.015 levels —
    /// and interpolates each step as one fused multiply-add from the left or upper sample.
    /// </param>
    /// <returns>The first target index whose right-hand neighbour falls outside the source.</returns>
    private static int Tables(int sourceSize, int targetSize, double scale, bool precise, int* offsets, float* weights)
    {
        int limit = targetSize;
        for (int dx = 0; dx < targetSize; dx++)
        {
            int sx;
            float fx;
            if (precise)
            {
                double position = (dx + 0.5) * scale - 0.5;
                sx = (int)Math.Floor(position);
                fx = (float)(position - sx);
            }
            else
            {
                fx = (float)((dx + 0.5) * scale - 0.5);
                sx = (int)MathF.Floor(fx);
                fx -= sx;
            }
            if (sx < 0)
            {
                fx = 0;
                sx = 0;
            }
            if (sx + 1 >= sourceSize)
            {
                limit = Math.Min(limit, dx);
                if (sx >= sourceSize - 1)
                {
                    fx = 0;
                    sx = sourceSize - 1;
                }
            }
            offsets[dx] = sx;
            weights[dx * 2] = 1f - fx;
            weights[dx * 2 + 1] = fx;
        }
        return limit;
    }

    private static void Linear8u(ImageBuffer source, ImageBuffer target, int cn, double scaleX, double scaleY)
    {
        int sw = source.Width, sh = source.Height, dwPx = target.Width, dh = target.Height, dw = dwPx * cn;
        using var integers = new Scratch<int>(dwPx + dw * 4L + dh * 3L);
        using var fractions = new Scratch<float>(dwPx * 2L);
        int* xofs = integers.Pointer;
        float* xalpha = fractions.Pointer;
        int xmax = Tables(sw, dwPx, scaleX, precise: false, xofs, xalpha);
        // Per element rather than per pixel, so the horizontal pass is one flat loop.
        int* o0 = xofs + dwPx, o1 = o0 + dw, a0 = o1 + dw, a1 = a0 + dw;
        for (int dx = 0; dx < dwPx; dx++)
        for (int c = 0; c < cn; c++)
        {
            int e = dx * cn + c, sx = xofs[dx] * cn + c;
            if (dx < xmax)
            {
                o0[e] = sx;
                o1[e] = sx + cn;
                a0[e] = Saturate.ToShort(xalpha[dx * 2] * CoefficientScale);
                a1[e] = Saturate.ToShort(xalpha[dx * 2 + 1] * CoefficientScale);
            }
            else
            {
                o0[e] = o1[e] = sx;
                a0[e] = CoefficientScale;
                a1[e] = 0;
            }
        }
        int* yofs = a1 + dw, ib = yofs + dh;
        for (int dy = 0; dy < dh; dy++)
        {
            float fy = (float)((dy + 0.5) * scaleY - 0.5);
            int sy = (int)MathF.Floor(fy);
            fy -= sy;
            yofs[dy] = sy;
            ib[dy * 2] = Saturate.ToShort((1f - fy) * CoefficientScale);
            ib[dy * 2 + 1] = Saturate.ToShort(fy * CoefficientScale);
        }
        nint tableAt = (nint)integers.Pointer;
        ParallelRows.Blocks(dh, dw * 3, (from, to) =>
        {
            // Two horizontal rows, reused while consecutive output rows read the same source rows:
            // a two-times enlargement computes each source row once instead of four times.
            var bufferA = System.Buffers.ArrayPool<int>.Shared.Rent(dw);
            var bufferB = System.Buffers.ArrayPool<int>.Shared.Rent(dw);
            int tagA = -1, tagB = -1;
            int* po0 = (int*)tableAt + dwPx, po1 = po0 + dw, pa0 = po1 + dw, pa1 = pa0 + dw, py = pa1 + dw, pib = py + dh;
            fixed (int* pa = bufferA, pb = bufferB)
                for (int dy = from; dy < to; dy++)
                {
                    int y0 = Clip(py[dy], sh), y1 = Clip(py[dy] + 1, sh);
                    int* r0;
                    if (tagA == y0) r0 = pa;
                    else if (tagB == y0) r0 = pb;
                    else
                    {
                        // Keep whichever buffer y1 might still want.
                        bool useA = tagA != y1 || tagB == y1;
                        r0 = useA ? pa : pb;
                        Horizontal(source.Row(y0), r0, dw, po0, po1, pa0, pa1);
                        if (useA) tagA = y0;
                        else tagB = y0;
                    }
                    int* r1;
                    if (y1 == y0) r1 = r0;
                    else if (tagA == y1) r1 = pa;
                    else if (tagB == y1) r1 = pb;
                    else
                    {
                        r1 = r0 == pa ? pb : pa;
                        Horizontal(source.Row(y1), r1, dw, po0, po1, pa0, pa1);
                        if (r1 == pa) tagA = y1;
                        else tagB = y1;
                    }
                    Vertical8u(r0, r1, pib[dy * 2], pib[dy * 2 + 1], target.Row(dy), dw);
                }
            System.Buffers.ArrayPool<int>.Shared.Return(bufferA);
            System.Buffers.ArrayPool<int>.Shared.Return(bufferB);
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Horizontal(byte* s, int* d, int dw, int* o0, int* o1, int* a0, int* a1)
    {
        for (int x = 0; x < dw; x++) d[x] = s[o0[x]] * a0[x] + s[o1[x]] * a1[x];
    }

    private static void Vertical8u(int* r0, int* r1, int b0, int b1, byte* d, int dw)
    {
        int x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var vb0 = Vector256.Create(b0);
            var vb1 = Vector256.Create(b1);
            var two = Vector256.Create(2);
            for (; x <= dw - 32; x += 32)
            {
                var v0 = Vertical(Vector256.Load(r0 + x), Vector256.Load(r1 + x), vb0, vb1, two);
                var v1 = Vertical(Vector256.Load(r0 + x + 8), Vector256.Load(r1 + x + 8), vb0, vb1, two);
                var v2 = Vertical(Vector256.Load(r0 + x + 16), Vector256.Load(r1 + x + 16), vb0, vb1, two);
                var v3 = Vertical(Vector256.Load(r0 + x + 24), Vector256.Load(r1 + x + 24), vb0, vb1, two);
                Vector256.Store(Vector256.Narrow(Vector256.Narrow(v0, v1), Vector256.Narrow(v2, v3)).AsByte(), d + x);
            }
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var vb0 = Vector128.Create(b0);
            var vb1 = Vector128.Create(b1);
            var two = Vector128.Create(2);
            for (; x <= dw - 16; x += 16)
            {
                var v0 = Vertical(Vector128.Load(r0 + x), Vector128.Load(r1 + x), vb0, vb1, two);
                var v1 = Vertical(Vector128.Load(r0 + x + 4), Vector128.Load(r1 + x + 4), vb0, vb1, two);
                var v2 = Vertical(Vector128.Load(r0 + x + 8), Vector128.Load(r1 + x + 8), vb0, vb1, two);
                var v3 = Vertical(Vector128.Load(r0 + x + 12), Vector128.Load(r1 + x + 12), vb0, vb1, two);
                Vector128.Store(Vector128.Narrow(Vector128.Narrow(v0, v1), Vector128.Narrow(v2, v3)).AsByte(), d + x);
            }
        }
        for (; x < dw; x++) d[x] = (byte)((((b0 * (r0[x] >> 4)) >> 16) + ((b1 * (r1[x] >> 4)) >> 16) + 2) >> 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Vertical(Vector256<int> a, Vector256<int> b, Vector256<int> b0, Vector256<int> b1, Vector256<int> two) =>
        Vector256.ShiftRightArithmetic(
            Vector256.ShiftRightArithmetic(b0 * Vector256.ShiftRightArithmetic(a, 4), 16)
            + Vector256.ShiftRightArithmetic(b1 * Vector256.ShiftRightArithmetic(b, 4), 16) + two, 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Vertical(Vector128<int> a, Vector128<int> b, Vector128<int> b0, Vector128<int> b1, Vector128<int> two) =>
        Vector128.ShiftRightArithmetic(
            Vector128.ShiftRightArithmetic(b0 * Vector128.ShiftRightArithmetic(a, 4), 16)
            + Vector128.ShiftRightArithmetic(b1 * Vector128.ShiftRightArithmetic(b, 4), 16) + two, 2);

    private static void Linear32f(ImageBuffer source, ImageBuffer target, int cn, double scaleX, double scaleY)
    {
        int sh = source.Height, dwPx = target.Width, dh = target.Height, dw = dwPx * cn;
        using var offsets = new Scratch<int>(dwPx);
        using var fractions = new Scratch<float>(dwPx * 2L);
        int xmax = Tables(source.Width, dwPx, scaleX, precise: true, offsets.Pointer, fractions.Pointer);
        nint offsetsAt = (nint)offsets.Pointer, fractionsAt = (nint)fractions.Pointer;
        ParallelRows.For(dh, dw * 4, dy =>
        {
            var rows = System.Buffers.ArrayPool<float>.Shared.Rent(dw * 2);
            fixed (float* r = rows)
            {
                int* xo = (int*)offsetsAt;
                float* xw = (float*)fractionsAt;
                float* r0 = r, r1 = r + dw;
                double position = (dy + 0.5) * scaleY - 0.5;
                int sy = (int)Math.Floor(position);
                float fy = (float)(position - sy);
                int y0 = Clip(sy, sh), y1 = Clip(sy + 1, sh);
                Horizontal32f(source.Row<float>(y0), r0, dwPx, cn, xo, xw, xmax);
                Horizontal32f(source.Row<float>(y1), r1, dwPx, cn, xo, xw, xmax);
                float* d = target.Row<float>(dy);
                for (int x = 0; x < dw; x++) d[x] = MathF.FusedMultiplyAdd(r1[x] - r0[x], fy, r0[x]);
            }
            System.Buffers.ArrayPool<float>.Shared.Return(rows);
        });
    }

    private static void Horizontal32f(float* s, float* d, int dwPx, int cn, int* xofs, float* xa, int xmax)
    {
        for (int dx = 0; dx < dwPx; dx++)
        {
            int sx = xofs[dx] * cn;
            if (dx < xmax)
            {
                float a1 = xa[dx * 2 + 1];
                for (int c = 0; c < cn; c++) d[dx * cn + c] = MathF.FusedMultiplyAdd(s[sx + cn + c] - s[sx + c], a1, s[sx + c]);
            }
            else
                for (int c = 0; c < cn; c++) d[dx * cn + c] = s[sx + c];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clip(int y, int h) => y >= 0 ? (y < h ? y : h - 1) : 0;
}
