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

    /// <summary>Black bars where the source glyphs would be, over whatever the caller painted.</summary>
    private static void Glyphs(Graphics g)
    {
        for (int x = 44; x < 236; x += 16)
            g.FillRectangle(Brushes.Black, x, 44, 9, 16);
    }

    /// <summary>A rendered surface: one flat colour, every neighbour identical.</summary>
    private static Bitmap Interface()
    {
        var frame = new Bitmap(320, 160);
        using var g = Graphics.FromImage(frame);
        g.Clear(Color.FromArgb(255, 0xE8, 0xE8, 0xE8));
        Glyphs(g);
        return frame;
    }

    /// <summary>
    /// A photographed surface: a colour ramp carrying sensor noise, so no two neighbours agree and
    /// no two colours account for the area. Both halves matter — the backdrop asks each in turn.
    /// </summary>
    private static Bitmap Photograph(int from = 60, int to = 200)
    {
        var frame = new Bitmap(320, 160);
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                int ramp = from + (to - from) * x / frame.Width;
                int noise = (x * 911 + y * 104729) % 17 - 8;
                byte level = (byte)Math.Clamp(ramp + noise, 0, 255);
                frame.SetPixel(x, y, Color.FromArgb(255,
                    level,
                    (byte)Math.Clamp(level + 12, 0, 255),
                    (byte)Math.Clamp(level - 18, 0, 255)));
            }
        }
        using var g = Graphics.FromImage(frame);
        Glyphs(g);
        return frame;
    }

    /// <summary>The plate for the block above, read back as pixels. Null when none was built.</summary>
    private static (BitmapSource Image, byte[] Pixels, int Stride, MediaColor Text)? Plate(
        Bitmap frame, MediaColor text, MediaColor? wash = null)
    {
        var backdrop = CaptureBubbleBackdrop.Create(frame, [Block()]);
        Assert.NotNull(backdrop);

        double feather = CaptureBubbleBackdrop.Feather(Line.Height);
        var area = new WpfRect(
            Line.X - feather, Line.Y - feather, Line.Width + feather * 2, Line.Height + feather * 2);
        if (backdrop!.Plate(area, wash ?? MediaColor.FromRgb(0x80, 0x90, 0x70), text, Line.Height, Line.Height) is not { } plate)
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
    /// Nothing beats a flat card on a surface that really is flat, so no plate is offered for one.
    /// </summary>
    [Fact]
    public void Plate_IsNullOnAFlatSurfaceSoTheCallerKeepsItsCard()
    {
        using var frame = Interface();

        Assert.Null(Plate(frame, MediaColor.FromRgb(0x20, 0x20, 0x20)));
    }

    /// <summary>
    /// The case that sent this back for a second attempt: a dark application page whose text sits
    /// among crisp buttons and pills. Blurring there smears the chrome around the bubble, and the
    /// flat card — which merges into the page — is the better answer.
    /// </summary>
    [Fact]
    public void Plate_IsNullOnASharpInterfaceEvenWhereTheBubbleCoversMoreThanOneColour()
    {
        using var frame = new Bitmap(320, 160);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(255, 0x1F, 0x22, 0x28));
            g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0x33, 0x38, 0x42)), 150, 38, 70, 28);
            g.FillRectangle(new SolidBrush(Color.FromArgb(255, 0x2A, 0x6E, 0x3F)), 32, 36, 26, 32);
            Glyphs(g);
        }

        Assert.Null(Plate(frame, MediaColor.FromRgb(0xE0, 0xE0, 0xE0)));
    }

    [Fact]
    public void Plate_IsBuiltForAPhotographedSurface()
    {
        using var frame = Photograph();

        Assert.NotNull(Plate(frame, MediaColor.FromRgb(0x10, 0x10, 0x10)));
    }

    /// <summary>
    /// The point of the whole thing: the source text is gone from the plate, so what remains can be
    /// blurred without leaving a legible smear.
    /// </summary>
    [Fact]
    public void Plate_HasNoTraceOfTheSourceText()
    {
        using var frame = Photograph();

        var plate = Plate(frame, MediaColor.FromRgb(0x10, 0x10, 0x10));
        Assert.NotNull(plate);

        var (image, pixels, stride, _) = plate!.Value;
        for (int y = image.PixelHeight / 3; y < image.PixelHeight * 2 / 3; y++)
        {
            for (int x = image.PixelWidth / 4; x < image.PixelWidth * 3 / 4; x++)
            {
                // The bars were pure black; nothing anywhere near that may survive.
                var colour = At(pixels, stride, x, y);
                Assert.True(colour.R > 0x30, $"({x},{y}) is {colour}");
            }
        }
    }

    /// <summary>
    /// A ramp across nearly the whole range, steeper than the other cases use, because the plate is
    /// blurred and washed before it is read back and only about a fifth of the surface's own
    /// gradient survives that. On the gentler default ramp what reaches the plate is twenty levels
    /// against a threshold of twenty, so the test would be measuring the property inside its own
    /// noise — and the whole line is erased and interpolated before any of this, which is one more
    /// reason not to ask a faint gradient to prove itself here.
    /// </summary>
    [Fact]
    public void Plate_KeepsTheGradientItWasCutFromRatherThanFlatteningIt()
    {
        using var frame = Photograph(8, 250);

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
        using var frame = Photograph();

        var plate = Plate(frame, MediaColor.FromRgb(0x10, 0x10, 0x10));
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
        using var frame = Photograph(from: 100, to: 150);
        var text = MediaColor.FromRgb(0x7A, 0x86, 0x68);

        var plate = Plate(frame, text, MediaColor.FromRgb(0x7D, 0x89, 0x6B));
        Assert.NotNull(plate);

        var (image, pixels, stride, answered) = plate!.Value;
        Assert.NotEqual(text, answered);
        for (int y = image.PixelHeight / 3; y < image.PixelHeight * 2 / 3; y++)
        {
            for (int x = image.PixelWidth / 4; x < image.PixelWidth * 3 / 4; x++)
            {
                var colour = At(pixels, stride, x, y);
                var surface = MediaColor.FromRgb(colour.R, colour.G, colour.B);
                // The plate's own target, above the flat card's: text on a picture crosses light
                // and dark parts of it, and scraping the card's minimum reads as washed out.
                Assert.True(OverlayTextColor.ContrastRatio(answered, surface) >= 4.5,
                    $"({x},{y}) is {colour}");
                // Still the range it was cut from, not washed away toward black or white.
                Assert.InRange(colour.R, 0x58, 0xA8);
            }
        }
    }

    /// <summary>
    /// A bubble at the edge of the selection reaches past the capture. The brush is stretched onto
    /// the element it fills, so a plate returned short would pull its opaque middle off the text.
    /// </summary>
    /// <summary>
    /// A plate pushed dark to carry light text used to stop at the ratio a flat card is held to,
    /// which leaves grey letters on a dark ground: legible by the number, hard to actually read.
    /// </summary>
    [Fact]
    public void Plate_TakesTheTextWellPastTheMinimumWhenMovingItIsFree()
    {
        using var frame = Photograph(from: 30, to: 90);
        var text = MediaColor.FromRgb(0x9A, 0xA2, 0x8E);

        var plate = Plate(frame, text, MediaColor.FromRgb(0x3C, 0x48, 0x2A));
        Assert.NotNull(plate);

        var (image, pixels, stride, answered) = plate!.Value;
        double worst = 21;
        for (int y = image.PixelHeight / 3; y < image.PixelHeight * 2 / 3; y++)
        {
            for (int x = image.PixelWidth / 4; x < image.PixelWidth * 3 / 4; x++)
            {
                var colour = At(pixels, stride, x, y);
                worst = Math.Min(worst, OverlayTextColor.ContrastRatio(
                    answered, MediaColor.FromRgb(colour.R, colour.G, colour.B)));
            }
        }

        Assert.True(worst >= 4.5, $"worst contrast was {worst:F2}");
    }

    /// <summary>
    /// A burned-in subtitle crossing a white shirt and the dark scene behind it. No one colour
    /// reads on both, so the plate is darkened — and the answer then has to be the white the
    /// subtitle was written in, not the grey that was the best compromise before it was darkened.
    /// </summary>
    [Fact]
    public void Plate_KeepsWhiteTextWhiteByDarkeningWhatItCrosses()
    {
        var frame = new Bitmap(320, 160);
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                int noise = (x * 911 + y * 104729) % 17 - 8;
                int level = Math.Clamp(238 - x * 204 / frame.Width + noise, 0, 255);
                frame.SetPixel(x, y, Color.FromArgb(255, level, level, Math.Clamp(level + 6, 0, 255)));
            }
        }
        using (var g = Graphics.FromImage(frame))
            Glyphs(g);

        using (frame)
        {
            var white = MediaColor.FromRgb(0xFF, 0xFF, 0xFF);
            var plate = Plate(frame, white, MediaColor.FromRgb(0x88, 0x88, 0x88));
            Assert.NotNull(plate);

            var (image, pixels, stride, answered) = plate!.Value;
            Assert.True(OverlayTextColor.RelativeLuminance(answered) > 0.6,
                $"the subtitle came back as {answered}, not a white");

            for (int y = image.PixelHeight / 3; y < image.PixelHeight * 2 / 3; y++)
            {
                for (int x = image.PixelWidth / 4; x < image.PixelWidth * 3 / 4; x++)
                {
                    var colour = At(pixels, stride, x, y);
                    Assert.True(
                        OverlayTextColor.ContrastRatio(answered,
                            MediaColor.FromRgb(colour.R, colour.G, colour.B)) >= 4.5,
                        $"({x},{y}) is {colour}");
                }
            }
        }
    }

    [Fact]
    public void Plate_IsTheFullSizeAskedForEvenWhereTheCaptureDoesNotReach()
    {
        using var frame = Photograph();
        var backdrop = CaptureBubbleBackdrop.Create(frame, [Block()]);

        var plate = backdrop!.Plate(new WpfRect(-12, -9, 120, 40),
            MediaColor.FromRgb(0x80, 0x90, 0x70), System.Windows.Media.Colors.Black, 20, 20);

        Assert.NotNull(plate);
        var image = (BitmapSource)plate!.Value.Brush.ImageSource;
        Assert.Equal(120, image.PixelWidth);
        Assert.Equal(40, image.PixelHeight);
    }

    /// <summary>
    /// The flat card takes the colour of what the bubble covers, not of the ring the capture was
    /// sampled from — so a translation that outgrows a coloured tag stops dragging the tag's
    /// colour across the page behind it.
    /// </summary>
    [Fact]
    public void Card_FollowsWhatTheBubbleCoversRatherThanWhatTheSourceLineSatOn()
    {
        using var frame = new Bitmap(320, 160);
        var tag = Color.FromArgb(255, 0xE8, 0x18, 0x5F);
        var page = Color.FromArgb(255, 0xF7, 0xF5, 0xF8);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(page);
            g.FillRectangle(new SolidBrush(tag), 36, 36, 90, 32);
            Glyphs(g);
        }

        var backdrop = CaptureBubbleBackdrop.Create(frame, [Block()]);
        var onTag = backdrop!.Card(new WpfRect(38, 38, 86, 28), MediaColor.FromRgb(0xFF, 0xFF, 0xFF));
        var outgrown = backdrop.Card(new WpfRect(38, 38, 250, 28), MediaColor.FromRgb(0xFF, 0xFF, 0xFF));

        Assert.NotNull(onTag);
        Assert.NotNull(outgrown);
        Assert.InRange(onTag!.Value.Background.R, 0xD8, 0xF8);
        Assert.InRange(onTag.Value.Background.G, 0x08, 0x38);
        // Grown well past the tag, the page is most of what is covered and the card follows it.
        Assert.InRange(outgrown!.Value.Background.G, 0xE0, 0xFF);
    }

    [Fact]
    public void Card_AnswersATextColourThatCanBeReadOnIt()
    {
        using var frame = Interface();
        var backdrop = CaptureBubbleBackdrop.Create(frame, [Block()]);

        var card = backdrop!.Card(Line, MediaColor.FromRgb(0xE4, 0xE4, 0xE4));

        Assert.NotNull(card);
        Assert.True(OverlayTextColor.ContrastRatio(card!.Value.Text, card.Value.Background)
            >= OverlayTextColor.MinimumContrast);
    }

    [Fact]
    public void Plate_IsNullForAnAreaOutsideTheCapture()
    {
        using var frame = Photograph();
        var backdrop = CaptureBubbleBackdrop.Create(frame, [Block()]);

        Assert.Null(backdrop!.Plate(
            new WpfRect(900, 900, 80, 30), System.Windows.Media.Colors.White,
            System.Windows.Media.Colors.Black, 20, 20));
    }
}
