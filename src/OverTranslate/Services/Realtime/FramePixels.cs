using System.Drawing;
using System.Drawing.Imaging;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// A captured frame held open for reading, pixel by pixel, for as long as this is not disposed.
/// </summary>
/// <remarks>
/// A bitmap can be locked by one reader at a time, so this is taken, used and let go within one
/// step of a poll — before anything else in that poll looks at the frame.
/// </remarks>
internal sealed unsafe class FramePixels : IDisposable
{
    private readonly Bitmap _bitmap;
    private readonly BitmapData _data;

    private FramePixels(Bitmap bitmap)
    {
        _bitmap = bitmap;
        _data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    }

    public static FramePixels Lock(Bitmap bitmap) => new(bitmap);

    public int Width => _data.Width;

    public int Height => _data.Height;

    /// <summary>One row of pixels, each blue, green, red and alpha from the low byte up.</summary>
    public uint* Row(int y) => (uint*)((byte*)_data.Scan0 + (long)y * _data.Stride);

    /// <summary>How bright one pixel is, 0 to 255.</summary>
    public int Brightness(int x, int y)
    {
        var pixel = Row(y)[x];
        return (int)(((pixel >> 16) & 0xFF) * 77 + ((pixel >> 8) & 0xFF) * 150 + (pixel & 0xFF) * 29) >> 8;
    }

    public void Dispose() => _bitmap.UnlockBits(_data);
}
