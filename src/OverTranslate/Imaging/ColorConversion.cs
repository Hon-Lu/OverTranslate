namespace OverTranslate.Imaging;

/// <summary>BGR to grey.</summary>
internal static unsafe class ColorConversion
{
    // Rec. 601 luma, in the 15-bit fixed point of the replaced library: 0.114, 0.587, 0.299.
    private const int BlueWeight = 3735, GreenWeight = 19235, RedWeight = 9798, Shift = 15;

    private const float BlueWeightF = 0.114f, GreenWeightF = 0.587f, RedWeightF = 0.299f;

    public static ImageBuffer ToGray(ImageBuffer source)
    {
        var target = ImageBuffer.Uninitialized(source.Size, source.Type.WithChannels(1));
        try
        {
            ToGray(source, target);
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    public static void ToGray(ImageBuffer source, ImageBuffer target)
    {
        if (source.Channels != 3) throw new NotSupportedException($"Grey from {source.Type}.");
        ImageBuffer.RequireSize(target, source.Size, source.Type.WithChannels(1));
        if (OpenCvReference.Handles(OpenCvReference.Families.Color)) { OpenCvReference.Gray(source, target); return; }
        int width = source.Width;
        if (source.Type == PixelType.U8C3)
        {
            ParallelRows.For(source.Height, width * 3, y =>
            {
                byte* s = source.Row(y), d = target.Row(y);
                for (int x = 0; x < width; x++, s += 3)
                    d[x] = (byte)((s[0] * BlueWeight + s[1] * GreenWeight + s[2] * RedWeight + (1 << (Shift - 1))) >> Shift);
            });
            return;
        }
        // Floats were never the library's own code: it hands them to IPP, whose arithmetic was found
        // by trying every association against it — green's product first, then blue and red each
        // fused into the sum. Its own vector path rounds differently in the last place.
        ParallelRows.For(source.Height, width * 3, y =>
        {
            float* s = source.Row<float>(y), d = target.Row<float>(y);
            for (int x = 0; x < width; x++, s += 3)
                d[x] = MathF.FusedMultiplyAdd(s[2], RedWeightF, MathF.FusedMultiplyAdd(s[0], BlueWeightF, s[1] * GreenWeightF));
        });
    }
}
