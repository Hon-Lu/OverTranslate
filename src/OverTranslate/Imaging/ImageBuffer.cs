using System.Drawing;
using System.Runtime.CompilerServices;

namespace OverTranslate.Imaging;

/// <summary>The four element layouts the repair works in: bytes or floats, grey or BGR.</summary>
internal enum PixelType : byte
{
    U8C1,
    U8C3,
    F32C1,
    F32C3,
}

internal static class PixelTypes
{
    public static int Channels(this PixelType type) => type is PixelType.U8C3 or PixelType.F32C3 ? 3 : 1;

    public static bool IsFloat(this PixelType type) => type is PixelType.F32C1 or PixelType.F32C3;

    /// <summary>Bytes in one channel of one pixel.</summary>
    public static int Depth(this PixelType type) => type.IsFloat() ? 4 : 1;

    public static int PixelSize(this PixelType type) => type.Depth() * type.Channels();

    public static PixelType WithChannels(this PixelType type, int channels) => (type.IsFloat(), channels) switch
    {
        (false, 1) => PixelType.U8C1,
        (false, 3) => PixelType.U8C3,
        (true, 1) => PixelType.F32C1,
        (true, 3) => PixelType.F32C3,
        _ => throw new ArgumentOutOfRangeException(nameof(channels)),
    };
}

/// <summary>
/// A two-dimensional image: rows of pixels, either owning them or a rectangle of another image's.
/// </summary>
/// <remarks>
/// <para>A view made with <see cref="ImageBuffer(ImageBuffer, Rectangle)"/> shares its parent's
/// pixels and remembers where it sits in them, which is not bookkeeping for its own sake. The
/// neighbourhood filters this replaced read the parent's pixels around a view as context — only the
/// edge of the image that owns the pixels counts as a border — and the repair was tuned on exactly
/// those answers. Treating a view as an image of its own changes what a filter sees along every edge
/// of every recognition box: measured on the subtitle bands, by up to 157 levels. So every filter
/// here that reads a neighbourhood asks <see cref="Root"/> and <see cref="Origin"/> for how far it
/// may look, and a copy made with <see cref="Clone"/> is what starts a new, isolated image.</para>
///
/// <para>Rows are not padded: an image that owns its pixels is one contiguous block, and a view's
/// stride is its parent's.</para>
/// </remarks>
internal sealed unsafe class ImageBuffer : IDisposable
{
    private PixelStorage? _storage;
    private readonly byte* _data;

    /// <summary>A new image of <paramref name="width"/> by <paramref name="height"/>, every byte zero.</summary>
    public ImageBuffer(int width, int height, PixelType type) : this(width, height, type, clear: true) { }

    public ImageBuffer(Size size, PixelType type) : this(size.Width, size.Height, type, clear: true) { }

    private ImageBuffer(int width, int height, PixelType type, bool clear)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        Type = type;
        Stride = width * type.PixelSize();
        long bytes = (long)Stride * height;
        _storage = PixelStorage.Rent(Math.Max(1, bytes));
        _data = _storage.Pointer;
        if (clear) new Span<byte>(_data, (int)bytes).Clear();
        Root = new Size(width, height);
    }

    /// <summary>The <paramref name="area"/> of <paramref name="parent"/>, sharing its pixels.</summary>
    public ImageBuffer(ImageBuffer parent, Rectangle area)
    {
        var storage = parent.Storage;
        if (area.X < 0 || area.Y < 0 || area.Width < 0 || area.Height < 0
            || area.Right > parent.Width || area.Bottom > parent.Height)
            throw new ArgumentOutOfRangeException(nameof(area), $"{area} is outside {parent.Width}x{parent.Height}.");
        storage.AddReference();
        _storage = storage;
        Width = area.Width;
        Height = area.Height;
        Type = parent.Type;
        Stride = parent.Stride;
        _data = parent._data + (long)area.Y * parent.Stride + (long)area.X * parent.Type.PixelSize();
        Root = parent.Root;
        Origin = new Point(parent.Origin.X + area.X, parent.Origin.Y + area.Y);
    }

    /// <summary>A new image whose content is whatever the pool last held there, for outputs written in full.</summary>
    public static ImageBuffer Uninitialized(int width, int height, PixelType type) => new(width, height, type, clear: false);

    public static ImageBuffer Uninitialized(Size size, PixelType type) => new(size.Width, size.Height, type, clear: false);

    /// <summary>A new three-channel byte image painted one colour.</summary>
    public static ImageBuffer Solid(Size size, byte blue, byte green, byte red)
    {
        var image = Uninitialized(size, PixelType.U8C3);
        for (int y = 0; y < image.Height; y++)
        {
            byte* row = image.Row(y);
            for (int x = 0; x < image.Width; x++)
            {
                row[x * 3] = blue;
                row[x * 3 + 1] = green;
                row[x * 3 + 2] = red;
            }
        }
        return image;
    }

    /// <summary>A new single-channel float image holding <paramref name="values"/>, row by row.</summary>
    public static ImageBuffer FromFloats(int width, int height, ReadOnlySpan<float> values)
    {
        if (values.Length != width * height) throw new ArgumentException("Wrong number of values.", nameof(values));
        var image = Uninitialized(width, height, PixelType.F32C1);
        values.CopyTo(new Span<float>(image._data, values.Length));
        return image;
    }

    public int Width { get; }

    public int Height { get; }

    public PixelType Type { get; }

    public int Channels => Type.Channels();

    public Size Size => new(Width, Height);

    /// <summary>Bytes from the start of one row to the start of the next.</summary>
    public int Stride { get; }

    /// <summary>The size of the image that owns these pixels.</summary>
    public Size Root { get; }

    /// <summary>Where this image's top-left pixel sits in <see cref="Root"/>.</summary>
    public Point Origin { get; }

    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>Bytes of one row that are pixels.</summary>
    public int RowBytes => Width * Type.PixelSize();

    public bool IsContinuous => Stride == RowBytes || Height <= 1;

    private PixelStorage Storage => _storage ?? throw new ObjectDisposedException(nameof(ImageBuffer));

    /// <summary>The first byte of the first row. Valid while this image is.</summary>
    public byte* Data
    {
        get
        {
            _ = Storage;
            return _data;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* Row(int y) => _data + (long)y * Stride;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T* Row<T>(int y) where T : unmanaged => (T*)(_data + (long)y * Stride);

    public Span<byte> RowSpan(int y) => new(Row(y), RowBytes);

    /// <summary>An isolated copy: its own pixels, and nothing around it to read.</summary>
    public ImageBuffer Clone()
    {
        var copy = Uninitialized(Width, Height, Type);
        CopyTo(copy);
        return copy;
    }

    public void CopyTo(ImageBuffer target)
    {
        RequireSameShape(this, target);
        if (target._data == _data) return;
        int bytes = RowBytes;
        for (int y = 0; y < Height; y++)
            Buffer.MemoryCopy(Row(y), target.Row(y), bytes, bytes);
    }

    /// <summary>Copies the pixels where <paramref name="mask"/> is not zero.</summary>
    public void CopyTo(ImageBuffer target, ImageBuffer mask)
    {
        RequireSameShape(this, target);
        RequireMask(mask, Size);
        int size = Type.PixelSize();
        for (int y = 0; y < Height; y++)
        {
            byte* from = Row(y), to = target.Row(y), where = mask.Row(y);
            if (size == 1)
            {
                for (int x = 0; x < Width; x++)
                    if (where[x] != 0) to[x] = from[x];
            }
            else if (size == 3)
            {
                for (int x = 0; x < Width; x++)
                    if (where[x] != 0)
                    {
                        to[x * 3] = from[x * 3];
                        to[x * 3 + 1] = from[x * 3 + 1];
                        to[x * 3 + 2] = from[x * 3 + 2];
                    }
            }
            else
            {
                for (int x = 0; x < Width; x++)
                    if (where[x] != 0) Buffer.MemoryCopy(from + x * size, to + x * size, size, size);
            }
        }
    }

    /// <summary>Every channel of every pixel set to <paramref name="value"/>, saturated to the element type.</summary>
    public void SetTo(double value)
    {
        if (Type.IsFloat())
        {
            float v = (float)value;
            for (int y = 0; y < Height; y++) new Span<float>(Row(y), Width * Channels).Fill(v);
        }
        else
        {
            byte v = Saturate.ToByte(value);
            for (int y = 0; y < Height; y++) new Span<byte>(Row(y), RowBytes).Fill(v);
        }
    }

    /// <summary>Every channel set to <paramref name="value"/> where <paramref name="mask"/> is not zero.</summary>
    public void SetTo(double value, ImageBuffer mask)
    {
        RequireMask(mask, Size);
        int channels = Channels;
        for (int y = 0; y < Height; y++)
        {
            byte* where = mask.Row(y);
            if (Type.IsFloat())
            {
                float v = (float)value;
                float* row = Row<float>(y);
                for (int x = 0; x < Width; x++)
                    if (where[x] != 0)
                        for (int c = 0; c < channels; c++) row[x * channels + c] = v;
            }
            else
            {
                byte v = Saturate.ToByte(value);
                byte* row = Row(y);
                for (int x = 0; x < Width; x++)
                    if (where[x] != 0)
                        for (int c = 0; c < channels; c++) row[x * channels + c] = v;
            }
        }
    }

    public void Dispose()
    {
        var storage = Interlocked.Exchange(ref _storage, null);
        storage?.Release();
    }

    internal static void RequireSameShape(ImageBuffer a, ImageBuffer b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.Type != b.Type)
            throw new ArgumentException($"Expected {a.Width}x{a.Height} {a.Type}, got {b.Width}x{b.Height} {b.Type}.");
    }

    internal static void RequireSize(ImageBuffer image, Size size, PixelType type)
    {
        if (image.Width != size.Width || image.Height != size.Height || image.Type != type)
            throw new ArgumentException($"Expected {size.Width}x{size.Height} {type}, got {image.Width}x{image.Height} {image.Type}.");
    }

    internal static void RequireMask(ImageBuffer mask, Size size) => RequireSize(mask, size, PixelType.U8C1);
}
