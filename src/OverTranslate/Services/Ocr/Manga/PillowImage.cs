using System.Drawing;
using System.Drawing.Imaging;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// The two picture operations the manga models were measured behind, done the way Pillow does them.
/// </summary>
/// <remarks>
/// <para>Every number the models were chosen on — the whole-sentence counts, the batch cap, the long
/// block threshold — came out of a Python bench that fed them through Pillow: <c>convert("L")</c> and
/// <c>resize(..., BILINEAR)</c>. Pillow's bilinear is not GDI+'s. When it shrinks it widens the filter
/// by the shrink factor, so a 1800px page going into the detector's 640 is averaged over about three
/// source pixels per output pixel; GDI+ samples two and skips the rest. A page is shrunk by that much
/// on every read, so matching it is what makes the bench a measurement of this code.</para>
///
/// <para>A port of <c>Resample.c</c> (8 bits per channel), fixed-point coefficients included, and of
/// the ITU-R 601-2 luma transform in <c>Convert.c</c>.</para>
/// </remarks>
internal static class PillowImage
{
    // Pillow's PRECISION_BITS for 8-bit resampling: 32 - 8 - 2.
    private const int PrecisionBits = 22;

    /// <summary>The bitmap as tightly packed RGB bytes, three per pixel, row after row.</summary>
    internal static byte[] Rgb(Bitmap bitmap, Rectangle area)
    {
        var rgb = new byte[area.Width * area.Height * 3];
        var data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < area.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                    int o = y * area.Width * 3;
                    for (int x = 0; x < area.Width; x++)
                    {
                        // Stored BGR.
                        rgb[o + x * 3] = row[x * 3 + 2];
                        rgb[o + x * 3 + 1] = row[x * 3 + 1];
                        rgb[o + x * 3 + 2] = row[x * 3];
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return rgb;
    }

    /// <summary>Pillow's <c>convert("L")</c>: L = R·299/1000 + G·587/1000 + B·114/1000, rounded.</summary>
    internal static byte[] Luma(byte[] rgb)
    {
        var luma = new byte[rgb.Length / 3];
        for (int i = 0; i < luma.Length; i++)
            luma[i] = (byte)((rgb[i * 3] * 19595 + rgb[i * 3 + 1] * 38470 + rgb[i * 3 + 2] * 7471 + 0x8000) >> 16);
        return luma;
    }

    /// <summary>Pillow's <c>resize((width, height), BILINEAR)</c> over interleaved 8-bit channels.</summary>
    internal static byte[] Resize(byte[] pixels, int width, int height, int channels, int outWidth, int outHeight)
    {
        // Horizontal first, then vertical — the order Pillow runs them in when both are needed, which
        // matters because the intermediate is rounded back to 8 bits.
        var (xBounds, xWeights, xSize) = Coefficients(width, outWidth);
        var (yBounds, yWeights, ySize) = Coefficients(height, outHeight);

        var across = width == outWidth
            ? pixels
            : Horizontal(pixels, width, height, channels, outWidth, xBounds, xWeights, xSize);
        return height == outHeight
            ? across
            : Vertical(across, outWidth, height, channels, outHeight, yBounds, yWeights, ySize);
    }

    private static byte[] Horizontal(
        byte[] src, int width, int height, int channels, int outWidth, int[] bounds, int[] weights, int kSize)
    {
        var dst = new byte[outWidth * height * channels];
        for (int y = 0; y < height; y++)
        {
            int rowIn = y * width * channels, rowOut = y * outWidth * channels;
            for (int x = 0; x < outWidth; x++)
            {
                int start = bounds[x * 2], count = bounds[x * 2 + 1], k = x * kSize;
                for (int c = 0; c < channels; c++)
                {
                    int sum = 1 << (PrecisionBits - 1);
                    for (int i = 0; i < count; i++)
                        sum += src[rowIn + (start + i) * channels + c] * weights[k + i];
                    dst[rowOut + x * channels + c] = Clip(sum);
                }
            }
        }

        return dst;
    }

    private static byte[] Vertical(
        byte[] src, int width, int height, int channels, int outHeight, int[] bounds, int[] weights, int kSize)
    {
        int stride = width * channels;
        var dst = new byte[stride * outHeight];
        for (int y = 0; y < outHeight; y++)
        {
            int start = bounds[y * 2], count = bounds[y * 2 + 1], k = y * kSize;
            for (int x = 0; x < stride; x++)
            {
                int sum = 1 << (PrecisionBits - 1);
                for (int i = 0; i < count; i++)
                    sum += src[(start + i) * stride + x] * weights[k + i];
                dst[y * stride + x] = Clip(sum);
            }
        }

        return dst;
    }

    private static byte Clip(int sum)
    {
        int value = sum >> PrecisionBits;
        return (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
    }

    // precompute_coeffs + normalize_coeffs_8bpc, for the bilinear (triangle, support 1) filter.
    private static (int[] Bounds, int[] Weights, int KSize) Coefficients(int inSize, int outSize)
    {
        double scale = (double)inSize / outSize;
        double filterScale = Math.Max(scale, 1.0);
        double support = 1.0 * filterScale;
        int kSize = (int)Math.Ceiling(support) * 2 + 1;

        var bounds = new int[outSize * 2];
        var weights = new int[outSize * kSize];
        var k = new double[kSize];
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale;
            double inverse = 1.0 / filterScale;
            int xmin = Math.Max((int)(center - support + 0.5), 0);
            int xmax = Math.Min((int)(center + support + 0.5), inSize) - xmin;

            double total = 0;
            for (int x = 0; x < xmax; x++)
            {
                double w = Math.Abs((x + xmin - center + 0.5) * inverse);
                w = w < 1.0 ? 1.0 - w : 0.0;
                k[x] = w;
                total += w;
            }

            for (int x = 0; x < xmax; x++)
            {
                double w = total != 0 ? k[x] / total : 0;
                weights[xx * kSize + x] = (int)(w < 0
                    ? -0.5 + w * (1 << PrecisionBits)
                    : 0.5 + w * (1 << PrecisionBits));
            }

            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }

        return (bounds, weights, kSize);
    }
}
