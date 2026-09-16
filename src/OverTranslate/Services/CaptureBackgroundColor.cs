using System.Drawing;
using MediaColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Rect;

namespace OverTranslate.Services;

/// <summary>
/// The paper a paragraph is printed on, read from the gaps between the paragraph's own lines.
/// </summary>
/// <remarks>
/// <para>Every other background colour here comes from a ring around the text box, and for a single
/// line that is the right place to look. For a grouped paragraph it is not: the box is the whole
/// paragraph and the ring is sized from the box, so around a 300px-tall paragraph it reaches some
/// 80px past it on every side — far enough to step off the panel the text is printed on and read
/// whatever is behind it. The gaps between the lines cannot leave the paragraph; they are inside
/// it by construction.</para>
///
/// <para>Paragraphs only. One line has no interior to read, and its ring is already the right
/// size.</para>
/// </remarks>
internal static class CaptureBackgroundColor
{
    /// <summary>
    /// How much of the gap has to agree before one colour is called the paper. Below this the
    /// gaps hold texture, a picture, or two surfaces meeting, and none of those is something to
    /// paint a whole bubble in — the caller falls back to the ring.
    /// </summary>
    private const double Majority = 0.6;

    /// <summary>Tight leading leaves almost nothing between two lines; that is not a measurement.</summary>
    private const int MinimumSamples = 64;

    /// <summary>The paper colour, or null when there is no paragraph here or no colour holds.</summary>
    /// <param name="vertical">Vertical text: the lines are columns and the gaps between them run
    /// down the page rather than across it.</param>
    public static MediaColor? BetweenLines(Bitmap frame, IReadOnlyList<WpfRect> lines, bool vertical)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return null;

        var ordered = lines
            .Where(line => line.Width > 0 && line.Height > 0
                && double.IsFinite(line.X + line.Y + line.Width + line.Height))
            .OrderBy(line => vertical ? line.X : line.Y)
            .ToArray();
        if (ordered.Length < 2) return null;

        var vote = new DominantColorVote();
        int samples = 0;

        for (int i = 1; i < ordered.Length; i++)
        {
            var gap = Gap(ordered[i - 1], ordered[i], vertical, frame.Width, frame.Height);
            if (gap.Width <= 0 || gap.Height <= 0) continue;
            if (PixelWindow.Read(frame, gap) is not { } pixels) continue;

            int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)gap.Width * gap.Height / 2048)));
            for (int y = gap.Top; y < gap.Bottom; y += step)
            {
                for (int x = gap.Left; x < gap.Right; x += step)
                {
                    // A third line can cross the gap between this pair — a superscript, or a
                    // column of a table read as one paragraph. Its glyphs are not the paper.
                    if (ordered.Any(line => line.Contains(x, y))) continue;

                    var pixel = pixels.At(x, y);
                    vote.Add(pixel.R, pixel.G, pixel.B);
                    samples++;
                }
            }
        }

        if (samples < MinimumSamples) return null;
        return vote.DominantWithShare() is { } dominant && dominant.Share >= Majority
            ? dominant.Color
            : null;
    }

    /// <summary>
    /// The clear band between two consecutive lines, pulled in from both so the row of descenders
    /// and the antialiased edge above the next line are not read as paper.
    /// </summary>
    private static Rectangle Gap(WpfRect first, WpfRect second, bool vertical, int width, int height)
    {
        double thickness = vertical
            ? Math.Min(first.Width, second.Width)
            : Math.Min(first.Height, second.Height);
        double inset = Math.Clamp(thickness * 0.15, 2, 6);

        var (left, right, top, bottom) = vertical
            ? (first.Right + inset, second.Left - inset,
               Math.Max(first.Top, second.Top), Math.Min(first.Bottom, second.Bottom))
            : (Math.Max(first.Left, second.Left), Math.Min(first.Right, second.Right),
               first.Bottom + inset, second.Top - inset);

        return Rectangle.FromLTRB(
            (int)Math.Clamp(Math.Ceiling(left), 0, width),
            (int)Math.Clamp(Math.Ceiling(top), 0, height),
            (int)Math.Clamp(Math.Floor(right), 0, width),
            (int)Math.Clamp(Math.Floor(bottom), 0, height));
    }
}
