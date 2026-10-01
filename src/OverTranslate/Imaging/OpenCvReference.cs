using OpenCvSharp;

namespace OverTranslate.Imaging;

/// <summary>
/// Transitional: routes chosen families of <c>OverTranslate.Imaging</c> back to OpenCV, so each
/// managed routine can be checked against the library it replaces inside the real pipeline. It is
/// deleted together with the package.
/// </summary>
internal static unsafe class OpenCvReference
{
    [Flags]
    public enum Families
    {
        None = 0,
        Morphology = 1 << 0,
        Resize = 1 << 1,
        Inpaint = 1 << 2,
        Filters = 1 << 3,
        Arithmetic = 1 << 4,
        Threshold = 1 << 5,
        Color = 1 << 6,
        Statistics = 1 << 7,
        Distance = 1 << 8,
        Conversion = 1 << 9,
        Channels = 1 << 10,
        All = (1 << 11) - 1,
    }

    /// <summary>Which families run on OpenCV.</summary>
    public static Families Use = Families.All;

    public static bool Handles(Families family) => (Use & family) != 0;

    /// <summary>When set, only this arithmetic operation runs managed; the rest of the family follows <see cref="Use"/>... or OpenCV.</summary>
    public static string? OnlyOperation;

    public static bool Handles(string operation) =>
        Handles(Families.Arithmetic) || (OnlyOperation is not null && OnlyOperation != operation);

    /// <summary>A header over the same pixels, with the parent the replaced filters read around a view.</summary>
    public static Mat Wrap(ImageBuffer image)
    {
        int size = image.Type.PixelSize();
        byte* root = image.Data - (long)image.Origin.Y * image.Stride - (long)image.Origin.X * size;
        using var whole = Mat.FromPixelData(image.Root.Height, image.Root.Width, TypeOf(image.Type), (IntPtr)root, image.Stride);
        return new Mat(whole, new Rect(image.Origin.X, image.Origin.Y, image.Width, image.Height));
    }

    private static MatType TypeOf(PixelType type) => type switch
    {
        PixelType.U8C1 => MatType.CV_8UC1,
        PixelType.U8C3 => MatType.CV_8UC3,
        PixelType.F32C1 => MatType.CV_32FC1,
        _ => MatType.CV_32FC3,
    };

    /// <summary>Runs an OpenCV call that must write into <paramref name="target"/> in place.</summary>
    private static void Into(ImageBuffer target, Action<Mat> call)
    {
        using var mat = Wrap(target);
        var before = mat.Data;
        call(mat);
        if (mat.Data != before) throw new InvalidOperationException("OpenCV reallocated the target.");
    }

    private static Mat Kernel(StructuringElement element)
    {
        var kernel = new Mat(element.Height, element.Width, MatType.CV_8UC1, Scalar.Black);
        for (int y = 0; y < element.Height; y++)
        {
            int half = element.HalfWidths[y];
            for (int x = element.AnchorX - half; half >= 0 && x <= element.AnchorX + half; x++) kernel.Set<byte>(y, x, 1);
        }
        return kernel;
    }

    public static void Morph(ImageBuffer source, ImageBuffer target, StructuringElement element, MorphTypes op, int iterations)
    {
        using var s = Wrap(source);
        using var k = Kernel(element);
        Into(target, t =>
        {
            if (op == MorphTypes.Dilate) Cv2.Dilate(s, t, k, iterations: iterations);
            else if (op == MorphTypes.Erode) Cv2.Erode(s, t, k, iterations: iterations);
            else Cv2.MorphologyEx(s, t, op, k);
        });
    }

    public static void Resize(ImageBuffer source, ImageBuffer target, Interpolation interpolation)
    {
        using var s = Wrap(source);
        Into(target, t => Cv2.Resize(s, t, new OpenCvSharp.Size(target.Width, target.Height), interpolation: interpolation switch
        {
            Interpolation.Area => InterpolationFlags.Area,
            Interpolation.Linear => InterpolationFlags.Linear,
            _ => InterpolationFlags.Nearest,
        }));
    }

    public static ImageBuffer Inpaint(ImageBuffer source, ImageBuffer mask, double radius)
    {
        var target = ImageBuffer.Uninitialized(source.Size, source.Type);
        using var s = Wrap(source);
        using var m = Wrap(mask);
        Into(target, t => Cv2.Inpaint(s, m, t, radius, InpaintTypes.NS));
        return target;
    }

    public static void Blur(ImageBuffer source, ImageBuffer target, int size)
    {
        using var s = Wrap(source);
        Into(target, t => Cv2.Blur(s, t, new OpenCvSharp.Size(size, size)));
    }

    public static ImageBuffer BoxSum(ImageBuffer source, int size)
    {
        var target = ImageBuffer.Uninitialized(source.Size, PixelType.F32C1);
        using var s = Wrap(source);
        Into(target, t => Cv2.BoxFilter(s, t, MatType.CV_32F, new OpenCvSharp.Size(size, size), normalize: false, borderType: BorderTypes.Constant));
        return target;
    }

    public static ImageBuffer Laplacian(ImageBuffer source)
    {
        var target = ImageBuffer.Uninitialized(source.Size, PixelType.F32C1);
        using var s = Wrap(source);
        Into(target, t => Cv2.Laplacian(s, t, MatType.CV_32F, ksize: 3));
        return target;
    }

    public static ImageBuffer GaussianBlur(ImageBuffer source, double sigma)
    {
        var target = ImageBuffer.Uninitialized(source.Size, source.Type);
        using var s = Wrap(source);
        Into(target, t => Cv2.GaussianBlur(s, t, new OpenCvSharp.Size(0, 0), sigma, borderType: BorderTypes.Replicate));
        return target;
    }

    public static ImageBuffer DistanceTransform(ImageBuffer mask)
    {
        var target = ImageBuffer.Uninitialized(mask.Size, PixelType.F32C1);
        using var s = Wrap(mask);
        Into(target, t => Cv2.DistanceTransform(s, t, DistanceTypes.L2, DistanceTransformMasks.Mask3));
        return target;
    }

    public static void Gray(ImageBuffer source, ImageBuffer target)
    {
        using var s = Wrap(source);
        Into(target, t => Cv2.CvtColor(s, t, ColorConversionCodes.BGR2GRAY));
    }

    public static double Otsu(ImageBuffer source)
    {
        using var s = Wrap(source);
        using var scratch = new Mat();
        return Cv2.Threshold(s, scratch, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
    }

    public static void Threshold(ImageBuffer source, ImageBuffer target, double threshold, double maximum)
    {
        using var s = Wrap(source);
        Into(target, t => Cv2.Threshold(s, t, threshold, maximum, ThresholdTypes.Binary));
    }

    public static double Mean(ImageBuffer image, ImageBuffer? mask)
    {
        using var s = Wrap(image);
        if (mask is null) return Cv2.Mean(s).Val0;
        using var m = Wrap(mask);
        return Cv2.Mean(s, m).Val0;
    }

    public static (double, double) MinMax(ImageBuffer image, ImageBuffer? mask)
    {
        using var s = Wrap(image);
        double least, most;
        if (mask is null) Cv2.MinMaxLoc(s, out least, out most, out _, out _);
        else
        {
            using var m = Wrap(mask);
            Cv2.MinMaxLoc(s, out least, out most, out _, out _, m);
        }
        return (least, most);
    }

    public static int CountNonZero(ImageBuffer image)
    {
        using var s = Wrap(image);
        return Cv2.CountNonZero(s);
    }

    public static System.Drawing.Rectangle BoundingRect(ImageBuffer mask)
    {
        using var s = Wrap(mask);
        var r = Cv2.BoundingRect(s);
        return new(r.X, r.Y, r.Width, r.Height);
    }

    public static void Convert(ImageBuffer source, ImageBuffer target, double scale)
    {
        using var s = Wrap(source);
        Into(target, t => s.ConvertTo(t, TypeOf(target.Type), scale));
    }

    public static ImageBuffer Merge(ImageBuffer a, ImageBuffer b, ImageBuffer c)
    {
        var target = ImageBuffer.Uninitialized(a.Size, PixelType.F32C3);
        using var ma = Wrap(a);
        using var mb = Wrap(b);
        using var mc = Wrap(c);
        Into(target, t => Cv2.Merge([ma, mb, mc], t));
        return target;
    }

    /// <summary>Element-wise operations, by name, with either a second image or a scalar.</summary>
    public static void Binary(string op, ImageBuffer a, ImageBuffer? b, double scalar, ImageBuffer target, double scale = 1)
    {
        using var ma = Wrap(a);
        using var mb = b is null ? null : Wrap(b);
        var sc = Scalar.All(scalar);
        Into(target, t =>
        {
            switch (op)
            {
                case "add": if (mb is null) Cv2.Add(ma, sc, t); else Cv2.Add(ma, mb, t); break;
                case "subtract": if (mb is null) Cv2.Subtract(ma, sc, t); else Cv2.Subtract(ma, mb, t); break;
                case "rsubtract": Cv2.Subtract(sc, ma, t); break;
                case "multiply": if (mb is null) Cv2.Multiply(ma, sc, t); else Cv2.Multiply(ma, mb, t); break;
                case "divide": if (mb is null) Cv2.Divide(ma, sc, t); else Cv2.Divide(ma, mb, t, scale); break;
                case "min": if (mb is null) Cv2.Min(ma, scalar, t); else Cv2.Min(ma, mb, t); break;
                case "max": if (mb is null) Cv2.Max(ma, scalar, t); else Cv2.Max(ma, mb, t); break;
                case "and": Cv2.BitwiseAnd(ma, mb!, t); break;
                case "or": Cv2.BitwiseOr(ma, mb!, t); break;
                case "not": Cv2.BitwiseNot(ma, t); break;
                case "absdiff": Cv2.Absdiff(ma, mb!, t); break;
                case "abs": Cv2.Absdiff(ma, Scalar.All(0), t); break;
                case "addbytes": Cv2.Add(ma, mb!, t, dtype: target.Type == PixelType.U8C3 ? MatType.CV_8UC3.Value : MatType.CV_8UC1.Value); break;
                default: throw new ArgumentException(op);
            }
        });
    }

    public static void AddWeighted(ImageBuffer a, double alpha, ImageBuffer b, double beta, double gamma, ImageBuffer target)
    {
        using var ma = Wrap(a);
        using var mb = Wrap(b);
        Into(target, t => Cv2.AddWeighted(ma, alpha, mb, beta, gamma, t));
    }
}
