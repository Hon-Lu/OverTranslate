using System.Drawing;
using MediaColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Rect;

namespace OverTranslate.Services;

/// <summary>What one text box on screen was drawn in: the surface its glyphs sit on, their colour, and their outline.</summary>
/// <param name="Text">Null when no pixel inside the box stood far enough from the background.</param>
/// <param name="Outline">The band drawn around every glyph, when there is one — see <see cref="TextLayers"/>.</param>
internal readonly record struct SourceTextColor(MediaColor Background, MediaColor? Text, MediaColor? Outline = null);

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
    /// <param name="backgroundOverride">
    /// A background the caller has already established by a better route than the ring — see
    /// <see cref="CaptureBackgroundColor"/>. The glyph colour is still read against it, because
    /// what counts as a glyph is "far from the background" and that question needs an answer here.
    /// </param>
    /// <param name="ringReference">
    /// The height the ring around the box is sized from, for callers whose box is a whole paragraph
    /// rather than one line. Defaults to the box's own height, which is what a single line wants.
    /// </param>
    public static SourceTextColor? Sample(
        Bitmap frame, WpfRect bounds, MediaColor? backgroundOverride = null, double? ringReference = null)
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
        // Sized from one line, not from the box. They are the same number for a single line, which
        // is what these ratios were chosen against; for a grouped paragraph the box is the whole
        // paragraph, and a ring 28% of *that* clears the panel the paragraph is printed on and
        // reads the page behind it instead.
        double reference = ringReference is { } given && given > 0 ? given : bounds.Height;
        int padX = Math.Max(4, (int)Math.Round(reference * 0.35));
        int padY = Math.Max(3, (int)Math.Round(reference * 0.28));
        var outer = Rectangle.FromLTRB(
            Math.Clamp((int)bounds.X - padX, 0, frame.Width),
            Math.Clamp((int)bounds.Y - padY, 0, frame.Height),
            Math.Clamp((int)(bounds.X + bounds.Width) + padX, 0, frame.Width),
            Math.Clamp((int)(bounds.Y + bounds.Height) + padY, 0, frame.Height));

        if (PixelWindow.Read(frame, outer) is not { } window)
            return null;

        var layers = TextLayerReader.Read(window, outer, inner, backgroundOverride);
        return new SourceTextColor(layers.Surface, layers.Body, layers.Outline);
    }

    /// <summary>
    /// The colours the capture overlay draws a block in: the sampled background, and a text colour
    /// that is tuned toward the source and guaranteed legible on it.
    /// </summary>
    /// <param name="sourceLines">
    /// The block's own lines, when it has more than one. A paragraph is read from the paper between
    /// its lines rather than from the ring around it, and the ring — still the fallback when the
    /// gaps hold no single colour — is sized from a line rather than from the paragraph.
    /// </param>
    public static (MediaColor Background, MediaColor Text) ForCaptureOverlay(
        Bitmap frame, WpfRect bounds, IReadOnlyList<WpfRect>? sourceLines = null, bool vertical = false)
    {
        var black = MediaColor.FromRgb(0, 0, 0);
        var white = MediaColor.FromRgb(255, 255, 255);

        var paper = sourceLines is { Count: > 1 }
            ? CaptureBackgroundColor.BetweenLines(frame, sourceLines, vertical)
            : null;
        double? ring = sourceLines is { Count: > 0 }
            ? sourceLines.Min(line => vertical ? line.Width : line.Height)
            : null;

        if (Sample(frame, bounds, paper, ring) is not { } sample)
            return (white, black);

        var background = sample.Background;
        if (sample.Text is not { } text)
            return (background, OverlayTextColor.PerceivedLuminance(background) > 0.5 ? black : white);

        // The card has no outline to draw, so an outlined source is written in whichever of its two
        // colours reads on the card. The body where it does — white labels on a blue button stay
        // white — and the outline where it does not: white subtitles with a black edge over a bright
        // scene were drawn in the edge's black before the body could be told apart, and on a card of
        // the scene's colour that is still the one of the two that can be read.
        var tuned = OverlayTextColor.Tune(text, background);
        if (sample.Outline is { } outline
            && OverlayTextColor.ContrastRatio(tuned, background) < OverlayTextColor.MinimumContrast
            && OverlayTextColor.ContrastRatio(outline, background) > OverlayTextColor.ContrastRatio(tuned, background))
            tuned = OverlayTextColor.Tune(outline, background);

        return (background, OverlayTextColor.EnsureContrast(tuned, background, OverlayTextColor.MinimumContrast));
    }
}
