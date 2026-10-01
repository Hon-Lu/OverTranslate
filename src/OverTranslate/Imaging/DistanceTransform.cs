namespace OverTranslate.Imaging;

/// <summary>How far each pixel of a mask is from the nearest pixel outside it.</summary>
internal static unsafe class DistanceTransform
{
    // The 3×3 chamfer approximation of Euclidean distance: a step across or down costs 0.955, a
    // diagonal 1.3693.
    private const float Straight = 0.955f, Diagonal = 1.3693f;

    /// <summary>
    /// For each non-zero pixel, the chamfer distance to the nearest zero pixel; zero on zero pixels.
    /// Past the edge of the image counts as infinitely far, not as zero.
    /// </summary>
    /// <remarks>
    /// The replaced library hands this to IPP, which sums the steps in float; its own code sums them
    /// in 16-bit fixed point, and the two differ in the last few places on most pixels. This sums in
    /// float too, in two raster passes, and then agrees with IPP on all but the rare pixel reached
    /// along two paths that round differently — a single unit in the last place, where IPP keeps the
    /// larger of two equal distances. The one use of this is a comparison with a radius of five, and
    /// no sum of these two steps lies anywhere near a whole number, so that difference never decides
    /// anything.
    /// </remarks>
    public static ImageBuffer Chamfer3(ImageBuffer mask)
    {
        if (mask.Type != PixelType.U8C1) throw new NotSupportedException($"Distance transform of {mask.Type}.");
        int width = mask.Width, height = mask.Height;
        var target = ImageBuffer.Uninitialized(mask.Size, PixelType.F32C1);
        if (mask.IsEmpty) return target;
        int step = width + 2;
        using var temp = new Scratch<float>((long)step * (height + 2));
        float* t0 = temp.Pointer;
        new Span<float>(t0, step * (height + 2)).Fill(float.MaxValue);
        for (int y = 0; y < height; y++)
        {
            float* t = t0 + (long)(y + 1) * step + 1;
            byte* s = mask.Row(y);
            for (int x = 0; x < width; x++)
            {
                if (s[x] == 0) { t[x] = 0; continue; }
                float best = t[x - step - 1] + Diagonal;
                best = MathF.Min(best, t[x - step] + Straight);
                best = MathF.Min(best, t[x - step + 1] + Diagonal);
                best = MathF.Min(best, t[x - 1] + Straight);
                t[x] = best;
            }
        }
        for (int y = height - 1; y >= 0; y--)
        {
            float* t = t0 + (long)(y + 1) * step + 1;
            float* d = target.Row<float>(y);
            for (int x = width - 1; x >= 0; x--)
            {
                float best = t[x];
                best = MathF.Min(best, t[x + step + 1] + Diagonal);
                best = MathF.Min(best, t[x + step] + Straight);
                best = MathF.Min(best, t[x + step - 1] + Diagonal);
                best = MathF.Min(best, t[x + 1] + Straight);
                t[x] = best;
                d[x] = best;
            }
        }
        return target;
    }
}
