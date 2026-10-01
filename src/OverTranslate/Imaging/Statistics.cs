using System.Drawing;

namespace OverTranslate.Imaging;

/// <summary>Counts, extremes, averages and extents of single-channel images.</summary>
internal static unsafe class Statistics
{
    public static int CountNonZero(ImageBuffer image)
    {
        RequireSingle(image);
        if (OpenCvReference.Handles(OpenCvReference.Families.Statistics)) return OpenCvReference.CountNonZero(image);
        int count = 0;
        for (int y = 0; y < image.Height; y++)
        {
            if (image.Type == PixelType.U8C1)
            {
                var row = new ReadOnlySpan<byte>(image.Row(y), image.Width);
                count += row.Length - CountZeros(row);
            }
            else
            {
                float* row = image.Row<float>(y);
                for (int x = 0; x < image.Width; x++) if (row[x] != 0) count++;
            }
        }
        return count;
    }

    /// <summary>The smallest rectangle holding every non-zero pixel; empty when there is none.</summary>
    public static Rectangle BoundingRect(ImageBuffer mask)
    {
        if (mask.Type != PixelType.U8C1) throw new NotSupportedException($"Bounds of {mask.Type}.");
        if (OpenCvReference.Handles(OpenCvReference.Families.Statistics)) return OpenCvReference.BoundingRect(mask);
        int left = int.MaxValue, top = -1, right = -1, bottom = -1;
        for (int y = 0; y < mask.Height; y++)
        {
            var row = new ReadOnlySpan<byte>(mask.Row(y), mask.Width);
            int first = row.IndexOfAnyExcept((byte)0);
            if (first < 0) continue;
            int last = row.LastIndexOfAnyExcept((byte)0);
            if (top < 0) top = y;
            bottom = y;
            left = Math.Min(left, first);
            right = Math.Max(right, last);
        }
        return top < 0 ? Rectangle.Empty : new Rectangle(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>The smallest and largest value, over the pixels <paramref name="mask"/> lets through if given; zeros when none is.</summary>
    public static (double Min, double Max) MinMax(ImageBuffer image, ImageBuffer? mask = null)
    {
        RequireSingle(image);
        if (mask is not null) ImageBuffer.RequireMask(mask, image.Size);
        if (OpenCvReference.Handles(OpenCvReference.Families.Statistics)) return OpenCvReference.MinMax(image, mask);
        double least = double.MaxValue, most = double.MinValue;
        bool any = false;
        for (int y = 0; y < image.Height; y++)
        {
            byte* where = mask is null ? null : mask.Row(y);
            if (image.Type == PixelType.U8C1)
            {
                byte* row = image.Row(y);
                for (int x = 0; x < image.Width; x++)
                {
                    if (where != null && where[x] == 0) continue;
                    any = true;
                    if (row[x] < least) least = row[x];
                    if (row[x] > most) most = row[x];
                }
            }
            else
            {
                float* row = image.Row<float>(y);
                for (int x = 0; x < image.Width; x++)
                {
                    if (where != null && where[x] == 0) continue;
                    any = true;
                    if (row[x] < least) least = row[x];
                    if (row[x] > most) most = row[x];
                }
            }
        }
        return any ? (least, most) : (0, 0);
    }

    /// <summary>The average of a byte image, over the pixels <paramref name="mask"/> lets through if given; zero when none is.</summary>
    /// <remarks>
    /// The sum is exact and multiplied by the reciprocal of the count rather than divided by it —
    /// the replaced library's arithmetic, which the callers' thresholds sit on: 250 pixels at 250
    /// out of 49 counted do not average to exactly 250 that way.
    /// </remarks>
    public static double Mean(ImageBuffer image, ImageBuffer? mask = null)
    {
        if (image.Type != PixelType.U8C1) throw new NotSupportedException($"Mean of {image.Type}.");
        if (mask is not null) ImageBuffer.RequireMask(mask, image.Size);
        if (OpenCvReference.Handles(OpenCvReference.Families.Statistics)) return OpenCvReference.Mean(image, mask);
        long sum = 0, count = 0;
        for (int y = 0; y < image.Height; y++)
        {
            byte* row = image.Row(y);
            if (mask is null)
            {
                for (int x = 0; x < image.Width; x++) sum += row[x];
                count += image.Width;
            }
            else
            {
                byte* where = mask.Row(y);
                for (int x = 0; x < image.Width; x++)
                    if (where[x] != 0)
                    {
                        sum += row[x];
                        count++;
                    }
            }
        }
        return sum * (count != 0 ? 1.0 / count : 0);
    }

    private static int CountZeros(ReadOnlySpan<byte> row)
    {
        int zeros = 0, at = 0;
        while (at < row.Length)
        {
            int next = row[at..].IndexOf((byte)0);
            if (next < 0) break;
            at += next;
            int run = row[at..].IndexOfAnyExcept((byte)0);
            int length = run < 0 ? row.Length - at : run;
            zeros += length;
            at += length;
        }
        return zeros;
    }

    private static void RequireSingle(ImageBuffer image)
    {
        if (image.Channels != 1) throw new NotSupportedException($"Expected one channel, got {image.Type}.");
    }
}
