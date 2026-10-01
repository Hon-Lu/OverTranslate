namespace OverTranslate.Imaging;

/// <summary>Bytes to floats and back, optionally scaled on the way.</summary>
internal static unsafe class Conversion
{
    /// <summary>
    /// A new image of <paramref name="type"/> (same channels) holding each element times
    /// <paramref name="scale"/>: the scale is rounded to float first and the product rounded once,
    /// then saturated half to even when the target is bytes.
    /// </summary>
    public static ImageBuffer Convert(ImageBuffer source, PixelType type, double scale = 1)
    {
        if (type.Channels() != source.Channels) throw new ArgumentException("Conversion keeps the channel count.");
        var target = ImageBuffer.Uninitialized(source.Size, type);
        try
        {
            Convert(source, target, scale);
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    public static void Convert(ImageBuffer source, ImageBuffer target, double scale = 1)
    {
        if (target.Size != source.Size || target.Channels != source.Channels) throw new ArgumentException("Mismatched shapes.");
        if (OpenCvReference.Handles(OpenCvReference.Families.Conversion)) { OpenCvReference.Convert(source, target, scale); return; }
        int n = source.Width * source.Channels;
        float s = (float)scale;
        bool scaled = scale != 1;
        switch (source.Type.IsFloat(), target.Type.IsFloat())
        {
            case (false, true):
                ParallelRows.For(source.Height, n, y =>
                {
                    byte* from = source.Row(y);
                    float* to = target.Row<float>(y);
                    if (scaled) for (int x = 0; x < n; x++) to[x] = from[x] * s;
                    else for (int x = 0; x < n; x++) to[x] = from[x];
                });
                break;
            case (true, false):
                ParallelRows.For(source.Height, n, y =>
                {
                    float* from = source.Row<float>(y);
                    byte* to = target.Row(y);
                    if (scaled) for (int x = 0; x < n; x++) to[x] = Saturate.ToByte(from[x] * s);
                    else for (int x = 0; x < n; x++) to[x] = Saturate.ToByte(from[x]);
                });
                break;
            case (true, true):
                if (!scaled) { source.CopyTo(target); break; }
                ParallelRows.For(source.Height, n, y =>
                {
                    float* from = source.Row<float>(y), to = target.Row<float>(y);
                    for (int x = 0; x < n; x++) to[x] = from[x] * s;
                });
                break;
            default:
                if (!scaled) { source.CopyTo(target); break; }
                ParallelRows.For(source.Height, n, y =>
                {
                    byte* from = source.Row(y), to = target.Row(y);
                    for (int x = 0; x < n; x++) to[x] = Saturate.ToByte(from[x] * s);
                });
                break;
        }
    }
}
