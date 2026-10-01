using System.Drawing;
using OverTranslate.Imaging;

namespace OverTranslate.Services.Realtime;

internal record CpuTextRegion(double X, double Y, double Width, double Height, double? GlyphHeight);

/// <summary>Stateless CPU segmentation: the text, and the outline and antialiasing drawn with it.</summary>
internal static class CpuTextMask
{
    /// <summary>How much picture is taken in around a recognition box, as a share of glyph height.</summary>
    /// <remarks>
    /// Everything here is measured inside one box, so this is two things at once: the context the two
    /// hats and their Otsu thresholds are computed over, and the room the mask has to grow past the
    /// box. It is not an allowance the mask is entitled to fill. Widening it feeds more picture into
    /// the thresholds, and at .12 clamped to six — what it was while the mask was a band of fixed
    /// width around the body — the card corpus put a third of its tiles onto the half-resolution
    /// repair and the game corpus tripled the mask islands that hold no text at all.
    /// </remarks>
    private const double BoxPadding = .09;

    /// <summary>Contrast a pixel needs to continue the text's fade outward.</summary>
    /// <inheritdoc cref="Build"/>
    private const double OutlineTail = 10;

    /// <summary>How far past the glyphs the mask may reach, as a share of glyph height.</summary>
    /// <remarks>
    /// <para>How far the mask reaches used to be a count — four pixels of growth, one of
    /// antialiasing, two of margin — and a count is two different things at two sizes. At the
    /// 44-pixel glyphs of a burnt-in subtitle seven pixels is a sixth of the letter; at the 14-pixel
    /// text of a chat panel it is half of it, and it is also the whole gap to the line below. The
    /// mask for a paragraph of small text became one slab: measured over the panel corpus, only 54%
    /// of the picture lying between two lines of text survived it. That is the worst place to lose,
    /// because it is the only observation the fill has when it is asked to carry the scene across the
    /// line. It was reported as a hump — the fill's own outline showing, there being nothing left
    /// under the words for it to agree with.</para>
    ///
    /// <para>So the reach is the smallest of three things: this share of the glyph, half the clear
    /// space to the nearest line over or under it (<see cref="Room"/>), and
    /// <see cref="Furthest"/>. Over the panel corpus that takes the surviving picture between lines
    /// from 54% to 68% and the share of the frame the repair disturbs where no text was from 1.16%
    /// to 0.68%, and it leaves the three subtitle corpora, whose lines have room, where they
    /// were.</para>
    ///
    /// <para>What it costs is the outline: on the panel corpus what is left along a glyph goes from
    /// 28.9% to 42.1%. That is the trade, and it is the one that was asked for — the erase reads as
    /// wrong when it flattens the picture, and merely imperfect when a stroke's last pixel
    /// survives.</para>
    /// </remarks>
    private const double Reach = .20;

    /// <summary>The most it may reach in any case, which is what a subtitle's outline asked for.</summary>
    /// <remarks>
    /// Swept over the subtitle corpora as a count, before it became a cap. Each pixel buys less than
    /// the one before it and costs more picture: from three to four, what is left along the game
    /// corpus's glyphs falls 22.3% to 17.6% for two hundredths of a point of disturbed picture, and
    /// from four to five it falls 17.6% to 15.1% for two more — while on the video and Latin corpora
    /// the fifth pixel erases nothing further and disturbs a twentieth of a point more. Four of
    /// growth, one for the antialiasing the growth stopped on, and two of margin.
    /// </remarks>
    private const int Furthest = 7;

    /// <summary>The most of that reach that may be spent on the blind margin rather than the fade.</summary>
    /// <remarks>
    /// The margin is applied to each line inside its own box, widened by it first so a glyph at the
    /// edge still gets one, rather than to the whole frame at the end — which is what lets it differ
    /// from line to line at all. Two pixels where there is room for seven and one where there is not:
    /// it was tried at one everywhere when the growth was new, on the reasoning that the growth
    /// reaches the fade where it actually is while a margin pays for every glyph everywhere, and a
    /// reader reported that version as colour left along the edge of erased words.
    /// </remarks>
    private const int FurthestHalo = 2;

    /// <summary>Both hats' bodies, followed outward along the text's own fade.</summary>
    /// <remarks>
    /// <para>Text is whatever stands out from its surroundings inside a recognition box, in either
    /// direction, so both hats get a body mask and the union is taken. Nothing votes on which
    /// direction the text is. That vote existed — the louder hat over the box was taken for the text
    /// and the other one searched for its outline — and it is wrong exactly where it costs most: a
    /// line of bright text on a dark scene has gaps between its glyphs narrower than the kernel, so
    /// the closing behind the black hat fills them and the dark response answers for the whole band
    /// of picture <em>between</em> the words, which is more area than the strokes are. The box voted
    /// dark, the body mask became the background between the glyphs, and the glyphs survived as
    /// whatever the outline search could reach from one of those filled gaps — a stroke with
    /// neighbours was found, a stroke standing on its own was not. In Japanese the lone strokes are
    /// the punctuation, which is why a line could come back with every exclamation mark still on it.
    /// Deciding the polarity by each hat's peak instead of its mean was tried and is worse: a single
    /// bright speck then decides the line. Taking both bodies makes the question moot.</para>
    ///
    /// <para>What the body leaves is the fade — an outline, a shadow, or plain antialiasing, all of
    /// them a ramp from the text down to the picture, with no single threshold that separates the
    /// bottom of the ramp from the picture itself. High enough not to eat the scene is high enough to
    /// leave the last pixel or two of every stroke, which is the dotted contour a reader sees tracing
    /// erased words. So the body is the seed of a hysteresis: it is followed outward while the
    /// response stays above <see cref="OutlineTail"/> and no further than <see cref="Reach"/>
    /// allows, which is the text's own fade wherever it happens to be, rather than a band of fixed
    /// width around every glyph. <see cref="OutlineTail"/> is low enough to be a
    /// genuine second threshold and high enough that film grain is not a path to walk along; swept at
    /// 6, 10, 14 and 18 it barely moves what is erased while the masked share of the frame falls
    /// steadily as it rises.</para>
    ///
    /// <para>The band is what this replaced, and that is the difference a reader sees. Of the pixels
    /// around a glyph that the source has darker than the scene — which is what an outline is — the
    /// share still darker after the repair, against the share of the frame the repair visibly changes
    /// where no text or outline ever was:</para>
    ///
    /// <code>
    ///   corpus                left behind, band -> fade    picture disturbed, band -> fade
    ///   chat-room (panel)            36.5%      28.9%            1.11%      1.14%
    ///   ja-game (dialogue)           38.2%      17.6%            0.12%      0.05%
    ///   ja-card                      68.6%      70.8%            0.22%      0.08%
    ///   ja-video                      9.9%       8.2%            0.63%      0.38%
    ///   en                           14.2%      18.0%            0.52%      0.39%
    /// </code>
    ///
    /// <para>Mask islands holding no text at all — a smudge over clean picture, which is what a band
    /// around a Latin box produces, since the box stands well clear of its glyphs and the band fills
    /// with whatever was in there — fall from 1.0 to 0.2 per frame on the Latin corpus and from 1.4
    /// to 0.5 on the game corpus. The card corpus moves a quarter of its tiles back from the
    /// half-resolution repair to the full-resolution one, because it is a thick hole that routes a
    /// tile there. What it costs is the two corpora above where more is left behind: the card corpus,
    /// whose subtitles already sit on a dark band, and Latin text, whose outline is thicker than the
    /// fade this follows. Both were judged the better trade against a picture the erase leaves
    /// alone.</para>
    /// </remarks>
    public static ImageBuffer Build(ImageBuffer source, IReadOnlyList<CpuTextRegion> lines)
    {
        var mask = new ImageBuffer(source.Size, PixelType.U8C1);
        try
        {
            using var gray = ColorConversion.ToGray(source);
            var one = StructuringElement.Ellipse(3, 3);
            foreach (var line in lines)
            {
                if (!double.IsFinite(line.X + line.Y + line.Width + line.Height) || line.Width <= 0 || line.Height <= 0) continue;
                double height = Math.Max(1, Math.Min(line.GlyphHeight ?? line.Height, line.Height));
                int padding = Math.Clamp((int)Math.Ceiling(height * BoxPadding), 2, 4);
                int allowance = Math.Min(Furthest, Math.Min(
                    Math.Max(2, (int)Math.Round(height * Reach) + 1),
                    Math.Max(2, Room(line, lines))));
                int halo = Math.Clamp((int)Math.Round(allowance * .3), 1, FurthestHalo);
                int growth = Math.Max(0, allowance - 1 - halo);
                var box = Clip(line.X - padding, line.Y - padding, line.X + line.Width + padding,
                    line.Y + line.Height + padding, source.Size);
                if (box.Width == 0 || box.Height == 0) continue;
                using var roi = new ImageBuffer(gray, box);
                using var light = ImageBuffer.Uninitialized(box.Size, PixelType.U8C1);
                using var dark = ImageBuffer.Uninitialized(box.Size, PixelType.U8C1);
                Hats(roi, height, light, dark);
                using var seed = ImageBuffer.Uninitialized(box.Size, PixelType.U8C1);
                using var darker = ImageBuffer.Uninitialized(box.Size, PixelType.U8C1);
                Body(light, seed);   // What stands out brighter than its surroundings,
                Body(dark, darker);  // and what stands out darker. Either one can be the text.
                Arithmetic.Or(seed, darker, seed);
                using var strongest = ImageBuffer.Uninitialized(box.Size, PixelType.U8C1);
                Arithmetic.Max(light, dark, strongest);
                if (growth > 0) GrowAlongTail(seed, strongest, one, growth);
                Morphology.Dilate(seed, seed, one); // The antialiased end of whatever the growth stopped on.

                // The blind margin, in a box widened to hold it. Two pixels at a subtitle's size:
                // one was tried when the growth was new, on the reasoning that the growth reaches the
                // fade where it actually is while a margin pays for every glyph everywhere, and a
                // reader reported that version as colour left along the edge of erased words. It
                // measures the same way — dropping it leaves more behind on every corpus, 32.6%
                // against 28.9% on the panel corpus and 22.3% against 17.6% on the game corpus, to
                // disturb between a tenth and a quarter of a point less of the picture.
                var wide = Clip(box.X - halo, box.Y - halo, box.Right + halo, box.Bottom + halo, source.Size);
                using var spread = new ImageBuffer(wide.Width, wide.Height, PixelType.U8C1);
                using (var inner = new ImageBuffer(spread, new Rectangle(box.X - wide.X, box.Y - wide.Y, box.Width, box.Height)))
                    seed.CopyTo(inner);
                Morphology.Dilate(spread, spread, one, iterations: halo);
                using var target = new ImageBuffer(mask, wide);
                Arithmetic.Or(target, spread, target);
            }
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }

    /// <summary>The widest structuring element the hats are computed with, in pixels.</summary>
    private const int WidestKernel = 25;

    /// <summary>The element a glyph has to ask for before it is measured scaled down.</summary>
    /// <remarks>
    /// Not <see cref="WidestKernel"/> itself. The subtitle corpora sit just past it — burnt-in
    /// Japanese subtitles measure 45-63 pixels, so they ask for 25-35 — and their strokes are a
    /// fifth of that, well inside the element they already get; measured with the scaled path from
    /// the old limit, those masks grew by up to seven tenths of a point of the frame for no stroke
    /// left behind before. The strokes that do outgrow the element belong to display type: a manga
    /// shout at 110 pixels, a title card.
    /// </remarks>
    private const int ScaledFrom = 41;

    /// <summary>
    /// Both hats of <paramref name="roi"/>, with an element a little over half the glyph height.
    /// </summary>
    /// <remarks>
    /// <para>A hat only answers for what is narrower than its element, so a stroke wider than the
    /// element is found at its ends and edges and missed in the middle. The element stopped at
    /// <see cref="WidestKernel"/> because an ellipse costs its own area per pixel; above a 45-pixel
    /// glyph that left the middle of the thickest strokes behind — a manga shout set at 110 pixels
    /// came back as a row of black blots where the junctions of its strokes had been.</para>
    ///
    /// <para>So a glyph far too big for the widest element is measured on the box scaled down until
    /// it fits, and the answers are scaled back up. The cost stays that of the widest element over a
    /// smaller box, and nothing under <see cref="ScaledFrom"/> changes.</para>
    /// </remarks>
    private static void Hats(ImageBuffer roi, double height, ImageBuffer light, ImageBuffer dark)
    {
        int wanted = (int)Math.Round(height * .55) | 1;
        if (wanted < ScaledFrom || roi.Width < 4 || roi.Height < 4)
        {
            int size = Math.Clamp(wanted, 5, WidestKernel);
            var kernel = StructuringElement.Ellipse(size, size);
            Morphology.TopHat(roi, light, kernel);
            Morphology.BlackHat(roi, dark, kernel);
            return;
        }

        double scale = WidestKernel / (double)wanted;
        var reduced = new Size(Math.Max(2, (int)Math.Round(roi.Width * scale)), Math.Max(2, (int)Math.Round(roi.Height * scale)));
        using var small = Resize.To(roi, reduced, Interpolation.Area);
        var widest = StructuringElement.Ellipse(WidestKernel, WidestKernel);
        using var smallLight = ImageBuffer.Uninitialized(reduced, PixelType.U8C1);
        using var smallDark = ImageBuffer.Uninitialized(reduced, PixelType.U8C1);
        Morphology.TopHat(small, smallLight, widest);
        Morphology.BlackHat(small, smallDark, widest);
        Resize.To(smallLight, light, Interpolation.Linear);
        Resize.To(smallDark, dark, Interpolation.Linear);
    }

    /// <summary>
    /// One response's body: Otsu, then the same threshold again with a floor under it, so a response
    /// with nothing in it cannot be split into one anyway.
    /// </summary>
    /// <remarks>
    /// <para>A method rather than two copies of four lines, because the two hats have to be
    /// thresholded identically by construction: the union of the two is the mask's seed, and a
    /// difference between them would be read as a difference in the picture.</para>
    ///
    /// <para>The four-fifths is worth knowing about, because Otsu assumes two populations and a busy
    /// picture has no such thing: on the panel corpus a discounted threshold takes in the scene's own
    /// texture until the body fills a third to a half of every line box, and what is erased there is
    /// not text but the picture. Taking the discount off was swept and does what it should — a
    /// twenty-fifth less mask, four more points of the picture surviving between two lines, a third
    /// of a level less error against a known background. It also leaves four times as much of a
    /// glyph's own antialiasing standing, and a fill anchored on that inherits it; the backdrop
    /// plate's guard test reads the result as a gradient going four levels flatter. Both sides of
    /// that are real, and the trade belongs to a round that can weigh the residue it costs against
    /// the picture it saves rather than to this one.</para>
    /// </remarks>
    private static void Body(ImageBuffer response, ImageBuffer into)
    {
        double otsu = Threshold.Otsu(response);
        Threshold.Binary(response, into, Math.Max(18, otsu * .8), 255);
    }

    /// <summary>Extends the seed along the text's own fade — see <see cref="Build"/>.</summary>
    /// <remarks>
    /// A geodesic dilation: grow by a pixel, keep only what the tail threshold allows, put the seed
    /// back so a step can never lose ground. Both images are the recognition box, not the frame, and
    /// the loop runs a fixed number of times rather than to stability — the point is a bounded reach,
    /// and a run to stability would follow a scene edge for as far as that edge happens to be dark.
    /// </remarks>
    private static void GrowAlongTail(ImageBuffer seed, ImageBuffer response, StructuringElement one, int steps)
    {
        using var tail = ImageBuffer.Uninitialized(response.Size, PixelType.U8C1);
        Threshold.Binary(response, tail, OutlineTail, 255);
        using var grown = ImageBuffer.Uninitialized(seed.Size, PixelType.U8C1);
        for (int step = 0; step < steps; step++)
        {
            Morphology.Dilate(seed, grown, one);
            Arithmetic.And(grown, tail, grown);
            Arithmetic.Or(grown, seed, seed);
        }
    }

    /// <summary>Half the clear space between this line and the nearest one over or under it.</summary>
    /// <remarks>
    /// Half, so that two neighbours reaching toward each other still leave the picture between them
    /// alone. Only lines that stand over one another count: a caption at the other end of the frame
    /// is not what limits this one.
    /// </remarks>
    private static int Room(CpuTextRegion line, IReadOnlyList<CpuTextRegion> lines)
    {
        double nearest = double.MaxValue;
        foreach (var other in lines)
        {
            if (ReferenceEquals(other, line) || other.Width <= 0 || other.Height <= 0) continue;
            double shared = Math.Min(line.X + line.Width, other.X + other.Width) - Math.Max(line.X, other.X);
            if (shared < Math.Min(line.Width, other.Width) * .2) continue;
            double apart = other.Y >= line.Y + line.Height ? other.Y - (line.Y + line.Height)
                : line.Y >= other.Y + other.Height ? line.Y - (other.Y + other.Height)
                : 0;
            nearest = Math.Min(nearest, apart);
        }
        return nearest == double.MaxValue ? int.MaxValue : (int)Math.Floor(nearest / 2);
    }

    private static Rectangle Clip(double left, double top, double right, double bottom, Size size)
    {
        int x = (int)Math.Clamp(Math.Floor(left), 0, size.Width);
        int y = (int)Math.Clamp(Math.Floor(top), 0, size.Height);
        int r = (int)Math.Clamp(Math.Ceiling(right), x, size.Width);
        int b = (int)Math.Clamp(Math.Ceiling(bottom), y, size.Height);
        return new(x, y, r - x, b - y);
    }
}

internal sealed record CpuRepair(ImageBuffer Image, int FullTiles, int ReducedTiles) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

/// <summary>What was seen of the picture, averaged down, and how much of each cell that was.</summary>
internal sealed record Reduction(ImageBuffer Sum, ImageBuffer Seen) : IDisposable
{
    public void Dispose() { Sum.Dispose(); Seen.Dispose(); }
}

/// <summary>No background history, GPU, model, or shared mutable frame state.</summary>
/// <remarks>
/// <para>Two fills, because the hole left by a line of text is asked to do two different things.
/// Where the picture around it carries structure — an edge, a face, foliage — what belongs in the
/// hole is whatever the structure was doing, and that is what inpainting is for. Where the picture
/// around it is a surface catching light, what belongs in the hole is the surface's own slope, and
/// inpainting is bad at exactly that: it advances inward from the boundary averaging as it goes, so
/// a hole wider than its radius fills with something flatter than its surroundings. On a background
/// whose shading runs at an angle that reads as a flat bar where the line was, and a stack of lines
/// reads as a staircase — which is how this was reported.</para>
///
/// <para>The flat bar is not a tuning problem, it is what a diffusion from the boundary does. A
/// harmonic interpolation does not have it: a plane satisfies Laplace's equation, so a solve of that
/// kind carries a slope across a hole exactly, at any angle. <see cref="SmoothFill"/> is one, by
/// weighted multiresolution interpolation. What it cannot do is stop at an edge, so the two are
/// blended by <see cref="SlopeShare"/>, which asks of each place how far the picture there departs
/// from a slope.</para>
/// </remarks>
internal static class CpuHoleRepair
{
    /// <summary>How far around a pixel the inpainting that fills a tile reads — Navier–Stokes, see <see cref="Inpaint.NavierStokes"/>.</summary>
    /// <remarks>
    /// <para>Both were the tutorial's defaults rather than a choice. They were swept against clean
    /// picture: eight 1824x223 bands of real frames the shipped detector finds no text anywhere in,
    /// a subtitle drawn over each, and the repair scored inside its own mask against the band it was
    /// cut from. Forty smaller crops of the same kind, at 640x220, answer the same way.</para>
    ///
    /// <para>Telea's method is the cheaper of the two and buys nothing with it.
    /// Over the eight bands the whole repair goes from 34.4ms to 32.0ms — the inpainting call itself
    /// from 15.3 to 13.0 — while the error goes from 19.47 to 19.54 levels, and over the forty
    /// smaller crops Navier–Stokes is the better of the two in 30. Two milliseconds of a hundred is
    /// not a reason to change what the fill is.</para>
    ///
    /// <para>The radius is where the cost actually is, and the trap is that levels do not see it. At
    /// 1, 2, 3 and 4 the error is 19.43, 19.51, 19.47 and 19.48 while the repair costs 24.1, 28.0,
    /// 35.3 and 44.4ms — read that alone and the radius is free to lower. What separates them is
    /// colour: scored as distance from the truth's a*/b*, the worst hundredth inside the mask on the
    /// hardest band is 25 at radius 1, 18 at 2 and 17 at 3, and that number is a picture. At 1 a
    /// cyan streak stands in the fill where the scene has none and the tiles step against one
    /// another; at 2 the steps are still there on smooth dark picture. Three is where both stop, and
    /// four costs a quarter more than three for nothing. This is the same axis the half-resolution
    /// choice sits on — see <see cref="RepairReduced"/> — and it fails the same way: mean error is
    /// blind to it, so it cannot be the thing that decides.</para>
    /// </remarks>
    private const int FillRadius = 3;

    /// <summary>Departure from a local slope, in levels, at which the smooth fill is fully trusted.</summary>
    /// <inheritdoc cref="SlopeShare"/>
    private const double SlopeDeparture = 8;

    /// <summary>Departure at which it is not used at all.</summary>
    /// <inheritdoc cref="SlopeShare"/>
    private const double StructureDeparture = 30;

    /// <summary>How much picture around a place is asked whether it is a slope.</summary>
    /// <inheritdoc cref="SlopeShare"/>
    private const int DepartureWindow = 97;

    /// <summary>How far outside the mask the fills are computed, so a hole has context on every side.</summary>
    /// <remarks>
    /// A tile of the loop below is aligned to its own grid, so a tile holding the outermost masked
    /// pixel can reach most of a tile past it. At <see cref="Tile"/> the two fills always cover every
    /// tile the loop will ask them about, which is what lets the mix be a crop rather than a copy.
    /// </remarks>
    private const int Margin = Tile;

    /// <summary>The square the structured fill is computed in, one at a time.</summary>
    private const int Tile = 96;

    public static CpuRepair Repair(ImageBuffer source, ImageBuffer mask, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (source.Type != PixelType.U8C3 || mask.Type != PixelType.U8C1 || source.Size != mask.Size)
            throw new ArgumentException("Expected matching BGR image and byte mask.");
        var holes = Statistics.BoundingRect(mask);
        if (holes.Width == 0 || holes.Height == 0) return new(source.Clone(), 0, 0);

        var area = Clip(holes, Margin, source.Size);
        using var frame = new ImageBuffer(source, area);
        using var within = new ImageBuffer(mask, area);
        using var reduced = Reduce(frame, within);
        using var slope = SlopeShare(reduced, frame.Size);
        var (least, most) = Statistics.MinMax(slope, within);
        least /= 255;
        most /= 255;
        token.ThrowIfCancellationRequested();

        if (most <= .02) return Grained(Structured(source, mask, token), source, mask, area);

        using var smooth = SmoothFill(reduced, frame.Size);
        token.ThrowIfCancellationRequested();
        if (least >= .98)
        {
            var only = source.Clone();
            using (var target = new ImageBuffer(only, area)) smooth.CopyTo(target, within);
            return Grained(new(only, 0, 0), source, mask, area);
        }

        // Both fills are wanted, and mixing whole frames would pay for every pixel of a screen to
        // settle a question only the text asks. The tile loop already walks what the mask covers,
        // so the mix happens there, on the tiles and through the same mask the fill is written by.
        return Grained(Structured(source, mask, token, smooth, slope, area), source, mask, area);
    }

    /// <summary>The window the surrounding grain is measured over, in full-resolution pixels.</summary>
    /// <remarks>
    /// Wide enough that a panel's worth of picture answers rather than the few pixels pressed against
    /// a glyph, narrow enough that a smooth sky next to a rough wall is still told apart.
    /// </remarks>
    private const int GrainWindow = 33;

    /// <summary>The most any one pixel may contribute to that reading, in levels.</summary>
    /// <remarks>
    /// An average of raw departures is an average of whatever strong thing happens to lie nearby,
    /// and a line of text usually has one: the line above it, a panel edge, a face's eyes. Read
    /// straight, the smooth cheek beside a pair of eyelashes asks to be grained as hard as the
    /// eyelashes, and the result is a visibly speckled cheek — measurably closer in texture,
    /// obviously wrong to look at. Bounding each pixel's contribution keeps an edge from speaking
    /// for a surface while leaving surfaces that really are this busy — foliage, gravel, a tiled
    /// floor — to answer at their full strength, since none of their pixels needed the allowance.
    /// </remarks>
    private const double GrainQuiet = 12;

    /// <summary>The most grain that may be invented, in levels of standard deviation.</summary>
    /// <remarks>
    /// A ceiling, not a target. Where the surroundings are genuinely violent — a specular highlight,
    /// a hard panel edge — matching them exactly would mean inventing that much contrast out of
    /// nothing, and being wrong by that much is worse than being smooth by that much.
    /// </remarks>
    private const double GrainCeiling = 9;

    /// <summary>Mean absolute value of a standard normal, which is what the window above measures.</summary>
    private const double GrainExpectation = .7979;

    /// <summary>Restores the grain the fill could not invent, matched to what surrounds it.</summary>
    /// <remarks>
    /// <para>Every fill available without a model is some form of diffusion, and diffusion is smooth
    /// by construction. Measured over the corpora, what the repair writes carries between a seventh
    /// and two fifths of the detail of the picture around it — so what is left behind is not text but
    /// a patch of unnatural calm in the shape of the words, and that shape is what a reader sees. It
    /// is the complaint: not that the erase left something, that the erase is visible.</para>
    ///
    /// <para>What is missing is high-frequency energy, and high-frequency energy is the one thing that
    /// can be put back without knowing what was there — because at that scale nobody can tell the
    /// difference between the grain that belongs and grain that merely has the same strength. So the
    /// strength is measured from the picture that surrounds each hole, the strength already present in
    /// the fill is subtracted, and the difference is made up from a fixed noise field.</para>
    ///
    /// <para>Fixed, and that is the whole reason it is not simply drawn each time: the same frame must
    /// grain the same way twice, or a still picture would boil ten times a second. It is also
    /// self-limiting — where the surroundings are smooth the measurement is near zero and nothing is
    /// added, so a clear sky stays a clear sky.</para>
    /// </remarks>
    private static CpuRepair Grained(CpuRepair repair, ImageBuffer source, ImageBuffer mask, Rectangle area)
    {
        try
        {
            using var outside = ImageBuffer.Uninitialized(mask.Size, PixelType.U8C1);
            Arithmetic.Not(mask, outside);
            // What the fill wrote, before any of it is grained. A tile reads its neighbours for
            // context, and a neighbour that has already been grained would report the grain as
            // detail it already had and be shortchanged for it — a seam along every tile edge.
            using var smoothed = repair.Image.Clone();

            // The window never reaches past this, so a tile computed with this much context around
            // it gets the same answer it would have got from the whole frame at once, and only the
            // tiles the mask actually touches are paid for. On a subtitle that is a twentieth of them.
            const int guard = GrainWindow / 2 + 1;
            const int span = Tile * 2;
            for (int y = area.Y; y < area.Bottom; y += span)
            for (int x = area.X; x < area.Right; x += span)
            {
                var block = new Rectangle(x, y, Math.Min(span, area.Right - x), Math.Min(span, area.Bottom - y));
                using var covered = new ImageBuffer(mask, block);
                if (Statistics.CountNonZero(covered) == 0) continue;

                var crop = Clip(block, guard, source.Size);
                using var frame = new ImageBuffer(source, crop);
                using var filled = new ImageBuffer(smoothed, crop);
                using var beyond = new ImageBuffer(outside, crop);
                using var within = new ImageBuffer(mask, crop);
                using var ambient = Energy(frame, beyond);
                using var present = Energy(filled, within);

                using var strength = ImageBuffer.Uninitialized(crop.Size, PixelType.F32C1);
                Arithmetic.Subtract(ambient, present, strength);
                Arithmetic.Max(strength, 0.0, strength);
                Arithmetic.Min(strength, GrainCeiling * GrainExpectation, strength);
                Arithmetic.Divide(strength, GrainExpectation, strength);

                using var noise = Noise(crop);
                Arithmetic.Multiply(noise, strength, noise);
                using var grain = Channels.Spread(noise);
                using var grained = ImageBuffer.Uninitialized(crop.Size, PixelType.U8C3);
                Arithmetic.AddToBytes(filled, grain, grained);

                using var interior = new ImageBuffer(grained, new Rectangle(block.X - crop.X, block.Y - crop.Y, block.Width, block.Height));
                using var target = new ImageBuffer(repair.Image, block);
                interior.CopyTo(target, covered);
            }
            return repair;
        }
        catch { repair.Dispose(); throw; }
    }

    /// <summary>How far the picture departs from its own local average, where it was seen.</summary>
    private static ImageBuffer Energy(ImageBuffer image, ImageBuffer where)
    {
        // Whole levels throughout: the reading is an average of departures already bounded at
        // twelve, so carrying it in floating point would be carrying precision that was thrown
        // away two lines earlier, at four times the memory traffic on the largest images.
        using var gray = ColorConversion.ToGray(image);
        using var average = ImageBuffer.Uninitialized(gray.Size, PixelType.U8C1);
        Filters.Blur(gray, average, 5);
        using var detail = ImageBuffer.Uninitialized(gray.Size, PixelType.U8C1);
        Arithmetic.AbsoluteDifference(gray, average, detail);
        Arithmetic.Min(detail, GrainQuiet, detail);

        // A plain average would read the calm of the hole as the calm of the scene, so only the
        // pixels that answered are counted — the same weighted reduction the fill itself is built on.
        using var answered = new ImageBuffer(detail.Size, PixelType.U8C1);
        detail.CopyTo(answered, where);
        using var carried = Filters.BoxSum(answered, GrainWindow);
        using var seen = Filters.BoxSum(where, GrainWindow);
        Arithmetic.Max(seen, 1e-3, seen);
        var energy = ImageBuffer.Uninitialized(seen.Size, PixelType.F32C1);
        Arithmetic.Divide(carried, seen, energy, 255); // The weights arrived as bytes, so undo their scale.
        return energy;
    }

    /// <summary>A fixed field of unit noise, so the same picture grains the same way every frame.</summary>
    private static readonly Lazy<float[]> Speckle = new(() =>
    {
        const int side = 256;
        var values = new float[side * side];
        ulong state = 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < values.Length; i += 2)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            double first = ((state >> 11) + 1) / (double)(1UL << 53);
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            double second = (state >> 11) / (double)(1UL << 53);
            double radius = Math.Sqrt(-2 * Math.Log(first));
            values[i] = (float)(radius * Math.Cos(2 * Math.PI * second));
            if (i + 1 < values.Length) values[i + 1] = (float)(radius * Math.Sin(2 * Math.PI * second));
        }
        return values;
    });

    /// <summary>The side of the square <see cref="Speckle"/> is tiled from.</summary>
    private const int SpeckleSide = 256;

    /// <remarks>Taken at the crop's own place in the field, so neighbouring crops agree.</remarks>
    private static unsafe ImageBuffer Noise(Rectangle crop)
    {
        // Read straight off the tiled field rather than by tiling it out first: the same values,
        // without a field the size of the crop rounded up to whole tiles.
        var field = Speckle.Value;
        int left = ((crop.X % SpeckleSide) + SpeckleSide) % SpeckleSide;
        int top = ((crop.Y % SpeckleSide) + SpeckleSide) % SpeckleSide;
        var noise = ImageBuffer.Uninitialized(crop.Size, PixelType.F32C1);
        for (int y = 0; y < crop.Height; y++)
        {
            float* row = noise.Row<float>(y);
            int from = ((top + y) % SpeckleSide) * SpeckleSide;
            for (int x = 0; x < crop.Width; x++) row[x] = field[from + (left + x) % SpeckleSide];
        }
        return noise;
    }

    /// <summary>Inpainting, tile by tile, at the resolution each tile's holes are thin enough for.</summary>
    private static CpuRepair Structured(ImageBuffer source, ImageBuffer mask, CancellationToken token,
        ImageBuffer? smooth = null, ImageBuffer? share = null, Rectangle within = default)
    {
        var holeBounds = within;
        var result = source.Clone();
        ImageBuffer? coarse = null;
        try
        {
            using var distance = DistanceTransform.Chamfer3(mask);
            int full = 0, reduced = 0;
            const int tile = Tile, guard = 24;
            int width = source.Width, height = source.Height;
            for (int y = 0; y < height; y += tile)
            for (int x = 0; x < width; x += tile)
            {
                token.ThrowIfCancellationRequested();
                var area = new Rectangle(x, y, Math.Min(tile, width - x), Math.Min(tile, height - y));
                using var tileMask = new ImageBuffer(mask, area);
                if (Statistics.CountNonZero(tileMask) == 0) continue;
                // A fill about to be weighted to nothing is not computed. Where the picture under
                // this tile is a slope the smooth solve answers alone, which also keeps a tile of
                // slope from being the reason the whole frame is inpainted at half resolution.
                if (smooth is not null && share is not null && Covered(share, Local(area, holeBounds), tileMask) >= 250)
                {
                    using var only = new ImageBuffer(smooth, Local(area, holeBounds));
                    using var onlyTarget = new ImageBuffer(result, area);
                    only.CopyTo(onlyTarget, tileMask);
                    continue;
                }
                using var tileDistance = new ImageBuffer(distance, area);
                var (_, radius) = Statistics.MinMax(tileDistance);
                var context = new Rectangle(Math.Max(0, x - guard), Math.Max(0, y - guard),
                    Math.Min(width, area.Right + guard) - Math.Max(0, x - guard),
                    Math.Min(height, area.Bottom + guard) - Math.Max(0, y - guard));
                using var frame = new ImageBuffer(source, context);
                using var holes = new ImageBuffer(mask, context);
                // A crop with nothing observed in it cannot be filled from itself, so it is filled
                // from the reduced repair, which reads the whole frame. It used to be skipped, which
                // left the tile as it was: in the middle of a wide enough mask — a column of manga
                // text over dark hair, where the mask had taken the texture too — a square of the
                // source text stood untouched inside the erase.
                bool blind = Statistics.CountNonZero(holes) == holes.Height * holes.Width;
                ImageBuffer filled;
                if (!blind && (radius <= 5 || Math.Min(context.Width, context.Height) < 8))
                {
                    filled = Inpaint.NavierStokes(frame, holes, FillRadius);
                    full++;
                }
                else
                {
                    coarse ??= RepairReduced(source, mask);
                    using var coarseTile = new ImageBuffer(coarse, area);
                    using var coarseTarget = new ImageBuffer(result, area);
                    using var coarseFill = Blend(coarseTile, smooth, share, area, within);
                    coarseFill.CopyTo(coarseTarget, tileMask);
                    reduced++;
                    continue;
                }
                // Every tile reads original pixels; only its own interior is published. The guard
                // makes neighbouring glyphs unavailable as donors without duplicating writes.
                using (filled)
                {
                    var local = new Rectangle(x - context.X, y - context.Y, area.Width, area.Height);
                    using var interior = new ImageBuffer(filled, local);
                    using var target = new ImageBuffer(result, area);
                    using var fill = Blend(interior, smooth, share, area, within);
                    fill.CopyTo(target, tileMask);
                }
            }
            token.ThrowIfCancellationRequested();
            return new(result, full, reduced);
        }
        catch { result.Dispose(); throw; }
        finally { coarse?.Dispose(); }
    }

    /// <summary>One tile's fill, moved toward the smooth one by however much of a slope it sits on.</summary>
    /// <remarks>The two fills cover <paramref name="within"/> rather than the frame, so the tile is
    /// looked up relative to it. Every tile the loop reaches lies inside the mask's own bounds, and
    /// those are what <paramref name="within"/> was cut around.</remarks>
    private static ImageBuffer Blend(ImageBuffer structured, ImageBuffer? smooth, ImageBuffer? share, Rectangle area, Rectangle within)
    {
        if (smooth is null || share is null) return structured.Clone();
        var local = Local(area, within);
        using var shareTile = new ImageBuffer(share, local);
        // Nothing of the smooth fill belongs here, so nothing of it is computed.
        if (Statistics.Mean(shareTile) <= 2) return structured.Clone();
        using var smoothTile = new ImageBuffer(smooth, local);
        return Mix(structured, smoothTile, shareTile);
    }

    /// <summary>The average share over the part of a tile the mask covers.</summary>
    private static double Covered(ImageBuffer share, Rectangle local, ImageBuffer tileMask)
    {
        using var shareTile = new ImageBuffer(share, local);
        return Statistics.Mean(shareTile, tileMask);
    }

    /// <summary>Where a tile of the frame lands in the two fills, which cover the mask's bounds.</summary>
    private static Rectangle Local(Rectangle area, Rectangle within) =>
        new(area.X - within.X, area.Y - within.Y, area.Width, area.Height);

    /// <summary>The picture averaged down, carrying how much of each cell was actually observed.</summary>
    /// <remarks>
    /// Both questions below are asked of this one reduction, and both are about neighbourhoods rather
    /// than pixels, so this is the only place either of them reads the frame at its own size. An area
    /// resize of the picture with the hole blacked out is the sum of what was seen in each cell, and
    /// the same resize of the mask is how much of the cell that was; the cell's colour is one divided
    /// by the other, and every level of the solve below is that pair again.
    /// </remarks>
    private static Reduction Reduce(ImageBuffer source, ImageBuffer mask)
    {
        // The ring just outside the mask is where whatever survived the erase lives — the last pixel
        // of an outline, the end of a fade. It is not asked what the picture there is: a fill
        // anchored on it inherits the thing it was meant to remove, and a slope measured through it
        // is measured through a contour. What gets replaced is still only what the mask covers.
        using var spread = ImageBuffer.Uninitialized(mask.Size, PixelType.U8C1);
        var ring = StructuringElement.Ellipse(DonorGuard * 2 + 1, DonorGuard * 2 + 1);
        Morphology.Dilate(mask, spread, ring);
        using var known = ImageBuffer.Uninitialized(mask.Size, PixelType.U8C1);
        Arithmetic.Not(spread, known);
        using var held = source.Clone();
        held.SetTo(0, spread);

        var size = new Size(Math.Max(1, source.Width / SlopeScale), Math.Max(1, source.Height / SlopeScale));
        using var heldSmall = Resize.To(held, size, Interpolation.Area);
        using var knownSmall = Resize.To(known, size, Interpolation.Area);
        var sum = Conversion.Convert(heldSmall, PixelType.F32C3);
        try { return new(sum, Conversion.Convert(knownSmall, PixelType.F32C1, 1.0 / 255)); }
        catch { sum.Dispose(); throw; }
    }

    /// <summary>
    /// How much each place looks like a slope rather than like structure: 1 where nothing within
    /// <see cref="DepartureWindow"/> pixels of it departs from a slope by more than
    /// <see cref="SlopeDeparture"/> levels, 0 at <see cref="StructureDeparture"/>, a straight line
    /// between.
    /// </summary>
    /// <remarks>
    /// <para>A plane's second difference is zero wherever it is taken, so that is the measure:
    /// shading of any steepness and any angle answers nothing, an edge or a texture answers its own
    /// contrast. It is taken over cells the mask does not cover, so the glyphs, which are the
    /// sharpest thing in the frame, are never mistaken for the scene.</para>
    ///
    /// <para>The strongest answer within reach decides, not the average of them. The fill that
    /// carries slopes will carry one straight across an edge that happens to be near it, however
    /// calm the rest of the neighbourhood is — a coloured panel behind a line of text is exactly
    /// that, no curvature anywhere except the line between the panel and the page, and taking the
    /// mean there says "slope" and drags the panel's colour out across the page.</para>
    ///
    /// <para>The reach is what decides how far an edge counts for. Measured on a scene built to be
    /// adversarial — a hard slanted edge crossing every line of text — 97 pixels is where the blend
    /// stops costing anything worth naming against inpainting alone (5.67 against 5.58 mean error)
    /// while a smooth background still answers 1 almost everywhere and a stack of erased lines stops
    /// reading as a staircase.</para>
    /// </remarks>
    private static ImageBuffer SlopeShare(Reduction reduced, Size full)
    {
        using var colour = Average(reduced.Sum, reduced.Seen);
        using var value = ColorConversion.ToGray(colour);

        // A plane's second difference is zero wherever it is measured, so this is how far the picture
        // departs from a slope — and it is a three-cell question, which is what makes it usable next
        // to a hole. A window average would have been the same measure over a wider reach, but with
        // a hole in the window the observed cells no longer sit symmetrically around the middle, and
        // the average shifts off the plane it was meant to reproduce: every glyph would be ringed by
        // structure that is not there. Measured on a bare ramp, that reads five levels of departure
        // beside the hole; this reads none.
        using var departure = Filters.Laplacian(value);
        Arithmetic.Absolute(departure, departure);

        // Only cells the picture was seen right through, and whose neighbours were too, can answer.
        using var whole = ImageBuffer.Uninitialized(reduced.Seen.Size, PixelType.F32C1);
        Threshold.Binary(reduced.Seen, whole, .9, 1);
        using var weight = ImageBuffer.Uninitialized(whole.Size, PixelType.F32C1);
        Morphology.Erode(whole, weight, StructuringElement.Rectangle(3, 3));

        Arithmetic.Multiply(departure, weight, departure);
        Filters.Blur(departure, departure, 3); // One cell of noise is not an edge.

        // The strongest departure anywhere within reach, not the average of them: a fill that has an
        // edge near it will be carried across that edge whether or not the rest of the window is
        // calm, and a flat panel beside another flat panel is exactly that — nowhere any curvature
        // except the line between them.
        // Taken in two steps: the strongest of each small neighbourhood, then the strongest of those
        // over the reach. A rectangular dilation costs its own width, so asking it for the reach in
        // one go is four times the work for the same answer.
        int side = Math.Max(3, DepartureWindow / SlopeScale / 4 | 1);
        using var strongest = ImageBuffer.Uninitialized(departure.Size, PixelType.F32C1);
        Morphology.Dilate(departure, strongest, StructuringElement.Rectangle(5, 5));
        var coarse = new Size(Math.Max(1, departure.Width / 4), Math.Max(1, departure.Height / 4));
        using var reach = Resize.To(strongest, coarse, Interpolation.Nearest);
        Morphology.Dilate(reach, reach, StructuringElement.Rectangle(side, side));

        // The answer is one number per neighbourhood, so it is turned into a share while it is still
        // one number per neighbourhood, and grown to the frame once at the end.
        Arithmetic.Subtract(reach, SlopeDeparture, reach);
        Arithmetic.Divide(reach, StructureDeparture - SlopeDeparture, reach);
        Arithmetic.Min(reach, 1.0, reach);
        Arithmetic.Max(reach, 0.0, reach);
        Arithmetic.Subtract(1.0, reach, reach);
        using var levels = Conversion.Convert(reach, PixelType.U8C1, 255);
        return Resize.To(levels, full, Interpolation.Linear);
    }

    /// <summary>The divisor the picture is judged a slope or not at.</summary>
    /// <remarks>
    /// Half, not a quarter like the fill: the question is whether an edge is hiding near the text,
    /// and the only place left to see one is the sliver of picture between the glyphs and whatever
    /// they cover. At a quarter of the frame that sliver is inside a cell the mask also touches, so
    /// it cannot testify, and a coloured panel behind a line of text reads as calm.
    /// </remarks>
    private const int SlopeScale = 2;

    /// <summary>How wide a ring outside the mask is left out of the fill's evidence.</summary>
    private const int DonorGuard = 2;

    /// <summary>How far down the fill is computed, as a divisor of the frame.</summary>
    /// <remarks>
    /// The result is smooth by construction, so there is nothing at full resolution for it to carry.
    /// Measured against the same fill computed at full resolution over six scenes, a quarter costs
    /// between nothing and half a percent of mean error and runs in a third of the time.
    /// </remarks>
    private const int SmoothScale = 4;

    /// <summary>
    /// Weighted multiresolution interpolation — a pull-push solve — of everything the mask covers.
    /// </summary>
    /// <remarks>
    /// <para>Pull: the known pixels are averaged down a pyramid, each level carrying both a sum and
    /// the weight it was divided by, so a level knows how much of itself was actually observed. Push:
    /// from the coarsest level back up, a level takes its own average where it has one and what the
    /// level above it says where it does not. The result is smooth, matches the picture at the edge
    /// of the hole, and carries a slope across the middle instead of settling on an average.</para>
    ///
    /// <para>The pyramid stops as soon as every pixel of the coarsest level has been observed, which
    /// is as deep as the widest hole needs and no deeper. That bound is what keeps the solve local:
    /// each level further is a level at which a value from the other side of the picture can reach
    /// this one, and on a scene with a hard edge across it, running to the top costs 40% more error
    /// than stopping here.</para>
    /// </remarks>
    private static ImageBuffer SmoothFill(Reduction reduced, Size full)
    {
        var sums = new List<ImageBuffer>();
        var seen = new List<ImageBuffer>();
        try
        {
            // The fill is carried on a coarser grid than the judgement above, which is free: the
            // reduction it starts from is already made, and halving it again is a small image.
            var start = new Size(Math.Max(1, reduced.Sum.Width * SlopeScale / SmoothScale),
                Math.Max(1, reduced.Sum.Height * SlopeScale / SmoothScale));
            sums.Add(Resize.To(reduced.Sum, start, Interpolation.Area));
            seen.Add(Resize.To(reduced.Seen, start, Interpolation.Area));
            while (sums[^1].Width > 2 && sums[^1].Height > 2)
            {
                var (observed, _) = Statistics.MinMax(seen[^1]);
                if (observed > 0) break;
                var size = new Size(Math.Max(1, sums[^1].Width / 2), Math.Max(1, sums[^1].Height / 2));
                sums.Add(Resize.To(sums[^1], size, Interpolation.Area));
                seen.Add(Resize.To(seen[^1], size, Interpolation.Area));
            }

            var estimate = Average(sums[^1], seen[^1]);
            for (int level = sums.Count - 2; level >= 0; level--)
            {
                using var previous = estimate;
                using var up = Resize.To(previous, sums[level].Size, Interpolation.Linear);
                using var own = Average(sums[level], seen[level]);
                using var share = ImageBuffer.Uninitialized(seen[level].Size, PixelType.F32C1);
                Arithmetic.Min(seen[level], 1.0, share);
                estimate = Mix(up, own, share);
            }

            using (estimate)
            {
                using var levels = Conversion.Convert(estimate, PixelType.U8C3);
                return Resize.To(levels, full, Interpolation.Linear);
            }
        }
        finally
        {
            foreach (var level in sums) level.Dispose();
            foreach (var level in seen) level.Dispose();
        }
    }

    /// <summary>A level's own colour where it was observed, and whatever the sum divides to where not.</summary>
    private static ImageBuffer Average(ImageBuffer sum, ImageBuffer seen)
    {
        using var safe = ImageBuffer.Uninitialized(seen.Size, PixelType.F32C1);
        Arithmetic.Max(seen, 1e-6, safe);
        using var spread = Channels.Spread(safe);
        var average = ImageBuffer.Uninitialized(sum.Size, PixelType.F32C3);
        Arithmetic.Divide(sum, spread, average);
        return average;
    }

    /// <summary><paramref name="second"/> where the share is 1, <paramref name="first"/> where it is 0.</summary>
    private static ImageBuffer Mix(ImageBuffer first, ImageBuffer second, ImageBuffer share)
    {
        using var scale = share.Type == PixelType.U8C1 ? Conversion.Convert(share, PixelType.F32C1, 1.0 / 255) : share.Clone();
        using var towards = Channels.Spread(scale);
        using var away = ImageBuffer.Uninitialized(towards.Size, PixelType.F32C3);
        Arithmetic.Subtract(1.0, towards, away);
        using var a = Float(first);
        using var b = Float(second);
        Arithmetic.Multiply(a, away, a);
        Arithmetic.Multiply(b, towards, b);
        Arithmetic.Add(a, b, a);
        return first.Type == PixelType.U8C3 ? Conversion.Convert(a, PixelType.U8C3) : a.Clone();
    }

    private static ImageBuffer Float(ImageBuffer image) =>
        image.Type == PixelType.F32C3 ? image.Clone() : Conversion.Convert(image, PixelType.F32C3);

    private static Rectangle Clip(Rectangle area, int margin, Size size)
    {
        int x = Math.Max(0, area.X - margin), y = Math.Max(0, area.Y - margin);
        return new(x, y, Math.Min(size.Width, area.Right + margin) - x,
            Math.Min(size.Height, area.Bottom + margin) - y);
    }

    internal static ImageBuffer RepairReduced(ImageBuffer source, ImageBuffer mask)
    {
        // Compute only the missing content at half resolution; composite at native resolution.
        // Expanding before area resampling prevents a thin masked stroke from disappearing.
        //
        // It is also a donor guard, which is the reason it survived being questioned. Dropping it
        // starts the fill a pixel closer to the evidence and scores better for it — 19.0 levels of
        // error against a known background rather than 20.0 — but the pixel it moves onto is the
        // antialiased edge of the glyph, and a fill anchored there inherits the thing it was meant
        // to remove. The backdrop plate's guard test reads that directly, as a gradient it cut from
        // the repaired frame going four levels flatter. One level of error is not worth it.
        using var expanded = ImageBuffer.Uninitialized(mask.Size, PixelType.U8C1);
        Morphology.Dilate(mask, expanded, StructuringElement.Ellipse(3, 3));
        var size = new Size(Math.Max(1, source.Width / 2), Math.Max(1, source.Height / 2));
        using var small = Resize.To(source, size, Interpolation.Area);
        using var smallMask = Resize.To(expanded, size, Interpolation.Area);
        Threshold.Binary(smallMask, smallMask, 0, 255);
        using var filled = Inpaint.NavierStokes(small, smallMask, FillRadius);
        using var full = Resize.To(filled, source.Size, Interpolation.Linear);
        var result = source.Clone();
        full.CopyTo(result, mask);
        return result;
    }
}
