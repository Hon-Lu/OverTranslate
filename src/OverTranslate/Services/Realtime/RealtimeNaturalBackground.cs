using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GdiColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Rect;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Builds a live-looking background patch for realtime translation. The source glyph rectangle is
/// replaced with colour interpolated from the pixels immediately around it, while every pixel
/// outside the OCR rectangle stays exactly as it was on screen. This is intentionally lightweight:
/// it runs several times per second next to OCR and avoids adding another native/AI dependency.
/// </summary>
/// <remarks>
/// Both halves of this file read a bitmap through one lock and a managed buffer rather than through
/// GDI+'s per-pixel accessors, because "lightweight" was not true of the first version: erasing a
/// line locked the whole patch and copied it three times over, and sampling a colour called GetPixel
/// — which locks and unlocks the bitmap on every call — some fifteen thousand times for one line.
/// Both ran on the thread drawing the interface. Neither the output nor the algorithm changed with
/// the buffers; only what it costs to arrive at them.
/// </remarks>
internal static class RealtimeNaturalBackground
{
    // OCR rectangles are often tight around the main glyph body. Japanese dakuten/handakuten,
    // punctuation dots and antialiasing can sit well outside that rectangle, which leaves the white
    // "crumbs" visible after replacement. Padding is therefore adaptive to the detected line height
    // rather than a fixed two pixels. The top side is deliberately the largest because detached
    // Japanese marks overwhelmingly live above the glyph body.
    private const int MinErasePadX = 5;
    private const int MinErasePadTop = 8;
    private const int MinErasePadBottom = 4;

    /// <summary>Every line a patch has to erase, from the blocks the window is drawing.</summary>
    /// <remarks>
    /// All of them, not only the block whose patch is being built. A patch is a fresh copy of the
    /// picture under it, so a line reaching into it that is left alone is reprinted over the repair
    /// its own block's patch made — and lines do reach into each other's patches, because each patch
    /// is grown by a guard margin and a Latin detection box is tall enough to overlap its neighbour
    /// before that. Two stacked English subtitle lines measured 765..835 and 827..901: the lower
    /// patch begins above the upper line's baseline and used to reprint the bottom half of it.
    ///
    /// Blocks with nothing translated are left out. Nothing was drawn over them, so their text is
    /// still on screen to be read, and erasing the part of it that falls in someone else's patch
    /// would rub out half a line the user can still read the rest of.
    /// </remarks>
    public static IReadOnlyList<WpfRect> EraseTargets(IReadOnlyList<TranslatedBlock> blocks) =>
    [
        .. blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.TranslatedText))
            .SelectMany(block => (block.SourceLineBounds is { Count: > 0 } lines ? lines : [block.Bounds])
                .Select(line => GlyphBounds(line, block.RenderGlyphHeight)))
    ];

    /// <summary>The part of a line rectangle its glyphs actually occupy.</summary>
    /// <remarks>
    /// A Latin source keeps the detector's box unshrunk, because the translation drawn over it is
    /// sized to cover the taller original glyphs; only the glyph height travels separately. Erasing
    /// that whole box is what this is for. Measured over the English subtitle corpus, at the size
    /// realtime reads a subtitle strip at, the box is 66–125px tall for glyphs of 33–49px — so the
    /// repair was being asked to invent a strip three times the height of the text it replaced, and
    /// interpolating that far turns whatever the line sits on into one smeared band.
    ///
    /// Centred, because the box is concentric with the text: over the same corpus the box's vertical
    /// centre and that of the same line read as CJK — which does shrink its box — agreed to within
    /// half a pixel on every line, while the widths were identical. The looseness is in height only,
    /// and it is symmetric.
    ///
    /// The padding <see cref="EraseSourceText"/> adds is what covers the rest: an outline, an
    /// ascender, the antialiasing around them.
    /// </remarks>
    public static WpfRect GlyphBounds(WpfRect line, double? glyphHeight)
    {
        if (glyphHeight is not { } height || height <= 0 || height >= line.Height)
            return line;

        double top = line.Y + (line.Height - height) / 2;
        double bottom = Math.Min(line.Bottom, top + height * (1 + DescenderAllowance));
        return new WpfRect(line.X, top, line.Width, bottom - top);
    }

    /// <summary>How much further down than its glyph height a Latin line reaches, as a fraction.</summary>
    /// <remarks>
    /// The glyph height is measured from the line's average pitch, which is the body of the text: it
    /// does not know about the tail of a g, j, p, q or y, nor about the outline drawn around it. Left
    /// out, that tail is not merely missed — the row band is read from immediately below the strip,
    /// so the band lands ON the tail, and a stroke a few pixels wide is drawn the height of the fill
    /// as a black column. It is the most visible way this repair fails on English subtitles.
    ///
    /// Swept over the English corpus at 0.00/0.15/0.20/0.25/0.30/0.35/0.50 and read off the repaired
    /// frames: the columns are obvious to 0.20, faint at 0.25, and gone at 0.30 on every frame that
    /// had a descender. Above that nothing improves and the strip only invents more of the picture.
    ///
    /// Latin only, because it applies to a rectangle that carries a glyph height, and CJK does not —
    /// its box already sits on its glyphs, which have no descender to reach past it.
    /// </remarks>
    private const double DescenderAllowance = 0.30;

    /// <param name="sourceLineBounds">
    /// Every line to erase out of this patch, as the rectangles its glyphs occupy — see
    /// <see cref="GlyphBounds"/>. Lines belonging to neighbouring blocks count: a patch is a copy of
    /// the picture, so any line reaching into it that is left alone is reprinted over whatever
    /// repaired it.
    /// </param>
    public static Bitmap? CreatePatch(
        Bitmap frame,
        Rectangle patchBounds,
        IReadOnlyList<WpfRect> sourceLineBounds)
    {
        var frameBounds = new Rectangle(0, 0, frame.Width, frame.Height);
        var clippedPatch = Rectangle.Intersect(frameBounds, patchBounds);
        if (clippedPatch.Width <= 0 || clippedPatch.Height <= 0)
            return null;

        var sources = new Rectangle[sourceLineBounds.Count];
        for (int i = 0; i < sourceLineBounds.Count; i++)
            sources[i] = Rectangle.FromLTRB(
                (int)Math.Floor(sourceLineBounds[i].Left),
                (int)Math.Floor(sourceLineBounds[i].Top),
                (int)Math.Ceiling(sourceLineBounds[i].Right),
                (int)Math.Ceiling(sourceLineBounds[i].Bottom));

        var work = Rectangle.Intersect(frameBounds, WorkingArea(clippedPatch, sources));

        Bitmap patch;
        try
        {
            patch = frame.Clone(work, PixelFormat.Format32bppArgb);
        }
        catch
        {
            return null;
        }

        if (sources.Length == 0)
            return patch;

        // One lock and one copy each way for the whole patch, however many lines are erased into it.
        // Every line reads only pixels outside the area it writes — see EraseSourceText — so they can
        // all work in this one buffer, and each still sees the lines erased before it exactly as it
        // did when every line locked the bitmap for itself.
        var data = patch.LockBits(
            new Rectangle(0, 0, patch.Width, patch.Height),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * patch.Height];
            for (int y = 0; y < patch.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);

            foreach (var source in sources)
            {
                var local = source with { X = source.X - work.X, Y = source.Y - work.Y };
                EraseSourceText(pixels, stride, patch.Width, patch.Height, local);
            }

            for (int y = 0; y < patch.Height; y++)
                Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
        }
        finally
        {
            patch.UnlockBits(data);
        }

        if (work == clippedPatch)
            return patch;

        using (patch)
        {
            var inside = new Rectangle(
                clippedPatch.X - work.X, clippedPatch.Y - work.Y, clippedPatch.Width, clippedPatch.Height);
            try
            {
                return patch.Clone(inside, PixelFormat.Format32bppArgb);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The area the repair is carried out in: the patch, grown to hold whole every line that reaches
    /// into it.
    /// </summary>
    /// <remarks>
    /// A patch is a crop of the picture, and a fill cut off by the edge of one is not the same fill:
    /// with no rows beyond the cut to read, <see cref="EraseSourceText"/> falls back to extending the
    /// one edge it has, which paints a flat block. Two patches that overlap then disagree about the
    /// line they share, and the one painted second leaves its version behind as a rectangle with a
    /// visible edge — the seam between two stacked subtitle lines.
    ///
    /// Repairing in an area that contains the whole line makes the fill for a line the same wherever
    /// it is drawn, because it is computed from the same pixels either way. The patch itself is cut
    /// out of the result afterwards.
    /// </remarks>
    private static Rectangle WorkingArea(Rectangle patch, IReadOnlyList<Rectangle> sources)
    {
        var work = patch;

        foreach (var source in sources)
        {
            var area = ErasedArea(source);
            if (!area.IntersectsWith(patch)) continue;

            // The rows and columns the fill is read from, which is as deep as one band goes.
            area.Inflate(BandDepth, BandDepth);
            work = Rectangle.Union(work, area);
        }

        return work;
    }

    /// <summary>
    /// Samples the source text colour so the replacement keeps roughly the same visual hierarchy as
    /// the original. Falls back to the configured realtime text colour when no convincing foreground
    /// pixels can be separated from the local background.
    /// </summary>
    public static MediaColor SampleTextColor(Bitmap frame, WpfRect bounds, MediaColor fallback)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return fallback;

        var inner = Clamp(bounds, frame.Width, frame.Height);
        if (inner.Width <= 0 || inner.Height <= 0)
            return fallback;

        // The ring the background is read from and the box the glyphs are read from, in one window:
        // the two passes below and the dominant-background pass all sample inside it, so the bitmap
        // is locked once for the whole decision.
        int padX = Math.Max(4, (int)Math.Round(bounds.Height * 0.35));
        int padY = Math.Max(3, (int)Math.Round(bounds.Height * 0.28));
        var outer = Rectangle.FromLTRB(
            Math.Clamp(inner.Left - padX, 0, frame.Width),
            Math.Clamp(inner.Top - padY, 0, frame.Height),
            Math.Clamp(inner.Right + padX, 0, frame.Width),
            Math.Clamp(inner.Bottom + padY, 0, frame.Height));
        if (outer.Width <= 0 || outer.Height <= 0)
            return fallback;

        if (PixelWindow.Read(frame, outer) is not { } window)
            return fallback;

        var bg = SampleDominantBackground(window, outer, inner);

        int maxDiff = 0;
        for (int y = inner.Top; y < inner.Bottom; y += 2)
        {
            for (int x = inner.Left; x < inner.Right; x += 2)
            {
                var c = window.At(x, y);
                int diff = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
                if (diff > maxDiff) maxDiff = diff;
            }
        }

        if (maxDiff < 36)
            return fallback;

        int threshold = Math.Max(54, (int)Math.Round(maxDiff * 0.58));
        var vote = new DominantColorVote();
        for (int y = inner.Top; y < inner.Bottom; y += 2)
        {
            for (int x = inner.Left; x < inner.Right; x += 2)
            {
                var c = window.At(x, y);
                int diff = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
                if (diff < threshold) continue;
                vote.Add(c.R, c.G, c.B);
            }
        }

        if (vote.Dominant() is not { } sampled)
            return fallback;

        var background = MediaColor.FromRgb(bg.R, bg.G, bg.B);

        // Nothing is corrected until it is known to be worth keeping: the fallback is the colour the
        // user chose, and tuning that would be overruling them rather than repairing a measurement.
        if (!SeparatesFrom(sampled, background))
            return fallback;

        return OverlayTextColor.Tune(sampled, background);
    }

    /// <summary>The area a line's fill covers: its glyphs, and the padding this repair adds.</summary>
    private static Rectangle ErasedArea(Rectangle source)
    {
        int heightBasis = Math.Clamp(source.Height, 12, 72);
        int padX = Math.Max(MinErasePadX, (int)Math.Ceiling(heightBasis * 0.16));
        int padTop = Math.Max(MinErasePadTop, (int)Math.Ceiling(heightBasis * 0.34));
        int padBottom = Math.Max(MinErasePadBottom, (int)Math.Ceiling(heightBasis * 0.16));

        return Rectangle.FromLTRB(
            source.Left - padX,
            source.Top - padTop,
            source.Right + padX,
            source.Bottom + padBottom);
    }

    /// <summary>
    /// Fills one source line's rectangle with colour interpolated from its surroundings.
    /// </summary>
    /// <remarks>
    /// Reads only outside the rectangle it writes, and reads all of it before writing any of it.
    /// That is what lets several lines share one buffer — there is no order in which one line's
    /// fill can be read as though it were the picture underneath.
    /// </remarks>
    private static void EraseSourceText(byte[] pixels, int stride, int width, int height, Rectangle source)
    {
        var rect = Rectangle.Intersect(new Rectangle(0, 0, width, height), ErasedArea(source));
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var above = rect.Top > 0 ? RowBand(pixels, stride, height, rect, above: true) : null;
        var below = rect.Bottom < height ? RowBand(pixels, stride, height, rect, above: false) : null;

        // Interpolating between two real edges beats extending one of them, whichever axis it is on,
        // so both two-sided cases are tried before either one-sided one. A line rect is wide and
        // short, which is why the rows come first: reaching across its width from a single column
        // either side is a smear the length of the sentence.
        if (above is not null && below is not null)
        {
            FillFromRows(pixels, stride, rect, above, below);
            return;
        }

        var left = rect.Left > 0 ? ColumnBand(pixels, stride, width, rect, left: true) : null;
        var right = rect.Right < width ? ColumnBand(pixels, stride, width, rect, left: false) : null;

        if (left is not null && right is not null)
        {
            FillFromColumns(pixels, stride, rect, left, right);
            return;
        }

        if (above is not null || below is not null)
        {
            FillFromRows(pixels, stride, rect, above, below);
            return;
        }

        if (left is not null || right is not null)
        {
            FillFromColumns(pixels, stride, rect, left, right);
            return;
        }

        // Nothing outside the rectangle is left to read from, so there is nothing to interpolate.
        // Taken before anything is written, which is also why it can read the same buffer: every
        // pixel it averages lies outside the rectangle about to be filled.
        var fill = BorderAverage(pixels, stride, width, height, rect);

        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            int row = y * stride;
            for (int x = rect.Left; x < rect.Right; x++)
            {
                int dst = row + x * 4;
                pixels[dst] = fill.B;
                pixels[dst + 1] = fill.G;
                pixels[dst + 2] = fill.R;
                pixels[dst + 3] = 255;
            }
        }
    }

    /// <summary>How deep, in rows or columns, one edge of the rectangle is read.</summary>
    /// <remarks>
    /// Three, reduced to their median rather than averaged. The padding around a line is sized for
    /// the glyph body, so an outline, a shadow or a Japanese mark can still reach the first row
    /// outside it — and one lit pixel there, carried the whole height of the fill, is a bright line
    /// down the middle of the repair. A median discards that row's value as long as the other two
    /// agree, so the line is never drawn in the first place. An average would not: it would let the
    /// stray pixel through at a third of its strength, which is a fainter line, not no line.
    ///
    /// It costs nothing in sharpness, which is what separates it from spreading the band sideways —
    /// the other half of the attempt this came from, and the half that made repairs visibly blurry.
    ///
    /// Three exactly: <see cref="Median"/> is written for three and nothing else.
    /// </remarks>
    private const int BandDepth = 3;

    /// <summary>The rows above or below the rectangle, reduced to one B,G,R per column of it.</summary>
    private static byte[] RowBand(byte[] pixels, int stride, int height, Rectangle rect, bool above)
    {
        var band = new byte[rect.Width * 3];
        var samples = new byte[BandDepth];

        for (int i = 0; i < rect.Width; i++)
        {
            int x = rect.Left + i;
            for (int channel = 0; channel < 3; channel++)
            {
                for (int depth = 0; depth < BandDepth; depth++)
                {
                    int y = Math.Clamp(above ? rect.Top - 1 - depth : rect.Bottom + depth, 0, height - 1);
                    samples[depth] = pixels[y * stride + x * 4 + channel];
                }

                band[i * 3 + channel] = Median(samples);
            }
        }

        return band;
    }

    /// <summary>The columns left or right of the rectangle, reduced to one B,G,R per row of it.</summary>
    private static byte[] ColumnBand(byte[] pixels, int stride, int width, Rectangle rect, bool left)
    {
        var band = new byte[rect.Height * 3];
        var samples = new byte[BandDepth];

        for (int i = 0; i < rect.Height; i++)
        {
            int row = (rect.Top + i) * stride;
            for (int channel = 0; channel < 3; channel++)
            {
                for (int depth = 0; depth < BandDepth; depth++)
                {
                    int x = Math.Clamp(left ? rect.Left - 1 - depth : rect.Right + depth, 0, width - 1);
                    samples[depth] = pixels[row + x * 4 + channel];
                }

                band[i * 3 + channel] = Median(samples);
            }
        }

        return band;
    }

    private static void FillFromRows(byte[] pixels, int stride, Rectangle rect, byte[]? above, byte[]? below)
    {
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            double t = (y - rect.Top + 1.0) / (rect.Height + 1.0);
            int row = y * stride;

            for (int i = 0; i < rect.Width; i++)
            {
                int dst = row + (rect.Left + i) * 4;
                int at = i * 3;

                for (int channel = 0; channel < 3; channel++)
                    pixels[dst + channel] = Blend(above, below, at + channel, t);

                pixels[dst + 3] = 255;
            }
        }
    }

    private static void FillFromColumns(byte[] pixels, int stride, Rectangle rect, byte[]? left, byte[]? right)
    {
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            int row = y * stride;
            int at = (y - rect.Top) * 3;

            for (int x = rect.Left; x < rect.Right; x++)
            {
                double t = (x - rect.Left + 1.0) / (rect.Width + 1.0);
                int dst = row + x * 4;

                for (int channel = 0; channel < 3; channel++)
                    pixels[dst + channel] = Blend(left, right, at + channel, t);

                pixels[dst + 3] = 255;
            }
        }
    }

    /// <summary>
    /// One channel of the fill: between the two edges where both exist, and the one that does where
    /// only one does — the rectangle runs to the edge of the picture and there is nothing beyond it.
    /// </summary>
    private static byte Blend(byte[]? start, byte[]? end, int at, double t) =>
        start is null ? end![at]
        : end is null ? start[at]
        : Lerp(start[at], end[at], t);

    /// <summary>The middle of three, which is <see cref="BandDepth"/> and only that.</summary>
    private static byte Median(byte[] samples)
    {
        byte a = samples[0], b = samples[1], c = samples[2];
        return a > b
            ? (b > c ? b : a > c ? c : a)
            : (a > c ? a : b > c ? c : b);
    }

    private static GdiColor BorderAverage(
        byte[] pixels,
        int stride,
        int width,
        int height,
        Rectangle rect)
    {
        long r = 0, g = 0, b = 0, n = 0;

        void Add(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            int i = y * stride + x * 4;
            b += pixels[i];
            g += pixels[i + 1];
            r += pixels[i + 2];
            n++;
        }

        for (int x = rect.Left; x < rect.Right; x++)
        {
            Add(x, rect.Top - 1);
            Add(x, rect.Bottom);
        }
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            Add(rect.Left - 1, y);
            Add(rect.Right, y);
        }

        return n == 0
            ? GdiColor.Black
            : GdiColor.FromArgb(255, (int)(r / n), (int)(g / n), (int)(b / n));
    }

    private static GdiColor SampleDominantBackground(
        PixelWindow window, Rectangle outer, Rectangle inner)
    {
        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
        for (int y = outer.Top; y < outer.Bottom; y += 2)
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
            return GdiColor.White;

        var dominant = buckets.Values.OrderByDescending(x => x.Count).First();
        return GdiColor.FromArgb(
            255,
            (int)(dominant.R / dominant.Count),
            (int)(dominant.G / dominant.Count),
            (int)(dominant.B / dominant.Count));
    }

    /// <summary>
    /// Whether what was sampled stands far enough from its background to be worth drawing.
    /// </summary>
    /// <remarks>
    /// The source may depend on an outline that sampling cannot reproduce. Where it does, the two
    /// averages come out close together, and the user's configured colour is safer than a foreground
    /// that is nearly invisible.
    /// </remarks>
    private static bool SeparatesFrom(MediaColor sampled, MediaColor background) =>
        Math.Abs(sampled.R - background.R) +
        Math.Abs(sampled.G - background.G) +
        Math.Abs(sampled.B - background.B) >= 90;

    private static Rectangle Clamp(WpfRect rect, int width, int height) => Rectangle.FromLTRB(
        Math.Clamp((int)Math.Floor(rect.Left), 0, width),
        Math.Clamp((int)Math.Floor(rect.Top), 0, height),
        Math.Clamp((int)Math.Ceiling(rect.Right), 0, width),
        Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, height));

    private static byte Lerp(byte a, byte b, double t) =>
        (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);

    /// <summary>
    /// One rectangle of a bitmap's pixels, read out once so a sampler can index it in the bitmap's
    /// own coordinates.
    /// </summary>
    /// <remarks>
    /// The alternative is <c>Bitmap.GetPixel</c>, which locks the bitmap, reads four bytes and
    /// unlocks it again on every call. Sampling one subtitle line asks for around fifteen thousand
    /// pixels, so this is the difference between one lock and fifteen thousand.
    /// </remarks>
    private readonly struct PixelWindow(byte[] pixels, int stride, Rectangle area)
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
}
