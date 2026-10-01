namespace OverTranslate.Imaging;

/// <summary>Binary thresholds, and Otsu's choice of one.</summary>
internal static unsafe class Threshold
{
    /// <summary>
    /// <paramref name="maximum"/> where the source is strictly above <paramref name="threshold"/>,
    /// zero elsewhere. On bytes the threshold is floored first, so 18.9 behaves as 18.
    /// </summary>
    public static void Binary(ImageBuffer source, ImageBuffer target, double threshold, double maximum)
    {
        if (source.Channels != 1) throw new NotSupportedException($"Threshold on {source.Type}.");
        ImageBuffer.RequireSameShape(source, target);
        int width = source.Width;
        if (source.Type == PixelType.U8C1)
        {
            int at = (int)Math.Floor(threshold);
            byte high = Saturate.ToByte(maximum);
            // Bounded to the byte range, the comparison keeps its meaning: below zero everything
            // is above it, at 255 nothing is.
            int bounded = Math.Clamp(at, -1, 255);
            ParallelRows.For(source.Height, width, y =>
            {
                byte* s = source.Row(y), d = target.Row(y);
                for (int x = 0; x < width; x++) d[x] = s[x] > bounded ? high : (byte)0;
            });
            return;
        }
        float t = (float)threshold, m = (float)maximum;
        ParallelRows.For(source.Height, width, y =>
        {
            float* s = source.Row<float>(y), d = target.Row<float>(y);
            for (int x = 0; x < width; x++) d[x] = s[x] > t ? m : 0f;
        });
    }

    /// <summary>The level that best splits the histogram of a byte image into two classes.</summary>
    /// <remarks>
    /// The between-class variance is accumulated in double, level by level, exactly as the replaced
    /// library did; ties keep the lowest level, and levels that leave one class all but empty are
    /// skipped. The answer is only ever used scaled by four fifths, so a level of difference here is
    /// a level of difference in what the mask takes — which is why this is the replaced code and not
    /// any of the equivalent formulations.
    /// </remarks>
    public static double Otsu(ImageBuffer source)
    {
        if (source.Type != PixelType.U8C1) throw new NotSupportedException($"Otsu on {source.Type}.");
        var histogram = stackalloc int[256];
        new Span<int>(histogram, 256).Clear();
        for (int y = 0; y < source.Height; y++)
        {
            byte* s = source.Row(y);
            for (int x = 0; x < source.Width; x++) histogram[s[x]]++;
        }
        const double epsilon = 1.1920928955078125e-07; // FLT_EPSILON
        double mu = 0, scale = 1.0 / ((double)source.Width * source.Height);
        for (int i = 0; i < 256; i++) mu += i * (double)histogram[i];
        mu *= scale;
        double mu1 = 0, q1 = 0, maxSigma = 0, maxValue = 0;
        for (int i = 0; i < 256; i++)
        {
            double p = histogram[i] * scale;
            mu1 *= q1;
            q1 += p;
            double q2 = 1.0 - q1;
            if (Math.Min(q1, q2) < epsilon || Math.Max(q1, q2) > 1.0 - epsilon) continue;
            mu1 = (mu1 + i * p) / q1;
            double mu2 = (mu - q1 * mu1) / q2;
            double sigma = q1 * q2 * (mu1 - mu2) * (mu1 - mu2);
            if (sigma > maxSigma)
            {
                maxSigma = sigma;
                maxValue = i;
            }
        }
        return maxValue;
    }
}
