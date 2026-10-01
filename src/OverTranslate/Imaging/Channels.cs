using System.Drawing;

namespace OverTranslate.Imaging;

/// <summary>Putting channels together, tiling, and padding.</summary>
internal static unsafe class Channels
{
    /// <summary>Three single-channel float images as the channels of one.</summary>
    public static ImageBuffer Merge(ImageBuffer first, ImageBuffer second, ImageBuffer third)
    {
        if (first.Type != PixelType.F32C1) throw new NotSupportedException($"Merging {first.Type}.");
        ImageBuffer.RequireSameShape(first, second);
        ImageBuffer.RequireSameShape(first, third);
        var target = ImageBuffer.Uninitialized(first.Size, PixelType.F32C3);
        int width = first.Width;
        ParallelRows.For(first.Height, width * 3, y =>
        {
            float* a = first.Row<float>(y), b = second.Row<float>(y), c = third.Row<float>(y), d = target.Row<float>(y);
            for (int x = 0; x < width; x++, d += 3)
            {
                d[0] = a[x];
                d[1] = b[x];
                d[2] = c[x];
            }
        });
        return target;
    }

    /// <summary>The same single-channel float image in all three channels.</summary>
    public static ImageBuffer Spread(ImageBuffer single) => Merge(single, single, single);

    /// <summary><paramref name="source"/> repeated <paramref name="down"/> times down and <paramref name="across"/> times across.</summary>
    public static ImageBuffer Tile(ImageBuffer source, int down, int across)
    {
        var target = ImageBuffer.Uninitialized(source.Width * across, source.Height * down, source.Type);
        int bytes = source.RowBytes;
        for (int y = 0; y < target.Height; y++)
        {
            byte* from = source.Row(y % source.Height), to = target.Row(y);
            for (int i = 0; i < across; i++) Buffer.MemoryCopy(from, to + (long)i * bytes, bytes, bytes);
        }
        return target;
    }

    /// <summary>A new image with the edge pixels repeated outward by the given amounts.</summary>
    public static ImageBuffer PadReplicate(ImageBuffer source, int top, int bottom, int left, int right)
    {
        if (source.IsEmpty) throw new ArgumentException("Nothing to replicate.");
        var target = ImageBuffer.Uninitialized(source.Width + left + right, source.Height + top + bottom, source.Type);
        int size = source.Type.PixelSize(), width = target.Width;
        for (int y = 0; y < target.Height; y++)
        {
            byte* from = source.Row(Math.Clamp(y - top, 0, source.Height - 1)), to = target.Row(y);
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Clamp(x - left, 0, source.Width - 1);
                Buffer.MemoryCopy(from + sx * size, to + x * size, size, size);
            }
        }
        return target;
    }

    /// <summary>The <paramref name="area"/> of <paramref name="source"/> as an isolated copy.</summary>
    public static ImageBuffer Crop(ImageBuffer source, Rectangle area)
    {
        using var view = new ImageBuffer(source, area);
        return view.Clone();
    }
}
