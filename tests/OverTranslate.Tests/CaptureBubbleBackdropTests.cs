using System.Drawing;
using System.Windows.Media.Imaging;
using OverTranslate.Services;
using Xunit;
using MediaColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Rect;

namespace OverTranslate.Tests;

public class CaptureBubbleBackdropTests
{
    private static readonly WpfRect Line = new(40, 40, 200, 24);

    private static TranslatedBlock Block() =>
        new("source", "譯文", Line, [Line], null,
            MediaColor.FromRgb(0xFF, 0xFF, 0xFF), MediaColor.FromRgb(0, 0, 0));

    /// <summary>A line of black bars — stand-ins for glyphs — on whatever the caller paints.</summary>
    private static Bitmap Frame(Action<Graphics> paint)
    {
        var frame = new Bitmap(320, 160);
        using var g = Graphics.FromImage(frame);
        paint(g);
        for (int x = 44; x < 236; x += 16)
            g.FillRectangle(Brushes.Black, x, 44, 9, 16);
        return frame;
    }

    /// <summary>The plate for the block above, read back as pixels.</summary>
    /// <param name="wash">The colour sampled for this block, which in the app is the surface the
    /// plate is cut from — so unless a test is about disagreement, it matches what was painted.</param>
    private static (BitmapSource Image, byte[] Pixels, int Stride, MediaColor Text)? Plate(
        Bitmap frame, MediaColor text, MediaColor? wash = null)
    {
        var backdrop = CaptureBubbleBackdrop.Create(frame, [Block()]);
        Assert.NotNull(backdrop);

        double feather = CaptureBubbleBackdrop.Feather(Line.Height);
        var area = new WpfRect(
            Line.X - feather, Line.Y - feather, Line.Width + feather * 2, Line.Height + feather * 2);
        if (backdrop!.Plate(area, wash ?? MediaColor.FromRgb(0xE8, 0xE8, 0xE8), text, Line.Height) is not { } plate)
            return null;

        var image = (BitmapSource)plate.Brush.ImageSource;
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return (image, pixels, stride, plate.Text);
    }

    private static MediaColor At(byte[] pixels, int stride, int x, int y)
    {
        int i = y * stride + x * 4;
        return MediaColor.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    [Fact]
    public void Create_IsNullWithNoBlocksToRepair()
    {
        using var frame = new Bitmap(320, 160);

        Assert.Null(CaptureBubbleBackdrop.Create(frame, []));
    }

    [Fact]
    public void Feather_StaysOutsideTheBubbleEvenForTinyText()
    {
        Assert.True(CaptureBubbleBackdrop.Feather(1) >= 3);
        Assert.True(CaptureBubbleBackdrop.Feather(10_000) <= 12);
    }

    /// <summary>
    /// The point of the whole thing: the source text is gone from the plate, so what remains can be
    /// blurred without leaving a legible smear.
    /// </summary>
    [Fact]
    public void Plate_HasNoTraceOfTheSourceTextOnAFlatSurface()
    {
        using var frame = Frame(g => g.Clear(Color.FromArgb(255, 0xE8, 0xE8, 0xE8)));

        var plate = Plate(frame, MediaColor.FromRgb(0x20, 0x20, 0x20));
        Assert.NotNull(plate);

        var (image, pixels, stride, _) = plate!.Value;
        for (int y = image.PixelHeight / 3; y < image.PixelHeight * 2 / 3; y++)
        {
            for (int x = image.PixelWidth / 4; x < image.PixelWidth * 3 / 4; x++)
            {
                var colour = At(pixels, stride, x, y);
                // The bars were pure black; nothing anywhere near that may survive.
                Assert.True(colour.R > 0xB0, $"({x},{y}) is {colour}");
            }
        }
    }

    [Fact]
    public void Plate_KeepsTheGradientItWasCutFromRatherThanFlatteningIt()
    {
        using var frame = Frame(g =>
        {
            for (int x = 0; x < 320; x++)
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, 255 - x / 2, 200, 120 + x / 4)), x, 0, 1, 160);
        });

        var plate = Plate(frame, MediaColor.FromRgb(0x10, 0x10, 0x10));
        Assert.NotNull(plate);

        var (image, pixels, stride, _) = plate!.Value;
        int middle = image.PixelHeight / 2;
        var left = At(pixels, stride, image.PixelWidth / 8, middle);
        var right = At(pixels, stride, image.PixelWidth * 7 / 8, middle);

        Assert.True(Math.Abs(left.R - right.R) > 20, $"{left} vs {right} — the plate went flat");
    }

    [Fact]
    public void Plate_IsOpaqueInTheMiddleAndFadedAtTheEdge()
    {
        using var frame = Frame(g => g.Clear(Color.FromArgb(255, 0xE8, 0xE8, 0xE8)));

        var plate = Plate(frame, MediaColor.FromRgb(0x20, 0x20, 0x20));
        Assert.NotNull(plate);

        var (image, pixels, stride, _) = plate!.Value;
        Assert.Equal(255, At(pixels, stride, image.PixelWidth / 2, image.PixelHeight / 2).A);
        Assert.True(At(pixels, stride, 0, image.PixelHeight / 2).A < 40);
        Assert.True(At(pixels, stride, image.PixelWidth / 2, 0).A < 40);
    }

    /// <summary>
    /// A translation that could not be read on its plate gets a new colour, and the plate — the
    /// part that came from the picture — is left alone.
    /// </summary>
    [Fact]
    public void Plate_MovesTheTextRatherThanThePictureWhenItCannotBeRead()
    {
        using var frame = Frame(g => g.Clear(Color.FromArgb(255, 0x88, 0x88, 0x88)));
        var text = MediaColor.FromRgb(0x77, 0x77, 0x77);

        var plate = Plate(frame, text, MediaColor.FromRgb(0x88, 0x88, 0x88));
        Assert.NotNull(plate);

        var (image, pixels, stride, answered) = plate!.Value;
        Assert.NotEqual(text, answered);
        for (int y = image.PixelHeight / 3; y < image.PixelHeight * 2 / 3; y++)
        {
            for (int x = image.PixelWidth / 4; x < image.PixelWidth * 3 / 4; x++)
            {
                var colour = At(pixels, stride, x, y);
                var surface = MediaColor.FromRgb(colour.R, colour.G, colour.B);
                Assert.True(
                    OverlayTextColor.ContrastRatio(answered, surface) >= OverlayTextColor.MinimumContrast,
                    $"({x},{y}) is {colour}");
                // The grey it was painted on is still the grey it was painted on.
                Assert.InRange(colour.R, 0x80, 0x90);
            }
        }
    }

    /// <summary>
    /// A bubble at the edge of the selection reaches past the capture. The brush is stretched onto
    /// the element it fills, so a plate returned short would pull its opaque middle off the text.
    /// </summary>
    [Fact]
    public void Plate_IsTheFullSizeAskedForEvenWhereTheCaptureDoesNotReach()
    {
        using var frame = Frame(g => g.Clear(Color.FromArgb(255, 0xE8, 0xE8, 0xE8)));
        var backdrop = CaptureBubbleBackdrop.Create(frame, [Block()]);

        var plate = backdrop!.Plate(new WpfRect(-12, -9, 120, 40),
            System.Windows.Media.Colors.White, System.Windows.Media.Colors.Black, 20);

        Assert.NotNull(plate);
        var image = (BitmapSource)plate!.Value.Brush.ImageSource;
        Assert.Equal(120, image.PixelWidth);
        Assert.Equal(40, image.PixelHeight);
    }

    [Fact]
    public void Plate_IsNullForAnAreaOutsideTheCapture()
    {
        using var frame = Frame(g => g.Clear(Color.White));
        var backdrop = CaptureBubbleBackdrop.Create(frame, [Block()]);

        Assert.Null(backdrop!.Plate(
            new WpfRect(900, 900, 80, 30), System.Windows.Media.Colors.White,
            System.Windows.Media.Colors.Black, 20));
    }
}
