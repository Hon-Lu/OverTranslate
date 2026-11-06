using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The screenshot's DIB, which is laid out by hand, and how long a copy waits for a locked
/// clipboard. Writing the real clipboard is left to the live check: a test that did would overwrite
/// whatever the person running the suite had copied.
/// </summary>
public class ClipboardWriterTests
{
    [Fact]
    public void Dib_is_a_32_bit_bottom_up_bitmap_with_its_rows_reversed()
    {
        // 2x2, Bgra32: top row red, green; bottom row blue, white.
        var pixels = new byte[]
        {
            0, 0, 255, 255,   0, 255, 0, 255,
            255, 0, 0, 255,   255, 255, 255, 255,
        };
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);

        var dib = ClipboardWriter.Dib(image);

        Assert.Equal(40 + 16, dib.Length);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));
        Assert.Equal(2, BitConverter.ToInt32(dib, 4));
        Assert.Equal(2, BitConverter.ToInt32(dib, 8));      // positive: bottom-up
        Assert.Equal(1, BitConverter.ToInt16(dib, 12));
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));
        Assert.Equal(0, BitConverter.ToInt32(dib, 16));     // BI_RGB
        Assert.Equal(16, BitConverter.ToInt32(dib, 20));

        // First stored row is the image's bottom row.
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 255, 255, 255 }, dib[40..48]);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, dib[48..56]);
    }

    [Fact]
    public void Dib_makes_every_pixel_opaque()
    {
        // Bgr32 leaves the fourth byte to whatever was there.
        var pixels = new byte[] { 10, 20, 30, 0, 40, 50, 60, 7 };
        var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgr32, null, pixels, 8);

        var dib = ClipboardWriter.Dib(image);

        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, dib[40..48]);
    }

    [Fact]
    public void Dib_converts_other_formats_to_32_bits()
    {
        var pixels = new byte[] { 10, 20, 30, 40, 50, 60 };
        var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Rgb24, null, pixels, 6);

        var dib = ClipboardWriter.Dib(image);

        Assert.Equal(32, BitConverter.ToInt16(dib, 14));
        Assert.Equal(new byte[] { 30, 20, 10, 255, 60, 50, 40, 255 }, dib[40..48]);
    }

    [Fact]
    public void A_locked_clipboard_is_waited_for_about_a_second_with_doubling_waits()
    {
        var delays = ClipboardWriter.RetryDelays;
        for (int i = 1; i < delays.Length; i++)
            Assert.Equal(delays[i - 1] * 2, delays[i]);
        Assert.InRange(delays.Sum(), 1000, 1500);
    }
}
