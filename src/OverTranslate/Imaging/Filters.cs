namespace OverTranslate.Imaging;

/// <summary>How a filter fills in pixels past the edge of the image that owns them.</summary>
internal enum Border
{
    /// <summary>Zero.</summary>
    Constant,

    /// <summary>The edge pixel, repeated.</summary>
    Replicate,

    /// <summary>Mirrored about the edge pixel, which is not repeated: <c>dcb|abcd|cba</c>.</summary>
    Reflect101,
}

/// <summary>Box averages and sums, the 3×3 Laplacian, and the Gaussian blur.</summary>
/// <remarks>
/// <para>All of them read a view's parent around it before falling back to <see cref="Border"/>,
/// which only ever applies at the edge of the image that owns the pixels — see
/// <see cref="ImageBuffer"/>.</para>
///
/// <para>Each one reproduces the arithmetic of the routine it replaced: which sums are exact
/// integers, where floats are carried in double, the fixed-point scale that turns a box sum back
/// into a byte. The repair compares what comes out of these against thresholds, so a rounding of
/// difference is a pixel of mask of difference.</para>
/// </remarks>
internal static unsafe class Filters
{
    /// <summary>The average over a <paramref name="size"/>² box, mirrored at the edges.</summary>
    /// <remarks>
    /// Bytes: the 16-bit box sum is turned back into a byte by a 23-bit fixed-point reciprocal,
    /// <c>((sum + delta) · scale) &gt;&gt; 23</c>. Floats: each row's sum is taken in double and the
    /// column sum carried down the image by adding the new row and subtracting the one that left
    /// the box, then multiplied by the reciprocal of the area.
    /// </remarks>
    public static void Blur(ImageBuffer source, ImageBuffer target, int size)
    {
        if (source.Channels != 1) throw new NotSupportedException($"Blur on {source.Type}.");
        ImageBuffer.RequireSameShape(source, target);
        if (size % 2 == 0) throw new ArgumentException("Odd box sizes only.");
        if (source.IsEmpty) return;
        int r = size / 2, width = source.Width, height = source.Height;
        using var padded = Neighbourhood(source, r, r, Border.Reflect101);
        int rows = padded.Height;
        if (source.Type == PixelType.U8C1)
        {
            if (size * size > 256) throw new NotSupportedException("Byte boxes larger than 256 pixels.");
            const int shift = 23;
            int divisor = size * size;
            double exact = (double)(1 << shift) / divisor;
            uint divScale = (uint)Math.Floor(exact), divDelta = (uint)(divisor / 2);
            if (exact - divScale < 0.5) divDelta++;
            else divScale++;
            using var scratch = new Scratch<int>((long)rows * width);
            nint across = (nint)scratch.Pointer;
            RowSums(padded, (int*)across, width, size);
            ColumnSums((int*)across, width, height, size, target, divScale, divDelta);
            return;
        }

        double scale = 1.0 / (size * size);
        // Row sums for every padded row in double, then the column sum carried down the image by
        // adding the row that enters the box and subtracting the one that leaves it — in the order
        // the replaced routine did both, element by element.
        using var sums = new Scratch<double>((long)rows * width);
        nint at = (nint)sums.Pointer;
        ParallelRows.For(rows, (long)width * size, py =>
        {
            float* row = padded.Row<float>(py);
            double* s = (double*)at + (long)py * width;
            for (int x = 0; x < width; x++)
            {
                double v = row[x];
                for (int dx = 1; dx < size; dx++) v += row[x + dx];
                s[x] = v;
            }
        });
        ParallelRows.Blocks(width, (long)rows, (from, to) =>
        {
            double* s = (double*)at;
            int count = to - from;
            using var carried = new Scratch<double>(count);
            double* running = carried.Pointer;
            new Span<double>(running, count).Clear();
            for (int py = 0; py < rows; py++)
            {
                double* entering = s + (long)py * width + from;
                if (py < size - 1)
                {
                    for (int i = 0; i < count; i++) running[i] += entering[i];
                    continue;
                }
                double* leaving = s + (long)(py - (size - 1)) * width + from;
                float* d = target.Row<float>(py - (size - 1)) + from;
                for (int i = 0; i < count; i++)
                {
                    double s0 = running[i] + entering[i];
                    d[i] = (float)(s0 * scale);
                    running[i] = s0 - leaving[i];
                }
            }
        });
    }

    /// <summary>
    /// The plain sum over a <paramref name="size"/>² box of a byte image, as floats, with zero past
    /// the edges. Integer throughout, so exact.
    /// </summary>
    public static ImageBuffer BoxSum(ImageBuffer source, int size)
    {
        if (source.Type != PixelType.U8C1) throw new NotSupportedException($"Box sum of {source.Type}.");
        if (size % 2 == 0) throw new ArgumentException("Odd box sizes only.");
        var target = ImageBuffer.Uninitialized(source.Size, PixelType.F32C1);
        if (source.IsEmpty) return target;
        int r = size / 2, width = source.Width, height = source.Height;
        using var padded = Neighbourhood(source, r, r, Border.Constant);
        using var scratch = new Scratch<int>((long)padded.Height * width);
        nint across = (nint)scratch.Pointer;
        RowSums(padded, (int*)across, width, size);
        ColumnSums((int*)across, width, height, size, target, 0, 0);
        return target;
    }

    /// <summary>Each padded row's running sum over <paramref name="size"/> bytes, one per output column.</summary>
    private static void RowSums(ImageBuffer padded, int* sums, int width, int size)
    {
        nint at = (nint)sums;
        ParallelRows.For(padded.Height, padded.Width, py =>
        {
            byte* row = padded.Row(py);
            int* a = (int*)at + (long)py * width;
            int s = 0;
            for (int dx = 0; dx < size; dx++) s += row[dx];
            a[0] = s;
            for (int x = 1; x < width; x++)
            {
                s += row[x + size - 1] - row[x - 1];
                a[x] = s;
            }
        });
    }

    /// <summary>
    /// The column sums of <paramref name="size"/> consecutive row sums, carried down the image a row
    /// at a time across a band of columns, so the walk stays in rows. A float target gets the sums;
    /// a byte target gets them divided in the 23-bit fixed point described at <see cref="Blur"/>.
    /// </summary>
    private static void ColumnSums(int* sums, int width, int height, int size, ImageBuffer target, uint divScale, uint divDelta)
    {
        bool bytes = target.Type == PixelType.U8C1;
        nint at = (nint)sums;
        ParallelRows.Blocks(width, (long)height + size, (from, to) =>
        {
            int* a = (int*)at;
            int count = to - from;
            using var carried = new Scratch<int>(count);
            int* running = carried.Pointer;
            new Span<int>(running, count).Clear();
            for (int dy = 0; dy < size - 1; dy++)
            {
                int* row = a + (long)dy * width + from;
                for (int i = 0; i < count; i++) running[i] += row[i];
            }
            for (int y = 0; y < height; y++)
            {
                int* entering = a + (long)(y + size - 1) * width + from, leaving = a + (long)y * width + from;
                if (bytes)
                {
                    byte* d = target.Row(y) + from;
                    for (int i = 0; i < count; i++)
                    {
                        int s = running[i] + entering[i];
                        d[i] = (byte)(((uint)s + divDelta) * divScale >> 23);
                        running[i] = s - leaving[i];
                    }
                }
                else
                {
                    float* d = target.Row<float>(y) + from;
                    for (int i = 0; i < count; i++)
                    {
                        int s = running[i] + entering[i];
                        d[i] = s;
                        running[i] = s - leaving[i];
                    }
                }
            }
        });
    }

    /// <summary>
    /// The 3×3 Laplacian of a float image, mirrored at the edges, with the kernel the replaced library
    /// uses at that size: the diagonal neighbours at 2 and the centre at −8, the edge neighbours not at all.
    /// </summary>
    public static ImageBuffer Laplacian(ImageBuffer source)
    {
        if (source.Type != PixelType.F32C1) throw new NotSupportedException($"Laplacian of {source.Type}.");
        var target = ImageBuffer.Uninitialized(source.Size, PixelType.F32C1);
        if (source.IsEmpty) return target;
        using var padded = Neighbourhood(source, 1, 1, Border.Reflect101);
        int width = source.Width;
        ParallelRows.For(source.Height, width * 5, y =>
        {
            float* above = padded.Row<float>(y), middle = padded.Row<float>(y + 1), below = padded.Row<float>(y + 2);
            float* d = target.Row<float>(y);
            for (int x = 0; x < width; x++)
                d[x] = LaplacianAt(above[x], above[x + 2], middle[x + 1], below[x], below[x + 2]);
        });
        return target;
    }

    /// <summary>Which arithmetic <see cref="LaplacianAt"/> uses; settled by measurement, see there.</summary>
    internal static int LaplacianForm = 0;

    private static float LaplacianAt(float topLeft, float topRight, float centre, float bottomLeft, float bottomRight) => LaplacianForm switch
    {
        0 => 2 * topLeft + 2 * topRight + -8 * centre + 2 * bottomLeft + 2 * bottomRight,
        1 => MathF.FusedMultiplyAdd(2, bottomRight, MathF.FusedMultiplyAdd(2, bottomLeft, MathF.FusedMultiplyAdd(-8, centre, MathF.FusedMultiplyAdd(2, topRight, 2 * topLeft)))),
        2 => (topLeft + topRight + bottomLeft + bottomRight) * 2 - centre * 8,
        3 => ((topLeft + topRight) + (bottomLeft + bottomRight)) * 2 - 8 * centre,
        4 => 2 * (topLeft + topRight + bottomLeft + bottomRight - 4 * centre),
        5 => (topLeft + topRight + bottomLeft + bottomRight - 4 * centre) * 2,
        6 => topLeft * 2 + topRight * 2 + bottomLeft * 2 + bottomRight * 2 - centre * 8,
        7 => MathF.FusedMultiplyAdd(-8, centre, (topLeft + topRight + bottomLeft + bottomRight) * 2),
        _ => 2 * topLeft + 2 * topRight + 2 * bottomLeft + 2 * bottomRight - 8 * centre,
    };

    /// <summary>
    /// The Gaussian blur of a byte image with the edges repeated, in the replaced library's
    /// bit-exact fixed point: the kernel quantised to 1/256 with its rounding error carried along
    /// it, and the separable sums exact integers until the final rounding.
    /// </summary>
    public static ImageBuffer GaussianBlur(ImageBuffer source, double sigma)
    {
        if (source.Type is not (PixelType.U8C1 or PixelType.U8C3)) throw new NotSupportedException($"Gaussian blur of {source.Type}.");
        int size = Saturate.ToInt(sigma * 3 * 2 + 1) | 1;
        var target = ImageBuffer.Uninitialized(source.Size, source.Type);
        if (source.IsEmpty) return target;
        int width = source.Width, height = source.Height, channels = source.Channels;
        if (size == 1 || (width == 1 && height == 1))
        {
            source.CopyTo(target);
            return target;
        }
        // A single row or column is blurred along the other direction only.
        int sizeX = width == 1 ? 1 : size, sizeY = height == 1 ? 1 : size;
        var kernelX = GaussianKernel(sizeX, sigma);
        var kernelY = sizeY == sizeX ? kernelX : GaussianKernel(sizeY, sigma);
        int rx = sizeX / 2, ry = sizeY / 2;
        using var padded = Neighbourhood(source, rx, ry, Border.Replicate);
        int n = width * channels;

        // Across, into 8.8 fixed point: Σ pixel × weight, weights summing to 256.
        using var scratch = new Scratch<int>((long)padded.Height * n);
        nint across = (nint)scratch.Pointer;
        ParallelRows.For(padded.Height, (long)n * sizeX, py =>
        {
            byte* row = padded.Row(py);
            int* a = (int*)across + (long)py * n;
            fixed (int* k = kernelX)
                for (int i = 0; i < n; i++)
                {
                    int s = 0;
                    for (int t = 0; t < sizeX; t++) s += row[i + t * channels] * k[t];
                    a[i] = s;
                }
        });
        // Down, into 16.16, then rounded half up to a byte.
        ParallelRows.For(height, (long)n * sizeY, y =>
        {
            byte* d = target.Row(y);
            int* a = (int*)across;
            fixed (int* k = kernelY)
                for (int i = 0; i < n; i++)
                {
                    long s = 0;
                    for (int t = 0; t < sizeY; t++) s += (long)a[(long)(y + t) * n + i] * k[t];
                    d[i] = (byte)Math.Min(255, (s + (1 << 15)) >> 16);
                }
        });
        return target;
    }

    /// <summary>The Gaussian weights for a kernel of <paramref name="size"/> taps, in 1/256ths summing to 256.</summary>
    /// <remarks>
    /// The small sizes are the binomial kernels the replaced library hard-codes. Otherwise the taps
    /// are the normalised Gaussian, quantised from the outside in with each rounding error carried
    /// into the next tap, and the centre takes whatever is left so the sum is exact.
    /// </remarks>
    private static int[] GaussianKernel(int size, double sigma)
    {
        if (size == 1) return [256];
        double[]? fixedTaps = size switch
        {
            3 => [0.25, 0.5, 0.25],
            5 => [0.0625, 0.25, 0.375, 0.25, 0.0625],
            7 => [0.03125, 0.109375, 0.21875, 0.28125, 0.21875, 0.109375, 0.03125],
            9 => [4 / 256.0, 13 / 256.0, 30 / 256.0, 51 / 256.0, 60 / 256.0, 51 / 256.0, 30 / 256.0, 13 / 256.0, 4 / 256.0],
            _ => null,
        };
        double[] taps;
        if (fixedTaps is not null && sigma <= 0) taps = fixedTaps;
        else
        {
            double sigmaX = sigma > 0 ? sigma : size * 0.15 + 0.35;
            double scale2X = -0.125 / (sigmaX * sigmaX);
            int half = (size - 1) / 2;
            var values = new double[half];
            double sum = 0;
            for (int i = 0, x = 1 - size; i < half; i++, x += 2)
            {
                double t = Math.Exp(x * x * scale2X);
                values[i] = t;
                sum += t;
            }
            sum *= 2;
            sum += 1;
            double inverse = 1 / sum;
            taps = new double[size];
            for (int i = 0; i < half; i++)
            {
                double t = values[i] * inverse;
                taps[i] = t;
                taps[size - 1 - i] = t;
            }
            taps[half] = inverse;
        }

        var result = new int[size];
        double error = 0;
        long total = 0;
        int middle = size / 2;
        for (int i = 0; i < middle; i++)
        {
            double adjusted = taps[i] * 256 + error;
            long v = (long)Math.Round(adjusted, MidpointRounding.ToEven);
            error = adjusted - v;
            result[i] = result[size - 1 - i] = (int)v;
            total += v;
        }
        result[middle] = (int)(256 - total * 2);
        return result;
    }

    /// <summary>
    /// <paramref name="source"/> grown by <paramref name="rx"/> and <paramref name="ry"/>: the
    /// parent's pixels where there are any, <paramref name="border"/> beyond the edge of the image
    /// that owns them.
    /// </summary>
    internal static ImageBuffer Neighbourhood(ImageBuffer source, int rx, int ry, Border border)
    {
        int width = source.Width + rx * 2, height = source.Height + ry * 2;
        var padded = new ImageBuffer(width, height, source.Type);
        int size = source.Type.PixelSize();
        int rootWidth = source.Root.Width, rootHeight = source.Root.Height;
        int ox = source.Origin.X, oy = source.Origin.Y;
        byte* origin = source.Data - (long)oy * source.Stride - (long)ox * size;
        var columns = new int[width];
        for (int x = 0; x < width; x++) columns[x] = Interpolate(ox + x - rx, rootWidth, border);
        // The run of columns inside the image, copied in one go.
        int firstInside = Math.Max(0, rx - ox), lastInside = Math.Min(width, rootWidth - ox + rx);
        for (int y = 0; y < height; y++)
        {
            int sy = Interpolate(oy + y - ry, rootHeight, border);
            if (sy < 0) continue; // constant border: the padded image is already zero
            byte* from = origin + (long)sy * source.Stride, to = padded.Row(y);
            if (lastInside > firstInside)
            {
                long bytes = (long)(lastInside - firstInside) * size;
                Buffer.MemoryCopy(from + (long)(ox + firstInside - rx) * size, to + (long)firstInside * size, bytes, bytes);
            }
            for (int x = 0; x < firstInside; x++) CopyPixel(from, to, columns[x], x, size);
            for (int x = Math.Max(lastInside, firstInside); x < width; x++) CopyPixel(from, to, columns[x], x, size);
        }
        return padded;

        static void CopyPixel(byte* from, byte* to, int sx, int x, int size)
        {
            if (sx < 0) return;
            Buffer.MemoryCopy(from + (long)sx * size, to + (long)x * size, size, size);
        }
    }

    /// <summary>Where a coordinate past the edge reads from; −1 for the constant border.</summary>
    private static int Interpolate(int p, int length, Border border)
    {
        if ((uint)p < (uint)length) return p;
        switch (border)
        {
            case Border.Constant:
                return -1;
            case Border.Replicate:
                return p < 0 ? 0 : length - 1;
            default:
                if (length == 1) return 0;
                do
                {
                    if (p < 0) p = -p;
                    else p = length - 1 - (p - length) - 1;
                }
                while ((uint)p >= (uint)length);
                return p;
        }
    }
}
