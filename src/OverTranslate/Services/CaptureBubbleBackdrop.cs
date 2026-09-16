using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OverTranslate.Services.Realtime;
using CvRect = OpenCvSharp.Rect;
using CvSize = OpenCvSharp.Size;
using MediaColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Rect;

namespace OverTranslate.Services;

/// <summary>What to paint one bubble with, and what colour its text has to be on that.</summary>
/// <remarks>
/// The two travel together because they are decided together. The colour sampled when the capture
/// was read was chosen against the ring around the source line, and the plate is the picture the
/// bubble actually covers — over a wide bubble on a busy scene those are not the same surface, and
/// text picked for one can be unreadable on the other.
/// </remarks>
internal readonly record struct CaptureBubblePlate(ImageBrush Brush, MediaColor Text);

/// <summary>
/// What a capture bubble is painted with: the picture that was under it, with the source text
/// repaired away, softened, and faded out at the edges so the bubble has no findable border.
/// </summary>
/// <remarks>
/// <para>A bubble used to be one flat colour. On a flat surface that is exactly right and this
/// produces the same thing — blurring a flat colour returns it — but over a photograph, a game
/// scene or a panel with a gradient it is a card sitting on the picture, and a long paragraph is a
/// long card. What is drawn instead is the picture itself: the source glyphs are repaired away
/// (the same repair the realtime overlay runs, so there is one of it rather than two), the result
/// is blurred enough that no detail competes with the translation, and only then is a little of
/// the sampled background colour washed over it.</para>
///
/// <para>The repair has to happen before the blur, not instead of it: blurring text that is still
/// there leaves a legible smear, which reads worse than the flat card did.</para>
///
/// <para>Measured over six real captures, the edge fade is what decides whether this works.
/// Identical blur and wash with a hard edge still shows a rectangle on a game scene; with the
/// fade the same bubble cannot be found. It is therefore not optional, and it is why a plate is
/// larger than the bubble it backs — see <see cref="Feather"/>.</para>
/// </remarks>
internal sealed class CaptureBubbleBackdrop
{
    /// <summary>Blur radius as a fraction of the source glyph height.</summary>
    /// <remarks>
    /// Tied to the text size rather than fixed because that is what "enough to stop competing with
    /// the glyphs" scales with. 0.15 and 0.30 were hard to tell apart on real captures and 0.50
    /// started to look soft; this is the middle of what worked.
    /// </remarks>
    private const double BlurFactor = 0.30;

    /// <summary>Width of the alpha ramp at the plate's edge, as a fraction of the glyph height.</summary>
    private const double FeatherFactor = 0.35;
    private const double FeatherMinimum = 3;
    private const double FeatherMaximum = 12;

    /// <summary>
    /// How much of the sampled background colour is washed over the blur. Small, and deliberately
    /// so: this settles the picture down, it does not buy legibility. It cannot — the wash colour
    /// is the background the text colour was already chosen against, so washing toward it moves
    /// the plate no further from the text. Legibility is <see cref="Lift"/>'s job.
    /// </summary>
    private const double WashOpacity = 0.25;

    private readonly byte[] _bgr;
    private readonly int _width;
    private readonly int _height;

    private CaptureBubbleBackdrop(byte[] bgr, int width, int height)
    {
        _bgr = bgr;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// How far outside the bubble its plate reaches, in captured pixels. The ramp has to live
    /// outside the bubble and not inside it: inside, its transparent half would fall across the
    /// ends of the source line and let the original text show through the translation covering it.
    /// </summary>
    public static double Feather(double glyphHeight) =>
        Math.Clamp(glyphHeight * FeatherFactor, FeatherMinimum, FeatherMaximum);

    /// <summary>
    /// Repairs the capture once, for every bubble that will be drawn over it. Null when there is
    /// nothing to repair or the repair failed, and the caller then paints flat colour as before.
    /// </summary>
    public static CaptureBubbleBackdrop? Create(
        Bitmap frame, IReadOnlyList<TranslatedBlock> blocks, CancellationToken token = default)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || blocks.Count == 0) return null;

        try
        {
            using var repaired = RealtimeCpuBackground.Repair(frame, blocks, token);
            var bgr = new byte[repaired.Width * repaired.Height * 3];
            var data = repaired.LockBits(new Rectangle(0, 0, repaired.Width, repaired.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                int stride = repaired.Width * 3;
                for (int y = 0; y < repaired.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bgr, y * stride, stride);
            }
            finally { repaired.UnlockBits(data); }

            return new CaptureBubbleBackdrop(bgr, repaired.Width, repaired.Height);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Never the reason a translation fails to appear: the flat colour is still a bubble.
            return null;
        }
    }

    /// <summary>
    /// The brush for one bubble. <paramref name="area"/> is the bubble already grown by
    /// <see cref="Feather"/> on every side, in the captured frame's own pixels.
    /// </summary>
    /// <param name="text">
    /// The colour the capture was read as having been written in. It is a starting point: what
    /// comes back may be lighter or darker, because it was chosen against the ring around the
    /// source line and this plate is a different surface.
    /// </param>
    public CaptureBubblePlate? Plate(WpfRect area, MediaColor wash, MediaColor text, double glyphHeight)
    {
        var requested = Round(area);
        if (requested.Width < 2 || requested.Height < 2) return null;
        var rect = Clip(requested);
        if (rect.Width < 2 || rect.Height < 2) return null;

        double sigma = Math.Clamp(glyphHeight * BlurFactor, 1, 16);
        int pad = (int)Math.Ceiling(sigma * 3);
        var work = Clip(new CvRect(rect.X - pad, rect.Y - pad, rect.Width + pad * 2, rect.Height + pad * 2));

        try
        {
            using var source = Read(work);
            using var blurred = new Mat();
            Cv2.GaussianBlur(source, blurred, new CvSize(0, 0), sigma, borderType: BorderTypes.Replicate);

            // Padded back out to the size that was asked for. A bubble at the edge of the
            // selection has part of its plate outside the capture, and the brush is stretched onto
            // the element it fills: an image returned short would stretch the opaque middle off
            // the text and squash the ramp that is the whole point of the extra margin.
            using var cropped = new Mat(blurred,
                new CvRect(rect.X - work.X, rect.Y - work.Y, rect.Width, rect.Height));
            using var plate = new Mat();
            Cv2.CopyMakeBorder(cropped, plate,
                rect.Y - requested.Y, requested.Bottom - rect.Bottom,
                rect.X - requested.X, requested.Right - rect.Right, BorderTypes.Replicate);
            if (WashOpacity > 0)
            {
                using var tint = new Mat(plate.Size(), MatType.CV_8UC3, new Scalar(wash.B, wash.G, wash.R));
                Cv2.AddWeighted(plate, 1 - WashOpacity, tint, WashOpacity, 0, plate);
            }

            // Move the text before moving the plate. Lightening or darkening one colour costs
            // the picture nothing; washing the whole plate toward black or white costs exactly
            // what this class exists to preserve, so it is the fallback and not the first answer.
            var palette = Palette(plate, (int)Math.Round(Feather(glyphHeight)));
            var legible = Legible(palette, text);
            double lift = Clears(palette, legible, Colors.Black, 0) ? 0 : Lift(palette, legible);
            if (lift > 0)
            {
                var target = OverlayTextColor.ContrastRatio(legible, Colors.White) >=
                    OverlayTextColor.ContrastRatio(legible, Colors.Black) ? Colors.White : Colors.Black;
                using var solid = new Mat(plate.Size(), MatType.CV_8UC3, new Scalar(target.B, target.G, target.R));
                Cv2.AddWeighted(plate, 1 - lift, solid, lift, 0, plate);
            }

            var brush = new ImageBrush(Fade(plate, Feather(glyphHeight))) { Stretch = Stretch.Fill };
            brush.Freeze();
            return new CaptureBubblePlate(brush, legible);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The colours the plate is made of, shrunk down. The plate has already been blurred, so it
    /// carries no detail for the shrink to lose, and reading every pixel of every bubble — several
    /// times over, for the searches below — costs far more than the answer is worth.
    /// </summary>
    /// <param name="inset">
    /// The feather ring, left out. Nothing is written on it — the text element keeps the bubble's
    /// own size while the plate is grown by exactly this much — so a dark corner out there has no
    /// business darkening the whole plate to stay legible under text that is not on it.
    /// </param>
    private static MediaColor[] Palette(Mat plate, int inset)
    {
        int margin = Math.Max(0, Math.Min(inset, Math.Min(plate.Width, plate.Height) / 2 - 1));
        using var written = margin > 0
            ? new Mat(plate, new CvRect(margin, margin, plate.Width - margin * 2, plate.Height - margin * 2))
            : plate.Clone();
        using var sample = new Mat();
        Cv2.Resize(written, sample, new CvSize(
            Math.Clamp(written.Width / 8, 8, 48), Math.Clamp(written.Height / 8, 4, 16)),
            interpolation: InterpolationFlags.Area);

        var colours = new MediaColor[sample.Rows * sample.Cols];
        var row = new byte[sample.Cols * 3];
        for (int y = 0; y < sample.Rows; y++)
        {
            Marshal.Copy(sample.Ptr(y), row, 0, row.Length);
            for (int x = 0; x < sample.Cols; x++)
                colours[y * sample.Cols + x] = MediaColor.FromRgb(row[x * 3 + 2], row[x * 3 + 1], row[x * 3]);
        }
        return colours;
    }

    /// <summary>
    /// The nearest colour to <paramref name="text"/> that can be read everywhere on this plate.
    /// </summary>
    /// <remarks>
    /// Lightness only — <see cref="OverlayTextColor.EnsureContrast"/> keeps the hue, which is the
    /// part that came from the original and the part worth keeping. It answers for one background
    /// at a time, so it is pointed at whichever part of the plate the text reads worst on and run
    /// again if fixing that one broke another; a plate holding both very dark and very light areas
    /// has no answer at all, and then the plate itself has to give way.
    /// </remarks>
    private static MediaColor Legible(MediaColor[] palette, MediaColor text)
    {
        var best = text;
        for (int round = 0; round < 4; round++)
        {
            if (Clears(palette, best, Colors.Black, 0)) return best;
            var worst = palette.MinBy(colour => OverlayTextColor.ContrastRatio(best, colour));
            var next = OverlayTextColor.EnsureContrast(best, worst, OverlayTextColor.MinimumContrast);
            if (next == best) break;
            best = next;
        }
        return best;
    }

    /// <summary>
    /// How far the plate has to move toward black or white before every part of it clears the
    /// minimum contrast against the text. Only reached when no text colour could.
    /// </summary>
    private static double Lift(MediaColor[] palette, MediaColor text)
    {
        var target = OverlayTextColor.ContrastRatio(text, Colors.White) >=
            OverlayTextColor.ContrastRatio(text, Colors.Black) ? Colors.White : Colors.Black;

        double low = 0, high = 1;
        for (int i = 0; i < 10; i++)
        {
            double middle = (low + high) / 2;
            if (Clears(palette, text, target, middle)) high = middle; else low = middle;
        }
        return high;
    }

    private static bool Clears(MediaColor[] palette, MediaColor text, MediaColor target, double amount) =>
        palette.All(colour => OverlayTextColor.ContrastRatio(text, Mix(colour, target, amount))
            >= OverlayTextColor.MinimumContrast);

    private static MediaColor Mix(MediaColor from, MediaColor to, double amount) => MediaColor.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    /// <summary>The plate as a bitmap whose outermost <paramref name="feather"/> pixels fade out.</summary>
    private static BitmapSource Fade(Mat plate, double feather)
    {
        int width = plate.Width;
        int height = plate.Height;
        int ramp = (int)Math.Round(feather);
        var pixels = new byte[width * height * 4];
        var row = new byte[width * 3];

        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(plate.Ptr(y), row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i] = row[x * 3];
                pixels[i + 1] = row[x * 3 + 1];
                pixels[i + 2] = row[x * 3 + 2];
                pixels[i + 3] = Alpha(x, y, width, height, ramp);
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Smoothstep rather than a straight ramp: a linear fade leaves a visible crease where it
    /// meets the opaque middle, which is the very line this exists to remove.
    /// </summary>
    private static byte Alpha(int x, int y, int width, int height, int ramp)
    {
        if (ramp <= 0) return 255;
        int edge = Math.Min(Math.Min(x, width - 1 - x), Math.Min(y, height - 1 - y));
        if (edge >= ramp) return 255;
        double t = (edge + 0.5) / ramp;
        return (byte)Math.Round(255 * t * t * (3 - 2 * t));
    }

    /// <summary>The requested area on the pixel grid, whether or not the capture reaches it.</summary>
    private static CvRect Round(WpfRect area)
    {
        if (!double.IsFinite(area.X + area.Y + area.Width + area.Height)
            || Math.Abs(area.X) > 1e6 || Math.Abs(area.Y) > 1e6
            || area.Width > 1e6 || area.Height > 1e6) return default;
        int left = (int)Math.Floor(area.X);
        int top = (int)Math.Floor(area.Y);
        return new CvRect(left, top,
            (int)Math.Ceiling(area.Right) - left, (int)Math.Ceiling(area.Bottom) - top);
    }

    private CvRect Clip(CvRect area)
    {
        int left = Math.Clamp(area.X, 0, _width);
        int top = Math.Clamp(area.Y, 0, _height);
        return new CvRect(left, top,
            Math.Clamp(area.Right, left, _width) - left,
            Math.Clamp(area.Bottom, top, _height) - top);
    }

    private Mat Read(CvRect area)
    {
        var mat = new Mat(area.Height, area.Width, MatType.CV_8UC3);
        try
        {
            int stride = _width * 3;
            for (int y = 0; y < area.Height; y++)
                Marshal.Copy(_bgr, (area.Y + y) * stride + area.X * 3, mat.Ptr(y), area.Width * 3);
            return mat;
        }
        catch { mat.Dispose(); throw; }
    }
}
