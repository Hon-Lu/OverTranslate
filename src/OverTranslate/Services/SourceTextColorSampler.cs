using System.Drawing;
using MediaColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Rect;

namespace OverTranslate.Services;

/// <summary>What one text box on screen was drawn in: the colour around it, and its glyphs' colour.</summary>
/// <param name="Text">Null when no pixel inside the box stood far enough from the background.</param>
internal readonly record struct SourceTextColor(MediaColor Background, MediaColor? Text);

/// <summary>
/// Reads the colours a line of source text was drawn in, for both places that draw a translation
/// back over it.
/// </summary>
/// <remarks>
/// The capture overlay and realtime subtitles used to carry a copy each of the same measurement,
/// with thresholds that had drifted apart for no recorded reason. The measurement lives here; what to
/// do with an unconvincing result stays with each caller, because they differ for real reasons — the
/// capture overlay has no colour of the user's to fall back on, and realtime text is not necessarily
/// drawn over the background sampled here.
/// </remarks>
internal static class SourceTextColorSampler
{
    /// <summary>
    /// Samples the background around <paramref name="bounds"/> and the dominant glyph colour inside it.
    /// Null when the box has no pixels in the frame.
    /// </summary>
    public static SourceTextColor? Sample(Bitmap frame, WpfRect bounds)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        // Truncated, not floored and ceilinged. Rounding the far edges outward reads one more column
        // and row, and on small pill-shaped labels with a border that pixel is enough to change which
        // colour wins: over 310 real screens it changed 29 capture blocks, eight of them clearly for the
        // worse (a white-on-orange tag drawn brown-on-cyan), for 0.7 points less 1px-shift instability.
        var inner = Rectangle.FromLTRB(
            Math.Clamp((int)bounds.X, 0, frame.Width),
            Math.Clamp((int)bounds.Y, 0, frame.Height),
            Math.Clamp((int)(bounds.X + bounds.Width), 0, frame.Width),
            Math.Clamp((int)(bounds.Y + bounds.Height), 0, frame.Height));
        if (inner.Width <= 0 || inner.Height <= 0)
            return null;

        // The ring the background is read from and the box the glyphs are read from, in one window,
        // so the bitmap is locked once for the whole decision.
        int padX = Math.Max(4, (int)Math.Round(bounds.Height * 0.35));
        int padY = Math.Max(3, (int)Math.Round(bounds.Height * 0.28));
        var outer = Rectangle.FromLTRB(
            Math.Clamp((int)bounds.X - padX, 0, frame.Width),
            Math.Clamp((int)bounds.Y - padY, 0, frame.Height),
            Math.Clamp((int)(bounds.X + bounds.Width) + padX, 0, frame.Width),
            Math.Clamp((int)(bounds.Y + bounds.Height) + padY, 0, frame.Height));

        if (PixelWindow.Read(frame, outer) is not { } window)
            return null;

        var background = DominantBackground(window, outer, inner);
        return new SourceTextColor(background, DominantGlyphColor(window, inner, background));
    }

    /// <summary>
    /// The colours the capture overlay draws a block in: the sampled background, and a text colour
    /// that is tuned toward the source and guaranteed legible on it.
    /// </summary>
    public static (MediaColor Background, MediaColor Text) ForCaptureOverlay(Bitmap frame, WpfRect bounds)
    {
        var black = MediaColor.FromRgb(0, 0, 0);
        var white = MediaColor.FromRgb(255, 255, 255);

        if (Sample(frame, bounds) is not { } sample)
            return (white, black);

        var background = sample.Background;
        if (sample.Text is not { } text)
            return (background, OverlayTextColor.PerceivedLuminance(background) > 0.5 ? black : white);

        return (background, OverlayTextColor.EnsureContrast(
            OverlayTextColor.Tune(text, background), background, OverlayTextColor.MinimumContrast));
    }

    /// <summary>
    /// The most common colour in a ring around the text. The most common rather than the average,
    /// so a box that no longer fully encloses its glyphs still reads the page and not the glyphs.
    /// </summary>
    /// <remarks>
    /// Every row, and every row inside the box below too. The ring above and below a small box is only
    /// a few rows deep, and reading every other one let a one-pixel shift of the same box land on a
    /// different winner: across 1,225 capture blocks and 1,354 realtime lines, skipping rows here or
    /// in the glyph pass roughly doubled how often a 1px shift changed the colour by more than dE 10.
    /// </remarks>
    private static MediaColor DominantBackground(PixelWindow window, Rectangle outer, Rectangle inner)
    {
        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
        for (int y = outer.Top; y < outer.Bottom; y++)
        {
            for (int x = outer.Left; x < outer.Right; x += 2)
            {
                bool insideText = x >= inner.Left && x < inner.Right && y >= inner.Top && y < inner.Bottom;
                if (insideText) continue;

                var c = window.At(x, y);
                int key = ((c.R >> 4) << 8) | ((c.G >> 4) << 4) | (c.B >> 4);
                var bucket = buckets.GetValueOrDefault(key);
                buckets[key] = (bucket.R + c.R, bucket.G + c.G, bucket.B + c.B, bucket.Count + 1);
            }
        }

        if (buckets.Count == 0)
            return MediaColor.FromRgb(255, 255, 255);

        var dominant = buckets.Values.OrderByDescending(bucket => bucket.Count).First();
        return MediaColor.FromRgb(
            (byte)(dominant.R / dominant.Count),
            (byte)(dominant.G / dominant.Count),
            (byte)(dominant.B / dominant.Count));
    }

    /// <summary>
    /// The dominant colour among the pixels inside the box that stand well clear of the background —
    /// within 40% of the furthest one, and never closer than 60.
    /// </summary>
    private static MediaColor? DominantGlyphColor(PixelWindow window, Rectangle inner, MediaColor background)
    {
        int maxDiff = 0;
        for (int y = inner.Top; y < inner.Bottom; y++)
        {
            for (int x = inner.Left; x < inner.Right; x += 2)
            {
                int diff = Distance(window.At(x, y), background);
                if (diff > maxDiff) maxDiff = diff;
            }
        }

        int threshold = Math.Max(60, (int)(maxDiff * 0.6));
        var vote = new DominantColorVote();
        for (int y = inner.Top; y < inner.Bottom; y++)
        {
            for (int x = inner.Left; x < inner.Right; x += 2)
            {
                var c = window.At(x, y);
                if (Distance(c, background) >= threshold)
                    vote.Add(c.R, c.G, c.B);
            }
        }

        return vote.Dominant();
    }

    private static int Distance(System.Drawing.Color c, MediaColor background) =>
        Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B);
}
