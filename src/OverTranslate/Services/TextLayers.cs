using System.Drawing;
using MediaColor = System.Windows.Media.Color;

namespace OverTranslate.Services;

/// <summary>
/// What one text box on screen is made of: the surface the glyphs sit on, the glyphs' own colour,
/// and the outline drawn around them when there is one.
/// </summary>
/// <param name="Surface">
/// What the glyphs are drawn directly on. Usually the picture around the box; a balloon or a
/// button when the text sits on one that the ring around the box reaches past.
/// </param>
/// <param name="Body">Null when no pixel inside the box stood far enough from the surface.</param>
/// <param name="Outline">The band drawn around every glyph, or null when there is none to see.</param>
internal readonly record struct TextLayers(MediaColor Surface, MediaColor? Body, MediaColor? Outline);

/// <summary>
/// Separates a text box into <see cref="TextLayers"/>, for both things that need to know: the
/// colour a translation is drawn in, and what the erase has to cover.
/// </summary>
/// <remarks>
/// <para>The colour used to be one question — the commonest colour inside the box that stands well
/// clear of the commonest colour around it — and that is right exactly when what is around the box
/// is what the glyphs were drawn on. Two ordinary kinds of text break it, and both are the same
/// mistake: the ring reaches past the layer the glyphs actually touch.</para>
///
/// <para>A plate. A balloon on a manga page, a pill-shaped button: the box sits inside it, the ring
/// around the box lands on the page outside it, and the plate itself becomes the most common
/// "far" colour — so the text came back the colour of the balloon, drawn on the colour of the page.
/// White on black, over a white balloon the reader can see. Measured over 15 manga pages, every
/// balloon that sits against black picture came back this way; the pills of a game menu did too.
/// What gives a plate away is how much of the box it fills (<see cref="PlateShare"/>): text strokes
/// cover a fifth of a tight box, a plate the text is printed on covers most of it.</para>
///
/// <para>An outline. White subtitles with a black edge, the white-on-blue labels of a game UI: the
/// edge stands further from the scene than the white body does, it has more pixels at the edge of
/// every stroke, and so it won the vote — the translation came back black where the source was
/// white. The outline is told apart by what it encloses: it runs all the way round every glyph, so
/// the glyph body is the colour sitting in its holes that cannot reach the ring without crossing it.
/// That is also what the inside of an "o" is, which is why the test asks for more than holes; see
/// <see cref="Enclosure"/>.</para>
/// </remarks>
internal static class TextLayerReader
{
    /// <summary>How close, as a summed channel distance, a pixel must be to a layer to count as it.</summary>
    private const int Near = 75;

    /// <summary>Share of the box a far colour must cover to be the surface rather than the text.</summary>
    /// <remarks>
    /// Over 1,198 boxes of every corpus the far colour covers 0.02–0.45 of the box when it is text,
    /// strokes and outline together, and 0.51–0.89 when it is a balloon or button the box sits in.
    /// </remarks>
    private const double PlateShare = .5;

    /// <summary>Share of the ring around the box a plate must also cover.</summary>
    /// <remarks>
    /// A plate runs on past the box and text does not: the box is drawn tight around the glyphs, so
    /// their colour is hardly in the ring at all, while every balloon and button measured has 16-50%
    /// of the ring. That is what keeps a very bold line, or a solid bar of highlighted text, from
    /// being read as the plate it is printed on.
    /// </remarks>
    private const double PlateRing = .1;

    /// <summary>
    /// Enclosed pixels, against the outline's own, below which the holes are only counters.
    /// </summary>
    private const double Enclosure = .3;

    /// <summary>Summed channel distance the body and outline must stand apart by.</summary>
    private const int Apart = 150;

    /// <summary>
    /// Share of the body's colour inside the box that must be enclosed by the outline, when the body
    /// is also far from the surface — and, when it is not, <see cref="EnclosedAlone"/>.
    /// </summary>
    /// <remarks>
    /// <para>This is what separates a body from a counter. A white "4" with a dark edge, on an
    /// orange badge, encloses dark pixels in its counter — but most of its dark pixels are the edge
    /// outside it, which reaches the badge: 2% of them are enclosed. A glyph body is enclosed all of
    /// it, less whatever the box cut off: 77–100% on subtitles, 38–100% on game labels.</para>
    ///
    /// <para>When the body is the same colour as the surface — white subtitles with a black edge
    /// over a bright scene, black manga text in a white outline over black picture — that is the
    /// only evidence left, and it is weak: the dark counters of a white label on a dark page are
    /// enclosed 10–13%, a white glyph body in a black edge 57–100%, and black manga text inside a
    /// white outline over dark screentone only 16–35%, because the screentone inside the box is
    /// the same black. Those last are left as they were: white, which is what they read as once
    /// the erase takes the outline, rather than guessed at.</para>
    /// </remarks>
    private const double EnclosedWithContrast = .3;

    /// <inheritdoc cref="EnclosedWithContrast"/>
    private const double EnclosedAlone = .5;

    /// <summary>
    /// The layers of the box <paramref name="inner"/>, read from <paramref name="window"/>, which
    /// covers <paramref name="outer"/> — the box and a ring around it.
    /// </summary>
    /// <param name="surfaceOverride">
    /// A surface the caller has already established by a better route than the ring.
    /// </param>
    public static TextLayers Read(PixelWindow window, Rectangle outer, Rectangle inner, MediaColor? surfaceOverride = null)
    {
        var ring = surfaceOverride ?? DominantBackground(window, outer, inner);
        if (DominantGlyphColor(window, inner, ring) is not { } first)
            return new TextLayers(ring, null, null);

        var plate = Plate(window, outer, inner, first);
        if (plate is { } body)
            return new TextLayers(first, body, null);

        return Enclosed(window, outer, inner, ring, first) is { } inside
            ? new TextLayers(ring, inside, first)
            : new TextLayers(ring, first, null);
    }

    /// <summary>The glyphs' colour against <paramref name="first"/>, when that is a plate.</summary>
    private static MediaColor? Plate(PixelWindow window, Rectangle outer, Rectangle inner, MediaColor first)
    {
        long covered = 0, total = 0, around = 0, ring = 0;
        for (int y = outer.Top; y < outer.Bottom; y++)
        {
            for (int x = outer.Left; x < outer.Right; x += 2)
            {
                bool near = Distance(window.At(x, y), first) < Near;
                if (inner.Contains(x, y)) { covered += near ? 1 : 0; total++; }
                else { around += near ? 1 : 0; ring++; }
            }
        }
        if (total == 0 || covered < total * PlateShare || around < ring * PlateRing)
            return null;

        return DominantGlyphColor(window, inner, first);
    }

    /// <summary>
    /// The colour <paramref name="outline"/> holds inside it, when that is a glyph body rather
    /// than a counter; null otherwise.
    /// </summary>
    private static MediaColor? Enclosed(
        PixelWindow window, Rectangle outer, Rectangle inner, MediaColor surface, MediaColor outline)
    {
        int width = outer.Width, height = outer.Height;
        var other = new bool[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                other[y * width + x] = Distance(window.At(outer.Left + x, outer.Top + y), outline) >= Near;

        // Everything not the outline that can walk to the ring without crossing it.
        var reached = new bool[width * height];
        var queue = new Queue<int>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (!other[i] || inner.Contains(outer.Left + x, outer.Top + y)) continue;
                reached[i] = true;
                queue.Enqueue(i);
            }
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % width, y = i / width;
            if (x > 0) Visit(i - 1);
            if (x < width - 1) Visit(i + 1);
            if (y > 0) Visit(i - width);
            if (y < height - 1) Visit(i + width);
        }
        void Visit(int j)
        {
            if (!other[j] || reached[j]) return;
            reached[j] = true;
            queue.Enqueue(j);
        }

        long outlined = 0, enclosed = 0;
        var vote = new DominantColorVote();
        for (int y = inner.Top; y < inner.Bottom; y++)
        {
            for (int x = inner.Left; x < inner.Right; x++)
            {
                int i = (y - outer.Top) * width + (x - outer.Left);
                if (!other[i]) outlined++;
                else if (!reached[i])
                {
                    enclosed++;
                    var c = window.At(x, y);
                    vote.Add(c.R, c.G, c.B);
                }
            }
        }
        if (outlined == 0 || enclosed < outlined * Enclosure || vote.Dominant() is not { } body)
            return null;
        if (Distance(body, outline) < Apart)
            return null;

        long bodyLike = 0, bodyEnclosed = 0;
        for (int y = inner.Top; y < inner.Bottom; y++)
        {
            for (int x = inner.Left; x < inner.Right; x++)
            {
                if (Distance(window.At(x, y), body) >= Near) continue;
                bodyLike++;
                int i = (y - outer.Top) * width + (x - outer.Left);
                if (other[i] && !reached[i]) bodyEnclosed++;
            }
        }
        double share = bodyLike == 0 ? 0 : bodyEnclosed / (double)bodyLike;
        bool standsOut = Distance(body, surface) >= Apart;
        return share >= (standsOut ? EnclosedWithContrast : EnclosedAlone) ? body : null;
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

    private static int Distance(Color c, MediaColor other) =>
        Math.Abs(c.R - other.R) + Math.Abs(c.G - other.G) + Math.Abs(c.B - other.B);

    private static int Distance(MediaColor c, MediaColor other) =>
        Math.Abs(c.R - other.R) + Math.Abs(c.G - other.G) + Math.Abs(c.B - other.B);
}
