using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GdiColor = System.Drawing.Color;

namespace OverTranslate.Services;

/// <summary>
/// One rectangle of a bitmap's pixels, read out once so a sampler can index it in the bitmap's
/// own coordinates.
/// </summary>
/// <remarks>
/// The alternative is <c>Bitmap.GetPixel</c>, which locks the bitmap, reads four bytes and
/// unlocks it again on every call. Sampling one subtitle line asks for around fifteen thousand
/// pixels, so this is the difference between one lock and fifteen thousand.
/// </remarks>
internal readonly struct PixelWindow(byte[] pixels, int stride, Rectangle area)
{
    public static PixelWindow? Read(Bitmap frame, Rectangle area)
    {
        BitmapData? data = null;
        try
        {
            data = frame.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            int stride = area.Width * 4;
            var pixels = new byte[stride * area.Height];
            for (int y = 0; y < area.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);

            return new PixelWindow(pixels, stride, area);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (data is not null) frame.UnlockBits(data);
        }
    }

    /// <param name="x">In the source bitmap's coordinates, not the window's.</param>
    public GdiColor At(int x, int y)
    {
        int i = (y - area.Top) * stride + (x - area.Left) * 4;
        return GdiColor.FromArgb(255, pixels[i + 2], pixels[i + 1], pixels[i]);
    }
}
