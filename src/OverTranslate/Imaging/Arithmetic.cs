using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace OverTranslate.Imaging;

/// <summary>Per-element arithmetic, comparisons and bitwise operations.</summary>
/// <remarks>
/// Float operations round once per operation exactly as the library this replaced did — one add,
/// one multiply, one divide, never fused — so the vector and scalar paths agree element for
/// element, and so do the results with what the repair was tuned on. Bytes saturate. Every target
/// must already have the shape of the inputs and may be one of them.
/// </remarks>
internal static unsafe class Arithmetic
{
    public static void Add(ImageBuffer a, ImageBuffer b, ImageBuffer target) => Binary<AddOp>(a, b, target);

    public static void Subtract(ImageBuffer a, ImageBuffer b, ImageBuffer target)
    {
        if (a.Type.IsFloat()) Binary<SubtractOp>(a, b, target);
        else Binary<SubtractSaturatedOp>(a, b, target);
    }

    public static void Multiply(ImageBuffer a, ImageBuffer b, ImageBuffer target) => Binary<MultiplyOp>(a, b, target);

    /// <summary><c>a / b</c>, or <c>(a * scale) / b</c> with the scale rounded to float first.</summary>
    public static void Divide(ImageBuffer a, ImageBuffer b, ImageBuffer target, double scale = 1)
    {
        if (OpenCvReference.Handles("divide")) { OpenCvReference.Binary("divide", a, b, 0, target, scale); return; }
        float s = (float)scale;
        if (s == 1) Binary<DivideOp>(a, b, target);
        else
        {
            using var scaled = ImageBuffer.Uninitialized(a.Size, a.Type);
            Scalar<MultiplyOp>(a, s, scaled);
            Binary<DivideOp>(scaled, b, target);
        }
    }

    public static void Min(ImageBuffer a, ImageBuffer b, ImageBuffer target) => Binary<MinOp>(a, b, target);

    public static void Max(ImageBuffer a, ImageBuffer b, ImageBuffer target) => Binary<MaxOp>(a, b, target);

    public static void And(ImageBuffer a, ImageBuffer b, ImageBuffer target) => Binary<AndOp>(a, b, target);

    public static void Or(ImageBuffer a, ImageBuffer b, ImageBuffer target) => Binary<OrOp>(a, b, target);

    public static void Not(ImageBuffer a, ImageBuffer target)
    {
        RequireBytes(a);
        ImageBuffer.RequireSameShape(a, target);
        if (OpenCvReference.Handles("not")) { OpenCvReference.Binary("not", a, null, 0, target); return; }
        int n = a.RowBytes;
        for (int y = 0; y < a.Height; y++)
        {
            byte* s = a.Row(y), d = target.Row(y);
            for (int x = 0; x < n; x++) d[x] = (byte)~s[x];
        }
    }

    /// <summary>|a − b| per element, for bytes.</summary>
    public static void AbsoluteDifference(ImageBuffer a, ImageBuffer b, ImageBuffer target) => Binary<AbsoluteDifferenceOp>(a, b, target);

    public static void Absolute(ImageBuffer a, ImageBuffer target)
    {
        RequireFloats(a);
        ImageBuffer.RequireSameShape(a, target);
        if (OpenCvReference.Handles("abs")) { OpenCvReference.Binary("abs", a, null, 0, target); return; }
        int n = a.Width * a.Channels;
        for (int y = 0; y < a.Height; y++)
        {
            float* s = a.Row<float>(y), d = target.Row<float>(y);
            for (int x = 0; x < n; x++) d[x] = MathF.Abs(s[x]);
        }
    }

    public static void Add(ImageBuffer a, double value, ImageBuffer target) => Scalar<AddOp>(a, value, target);

    public static void Subtract(ImageBuffer a, double value, ImageBuffer target) => Scalar<SubtractOp>(a, value, target);

    /// <summary><c>value − a</c> per element.</summary>
    public static void Subtract(double value, ImageBuffer a, ImageBuffer target) => Scalar<ReverseSubtractOp>(a, value, target);

    public static void Divide(ImageBuffer a, double value, ImageBuffer target) => Scalar<DivideOp>(a, value, target);

    public static void Min(ImageBuffer a, double value, ImageBuffer target) => Scalar<MinOp>(a, value, target);

    public static void Max(ImageBuffer a, double value, ImageBuffer target) => Scalar<MaxOp>(a, value, target);

    /// <summary>A byte image plus a float image of the same channels, back into bytes.</summary>
    /// <remarks>
    /// The float is rounded to a whole number first, half to even, and the sum taken in integers and
    /// saturated — not the sum rounded. That is what the replaced library does whenever the result is
    /// an integer type and only one input is not, and the two disagree by a level wherever the float
    /// ends in exactly a half.
    /// </remarks>
    public static void AddToBytes(ImageBuffer bytes, ImageBuffer floats, ImageBuffer target)
    {
        RequireBytes(bytes);
        RequireFloats(floats);
        if (bytes.Size != floats.Size || bytes.Channels != floats.Channels)
            throw new ArgumentException("Mismatched shapes.");
        ImageBuffer.RequireSameShape(bytes, target);
        if (OpenCvReference.Handles("addbytes")) { OpenCvReference.Binary("addbytes", bytes, floats, 0, target); return; }
        int n = bytes.Width * bytes.Channels;
        for (int y = 0; y < bytes.Height; y++)
        {
            byte* a = bytes.Row(y), d = target.Row(y);
            float* b = floats.Row<float>(y);
            for (int x = 0; x < n; x++) d[x] = (byte)Math.Clamp(a[x] + Saturate.ToInt(b[x]), 0, 255);
        }
    }

    /// <summary><c>a·alpha + b·beta + gamma</c> on bytes, in float, saturated.</summary>
    public static void AddWeighted(ImageBuffer a, double alpha, ImageBuffer b, double beta, double gamma, ImageBuffer target)
    {
        RequireBytes(a);
        ImageBuffer.RequireSameShape(a, b);
        ImageBuffer.RequireSameShape(a, target);
        if (OpenCvReference.Handles("addweighted")) { OpenCvReference.AddWeighted(a, alpha, b, beta, gamma, target); return; }
        float fa = (float)alpha, fb = (float)beta, fg = (float)gamma;
        int n = a.RowBytes;
        for (int y = 0; y < a.Height; y++)
        {
            byte* s1 = a.Row(y), s2 = b.Row(y), d = target.Row(y);
            for (int x = 0; x < n; x++) d[x] = Saturate.ToByte(MathF.FusedMultiplyAdd(s1[x], fa, MathF.FusedMultiplyAdd(s2[x], fb, fg)));
        }
    }

    private static void Binary<TOp>(ImageBuffer a, ImageBuffer b, ImageBuffer target) where TOp : IOperation
    {
        ImageBuffer.RequireSameShape(a, b);
        ImageBuffer.RequireSameShape(a, target);
        if (OpenCvReference.Handles(TOp.Name)) { OpenCvReference.Binary(TOp.Name, a, b, 0, target); return; }
        int n = a.Width * a.Channels;
        if (a.Type.IsFloat())
        {
            ParallelRows.For(a.Height, n, y => Row<TOp, float>(a.Row<float>(y), b.Row<float>(y), target.Row<float>(y), n));
        }
        else
        {
            if (!TOp.Bytes) throw new NotSupportedException($"{typeof(TOp).Name} on bytes.");
            ParallelRows.For(a.Height, n, y => Row<TOp, byte>(a.Row(y), b.Row(y), target.Row(y), n));
        }
    }

    /// <summary>The scalar is first brought to the element type, as the replaced library did.</summary>
    private static void Scalar<TOp>(ImageBuffer a, double value, ImageBuffer target) where TOp : IOperation
    {
        ImageBuffer.RequireSameShape(a, target);
        if (OpenCvReference.Handles(TOp.Name + "-scalar")) { OpenCvReference.Binary(TOp.Name, a, null, value, target); return; }
        int n = a.Width * a.Channels;
        if (a.Type.IsFloat())
        {
            float v = (float)value;
            ParallelRows.For(a.Height, n, y => ScalarRow<TOp, float>(a.Row<float>(y), v, target.Row<float>(y), n));
        }
        else
        {
            if (!TOp.Bytes) throw new NotSupportedException($"{typeof(TOp).Name} on bytes.");
            byte v = Saturate.ToByte(value);
            ParallelRows.For(a.Height, n, y => ScalarRow<TOp, byte>(a.Row(y), v, target.Row(y), n));
        }
    }

    private static void Row<TOp, T>(T* a, T* b, T* d, int n) where TOp : IOperation where T : unmanaged
    {
        int x = 0;
        if (Vector256.IsHardwareAccelerated)
            for (; x <= n - Vector256<T>.Count; x += Vector256<T>.Count)
                Vector256.Store(TOp.Apply(Vector256.Load(a + x), Vector256.Load(b + x)), d + x);
        if (Vector128.IsHardwareAccelerated)
            for (; x <= n - Vector128<T>.Count; x += Vector128<T>.Count)
                Vector128.Store(TOp.Apply(Vector128.Load(a + x), Vector128.Load(b + x)), d + x);
        for (; x < n; x++) d[x] = TOp.Apply(a[x], b[x]);
    }

    private static void ScalarRow<TOp, T>(T* a, T value, T* d, int n) where TOp : IOperation where T : unmanaged
    {
        int x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var v = Vector256.Create(value);
            for (; x <= n - Vector256<T>.Count; x += Vector256<T>.Count)
                Vector256.Store(TOp.Apply(Vector256.Load(a + x), v), d + x);
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var v = Vector128.Create(value);
            for (; x <= n - Vector128<T>.Count; x += Vector128<T>.Count)
                Vector128.Store(TOp.Apply(Vector128.Load(a + x), v), d + x);
        }
        for (; x < n; x++) d[x] = TOp.Apply(a[x], value);
    }

    private static void RequireBytes(ImageBuffer image)
    {
        if (image.Type.IsFloat()) throw new NotSupportedException($"Expected bytes, got {image.Type}.");
    }

    private static void RequireFloats(ImageBuffer image)
    {
        if (!image.Type.IsFloat()) throw new NotSupportedException($"Expected floats, got {image.Type}.");
    }

    /// <summary>One operation, written once for scalars and both vector widths.</summary>
    private interface IOperation
    {
        /// <summary>Whether the operation is defined on bytes; floats always are.</summary>
        static abstract bool Bytes { get; }

        /// <summary>Transitional: the operation's name for <see cref="OpenCvReference.Binary"/>.</summary>
        static abstract string Name { get; }

        static abstract T Apply<T>(T a, T b) where T : unmanaged;

        static abstract Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged;

        static abstract Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged;
    }

    // Only byte and float ever reach these, and the JIT folds each typeof test away.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float F<T>(T v) where T : unmanaged => Unsafe.As<T, float>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte B<T>(T v) where T : unmanaged => Unsafe.As<T, byte>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T As<T>(float v) where T : unmanaged => Unsafe.As<float, T>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T As<T>(byte v) where T : unmanaged => Unsafe.As<byte, T>(ref v);

    private readonly struct AddOp : IOperation
    {
        public static string Name => "add";
        public static bool Bytes => false;
        public static T Apply<T>(T a, T b) where T : unmanaged => As<T>(F(a) + F(b));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => a + b;
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => a + b;
    }

    private readonly struct SubtractOp : IOperation
    {
        public static string Name => "subtract";
        public static bool Bytes => false;
        public static T Apply<T>(T a, T b) where T : unmanaged => As<T>(F(a) - F(b));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => a - b;
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => a - b;
    }

    private readonly struct ReverseSubtractOp : IOperation
    {
        public static string Name => "rsubtract";
        public static bool Bytes => false;
        public static T Apply<T>(T a, T b) where T : unmanaged => As<T>(F(b) - F(a));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => b - a;
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => b - a;
    }

    private readonly struct SubtractSaturatedOp : IOperation
    {
        public static string Name => "subtract";
        public static bool Bytes => true;
        public static T Apply<T>(T a, T b) where T : unmanaged => As<T>((byte)Math.Max(0, B(a) - B(b)));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => a - Vector128.Min(a, b);
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => a - Vector256.Min(a, b);
    }

    private readonly struct MultiplyOp : IOperation
    {
        public static string Name => "multiply";
        public static bool Bytes => false;
        public static T Apply<T>(T a, T b) where T : unmanaged => As<T>(F(a) * F(b));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => a * b;
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => a * b;
    }

    private readonly struct DivideOp : IOperation
    {
        public static string Name => "divide";
        public static bool Bytes => false;
        public static T Apply<T>(T a, T b) where T : unmanaged => As<T>(F(a) / F(b));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => a / b;
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => a / b;
    }

    private readonly struct MinOp : IOperation
    {
        public static string Name => "min";
        public static bool Bytes => true;
        public static T Apply<T>(T a, T b) where T : unmanaged =>
            typeof(T) == typeof(float) ? As<T>(F(a) <= F(b) ? F(a) : F(b)) : As<T>(Math.Min(B(a), B(b)));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => Vector128.Min(a, b);
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => Vector256.Min(a, b);
    }

    private readonly struct MaxOp : IOperation
    {
        public static string Name => "max";
        public static bool Bytes => true;
        public static T Apply<T>(T a, T b) where T : unmanaged =>
            typeof(T) == typeof(float) ? As<T>(F(a) >= F(b) ? F(a) : F(b)) : As<T>(Math.Max(B(a), B(b)));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => Vector128.Max(a, b);
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => Vector256.Max(a, b);
    }

    private readonly struct AndOp : IOperation
    {
        public static string Name => "and";
        public static bool Bytes => true;
        public static T Apply<T>(T a, T b) where T : unmanaged => As<T>((byte)(B(a) & B(b)));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => a & b;
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => a & b;
    }

    private readonly struct OrOp : IOperation
    {
        public static string Name => "or";
        public static bool Bytes => true;
        public static T Apply<T>(T a, T b) where T : unmanaged => As<T>((byte)(B(a) | B(b)));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => a | b;
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => a | b;
    }

    private readonly struct AbsoluteDifferenceOp : IOperation
    {
        public static string Name => "absdiff";
        public static bool Bytes => true;
        public static T Apply<T>(T a, T b) where T : unmanaged => As<T>((byte)Math.Abs(B(a) - B(b)));
        public static Vector128<T> Apply<T>(Vector128<T> a, Vector128<T> b) where T : unmanaged => Vector128.Max(a, b) - Vector128.Min(a, b);
        public static Vector256<T> Apply<T>(Vector256<T> a, Vector256<T> b) where T : unmanaged => Vector256.Max(a, b) - Vector256.Min(a, b);
    }
}
