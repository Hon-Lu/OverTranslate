using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace OverTranslate.Imaging;

/// <summary>
/// A structuring element whose every row is one run of pixels centred on the anchor — a rectangle,
/// a cross, or an ellipse of odd size, which is every element the repair builds.
/// </summary>
internal sealed class StructuringElement
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, bool), StructuringElement> Made = new();

    private StructuringElement(int width, int height, int[] halfWidths)
    {
        Width = width;
        Height = height;
        HalfWidths = halfWidths;
        Reach = halfWidths.Max();
    }

    public int Width { get; }

    public int Height { get; }

    public int AnchorX => Width / 2;

    public int AnchorY => Height / 2;

    /// <summary>For each row, how far the run reaches either side of the anchor; -1 for an empty row.</summary>
    public int[] HalfWidths { get; }

    /// <summary>The widest half-width of any row.</summary>
    public int Reach { get; }

    public bool IsSinglePixel => Width * Height == 1;

    public static StructuringElement Rectangle(int width, int height)
    {
        RequireOdd(width, height);
        return Made.GetOrAdd((width, height, false), key => new(key.Item1, key.Item2, Enumerable.Repeat(key.Item1 / 2, key.Item2).ToArray()));
    }

    /// <summary>The ellipse inscribed in the box, row by row as the replaced library drew it.</summary>
    /// <remarks>
    /// Each row's half-width is the ellipse's at that row's centre, rounded half to even — which is
    /// why a 3×3 "ellipse" is a cross.
    /// </remarks>
    public static StructuringElement Ellipse(int width, int height)
    {
        RequireOdd(width, height);
        return Made.GetOrAdd((width, height, true), key => DrawEllipse(key.Item1, key.Item2));
    }

    private static StructuringElement DrawEllipse(int width, int height)
    {
        int r = height / 2, c = width / 2;
        double inverse = r != 0 ? 1.0 / ((double)r * r) : 0;
        var half = new int[height];
        for (int i = 0; i < height; i++)
        {
            int dy = i - r;
            if (Math.Abs(dy) > r) { half[i] = -1; continue; }
            int dx = Saturate.ToInt(c * Math.Sqrt((r * r - dy * dy) * inverse));
            int j1 = Math.Max(c - dx, 0), j2 = Math.Min(c + dx + 1, width);
            half[i] = j2 > j1 ? c - j1 : -1;
        }
        return new(width, height, half);
    }

    private static void RequireOdd(int width, int height)
    {
        if (width <= 0 || height <= 0 || width % 2 == 0 || height % 2 == 0)
            throw new ArgumentException($"Only odd element sizes are supported, got {width}x{height}.");
    }
}

/// <summary>Dilation, erosion and the two hats, for byte and float images of one channel.</summary>
/// <remarks>
/// <para>Max and min are exact, so the results are the replaced library's bit for bit by
/// construction — given the same neighbourhood. Pixels outside the image that owns the pixels never
/// take part (the default border of that library's morphology), but pixels of a view's parent do:
/// see <see cref="ImageBuffer"/>.</para>
///
/// <para>Rather than visiting every point of the element, each distinct half-width <c>a</c> gets one
/// horizontal running extreme <c>H_a</c>, built from the one before it
/// (<c>H_{a+1}(x) = max(H_a(x-1), H_a(x+1))</c>), and the output is the extreme over the element's
/// rows of the matching <c>H_a</c>, shifted by that row. A 25×25 ellipse becomes 12 incremental
/// passes and 25 row combinations per pixel instead of 489 points.</para>
/// </remarks>
internal static unsafe class Morphology
{
    public static void Dilate(ImageBuffer source, ImageBuffer target, StructuringElement element, int iterations = 1)
    {
        Run(source, target, element, iterations, max: true);
    }

    public static void Erode(ImageBuffer source, ImageBuffer target, StructuringElement element, int iterations = 1)
    {
        Run(source, target, element, iterations, max: false);
    }

    /// <summary>What stands out brighter than its surroundings: the source less its opening.</summary>
    public static void TopHat(ImageBuffer source, ImageBuffer target, StructuringElement element)
    {
        using var opened = ImageBuffer.Uninitialized(source.Size, source.Type);
        Run(source, opened, element, 1, max: false);
        Run(opened, opened, element, 1, max: true);
        Arithmetic.Subtract(source, opened, target);
    }

    /// <summary>What stands out darker than its surroundings: the closing less the source.</summary>
    public static void BlackHat(ImageBuffer source, ImageBuffer target, StructuringElement element)
    {
        using var closed = ImageBuffer.Uninitialized(source.Size, source.Type);
        Run(source, closed, element, 1, max: true);
        Run(closed, closed, element, 1, max: false);
        Arithmetic.Subtract(closed, source, target);
    }

    private static void Run(ImageBuffer source, ImageBuffer target, StructuringElement element, int iterations, bool max)
    {
        if (source.Type is not (PixelType.U8C1 or PixelType.F32C1))
            throw new NotSupportedException($"Morphology on {source.Type}.");
        ImageBuffer.RequireSize(target, source.Size, source.Type);
        if (source.IsEmpty) return;
        if (iterations <= 0 || element.IsSinglePixel)
        {
            source.CopyTo(target);
            return;
        }
        // Every pass writes a fresh image and copies it over, so the target may be the source.
        using var pass = ImageBuffer.Uninitialized(source.Size, source.Type);
        Pass(source, pass, element, max);
        pass.CopyTo(target);
        // Later passes read the target, with the target's own surroundings — what the replaced
        // library does, and not the same as the source's when the two are different views.
        for (int i = 1; i < iterations; i++)
        {
            Pass(target, pass, element, max);
            pass.CopyTo(target);
        }
    }

    /// <summary>One pass over <paramref name="source"/> and as much of its parent as the element reaches.</summary>
    private static void Pass(ImageBuffer source, ImageBuffer target, StructuringElement element, bool max)
    {
        if (source.Type == PixelType.U8C1) Pass<byte>(source, target, element, max);
        else Pass<float>(source, target, element, max);
    }

    private static void Pass<T>(ImageBuffer source, ImageBuffer target, StructuringElement element, bool max)
        where T : unmanaged, INumber<T>
    {
        var half = element.HalfWidths;
        int anchorY = element.AnchorY, reach = element.Reach;
        int elem = sizeof(T);

        // The neighbourhood: the view, widened by the element as far as the owning image allows.
        int left = Math.Min(source.Origin.X, reach);
        int top = Math.Min(source.Origin.Y, anchorY);
        int right = Math.Min(source.Root.Width - source.Origin.X - source.Width, reach);
        int bottom = Math.Min(source.Root.Height - source.Origin.Y - source.Height, half.Length - 1 - anchorY);
        int w = source.Width + left + right, h = source.Height + top + bottom;
        byte* corner = source.Data - (long)top * source.Stride - (long)left * elem;
        int stride = source.Stride;

        // Plane a is H_a over the whole neighbourhood, packed.
        long planeSize = (long)w * h;
        using var planes = ImageBuffer.Uninitialized(w, h * (reach + 1), elem == 1 ? PixelType.U8C1 : PixelType.F32C1);
        T* p0 = (T*)planes.Data;
        for (int y = 0; y < h; y++)
            new ReadOnlySpan<T>(corner + (long)y * stride, w).CopyTo(new Span<T>(p0 + y * w, w));
        for (int a = 1; a <= reach; a++)
        {
            nint previous = (nint)(p0 + (a - 1) * planeSize), next = (nint)(p0 + a * planeSize);
            bool centre = a == 1; // H_0 -> H_1 is the one step whose two halves do not overlap
            ParallelRows.For(h, w * 2, y => Widen((T*)previous + (long)y * w, (T*)next + (long)y * w, w, max, centre));
        }

        nint planeBase = (nint)p0, targetBase = (nint)target.Data;
        int targetStride = target.Stride, innerWidth = source.Width;
        ParallelRows.For(source.Height, (long)innerWidth * half.Length, oy =>
        {
            int y = oy + top;
            T* d = (T*)(targetBase + (nint)((long)oy * targetStride));
            bool first = true;
            for (int k = 0; k < half.Length; k++)
            {
                if (half[k] < 0) continue;
                int sy = y + k - anchorY;
                if (sy < 0 || sy >= h) continue;
                T* row = (T*)planeBase + half[k] * planeSize + (long)sy * w + left;
                if (first)
                {
                    new ReadOnlySpan<T>(row, innerWidth).CopyTo(new Span<T>(d, innerWidth));
                    first = false;
                }
                else Combine(d, row, innerWidth, max);
            }
        });
    }

    /// <summary>H_a from H_{a-1}: each pixel takes the extreme of its two neighbours (and itself, at the first step).</summary>
    private static void Widen<T>(T* previous, T* next, int w, bool max, bool centre) where T : unmanaged, INumber<T>
    {
        if (w == 1)
        {
            next[0] = previous[0];
            return;
        }
        next[0] = Pick(previous[0], previous[1], max);
        int x = 1;
        if (Vector256.IsHardwareAccelerated)
            for (; x <= w - 1 - Vector256<T>.Count; x += Vector256<T>.Count)
            {
                var both = Pick(Vector256.Load(previous + x - 1), Vector256.Load(previous + x + 1), max);
                if (centre) both = Pick(both, Vector256.Load(previous + x), max);
                Vector256.Store(both, next + x);
            }
        if (Vector128.IsHardwareAccelerated)
            for (; x <= w - 1 - Vector128<T>.Count; x += Vector128<T>.Count)
            {
                var both = Pick(Vector128.Load(previous + x - 1), Vector128.Load(previous + x + 1), max);
                if (centre) both = Pick(both, Vector128.Load(previous + x), max);
                Vector128.Store(both, next + x);
            }
        for (; x < w - 1; x++) next[x] = Pick(Pick(previous[x - 1], previous[x + 1], max), previous[x], max);
        next[w - 1] = Pick(previous[w - 2], previous[w - 1], max);
    }

    private static void Combine<T>(T* d, T* row, int w, bool max) where T : unmanaged, INumber<T>
    {
        int x = 0;
        if (Vector256.IsHardwareAccelerated)
            for (; x <= w - Vector256<T>.Count; x += Vector256<T>.Count)
                Vector256.Store(Pick(Vector256.Load(d + x), Vector256.Load(row + x), max), d + x);
        if (Vector128.IsHardwareAccelerated)
            for (; x <= w - Vector128<T>.Count; x += Vector128<T>.Count)
                Vector128.Store(Pick(Vector128.Load(d + x), Vector128.Load(row + x), max), d + x);
        for (; x < w; x++) d[x] = Pick(d[x], row[x], max);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Pick<T>(T a, T b, bool max) where T : INumber<T> => max ? T.Max(a, b) : T.Min(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<T> Pick<T>(Vector256<T> a, Vector256<T> b, bool max) where T : unmanaged =>
        max ? Vector256.Max(a, b) : Vector256.Min(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<T> Pick<T>(Vector128<T> a, Vector128<T> b, bool max) where T : unmanaged =>
        max ? Vector128.Max(a, b) : Vector128.Min(a, b);
}
