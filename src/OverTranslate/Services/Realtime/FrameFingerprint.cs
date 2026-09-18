using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// A coarse, noise-tolerant summary of a captured area — or of just the strips of it holding text —
/// used to decide whether anything worth recognising has changed since the last poll.
/// </summary>
/// <remarks>
/// This replaced an exact hash, which was the right shape and the wrong sensitivity. Real screen
/// content is never bit-identical between two frames: video carries compression noise, subpixel
/// antialiasing shifts as things move behind text, and a gradient dithers differently every repaint.
/// An exact hash calls all of that a change, so recognition ran continuously over content whose
/// words had not moved at all — measured at an 85% duty cycle on one region, for nothing.
///
/// So the area is reduced to a grid of average brightness cells and compared with a tolerance twice
/// over: a cell has to shift by more than <see cref="CellTolerance"/> to count at all, and enough
/// cells have to do so before the frame counts as changed. Noise moves every cell a little and fails
/// the first test; a line of text changing moves a fifth of them a lot and passes both.
/// </remarks>
internal sealed class FrameFingerprint
{
    // Cell counts, not pixel sizes: an area is summarised at the same resolution whatever its size,
    // so the comparison thresholds below mean the same thing for a 200px strip and a 1200px one.
    private const int CellsX = 32;
    private const int TextBandCellsY = 8;
    private const int FullAreaCellsY = 16;

    // Every second pixel in each direction. The cell averages barely move for a finer step, and this
    // runs several times a second on every watched region.
    private const int SampleStep = 2;

    /// <summary>
    /// How far one cell's brightness (0–255) may drift before it counts as changed. Sized above
    /// compression noise and antialiasing, well below the contrast between glyphs and their
    /// background.
    /// </summary>
    /// <remarks>
    /// Raised from 12 after measuring what each kind of change actually produces. At 12 a uniform
    /// 16-level brightening of the band — a scene getting lighter behind an unchanged subtitle —
    /// moved 84.8% of the cells, a bigger signal than replacing the subtitle itself, and the loop
    /// duly recognised the same words again. It showed: over a whole live session 48% of the reads
    /// that followed a gap under half a second came back identical to what was already on screen.
    ///
    /// Measured shares of cells moved, over a 1226x196 band:
    ///
    /// <code>
    ///                              tolerance 12   16     24
    ///   background +16 levels           84.8%    0.0%   0.0%
    ///   subtitle replaced, same length  11.3%   10.2%   6.3%
    ///   subtitle replaced, long line    25.0%   21.5%  18.4%
    ///   subtitle disappears             14.5%   14.5%  13.7%
    /// </code>
    ///
    /// 16 removes the drift entirely while every real change still clears
    /// <see cref="ChangedCellPercent"/> two to four times over. Going further closes that margin —
    /// at 24 a same-length replacement is down to 6.3% against a 5% bar — for nothing that 16 has
    /// not already dealt with.
    /// </remarks>
    private const int CellTolerance = 16;

    /// <summary>
    /// What share of cells must have changed before the frame has. A single cell over the tolerance
    /// is a glint or a cursor; a line of text changing takes a good fraction of the grid with it.
    /// </summary>
    internal const int ChangedCellPercent = 5;

    private readonly byte[] _cells;

    internal FrameFingerprint(byte[] cells) => _cells = cells;

    /// <param name="areas">
    /// Sub-rectangles to summarise, in bitmap coordinates; null or empty summarises the whole bitmap.
    /// </param>
    public static FrameFingerprint Capture(Bitmap bitmap, IReadOnlyList<Rectangle>? areas)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var frame = new Rectangle(0, 0, data.Width, data.Height);

            if (areas is null || areas.Count == 0)
                return new FrameFingerprint(Summarise(data, frame, FullAreaCellsY));

            var cells = new List<byte>(areas.Count * CellsX * TextBandCellsY);
            foreach (var area in areas)
            {
                var clipped = Rectangle.Intersect(area, frame);
                // Skipped rather than zero-filled: a band that has moved off the region entirely
                // changes the fingerprint's length, which Differs already treats as a change.
                if (clipped.Width <= 0 || clipped.Height <= 0) continue;
                cells.AddRange(Summarise(data, clipped, TextBandCellsY));
            }

            return new FrameFingerprint([.. cells]);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Whether <paramref name="other"/> shows something meaningfully different. A null or
    /// differently shaped counterpart counts as changed — there is nothing to compare against, and
    /// treating that as "unchanged" would strand the region.
    /// </summary>
    /// <remarks>
    /// The share is taken over everything compared, so this only means what it says when the area
    /// compared is the area the change is expected to fill — the text strips. Over a whole watched
    /// region it understates a line of text by the ratio of the block to the line; see
    /// <see cref="DiffersLocally"/>, which is what the two whole-region comparisons use.
    /// </remarks>
    public bool Differs(FrameFingerprint? other)
    {
        if (other is null || other._cells.Length != _cells.Length) return true;
        if (_cells.Length == 0) return false;

        int changed = 0;
        for (int i = 0; i < _cells.Length; i++)
            if (Math.Abs(_cells[i] - other._cells[i]) > CellTolerance)
                changed++;

        return changed * 100 > _cells.Length * ChangedCellPercent;
    }

    /// <summary>
    /// The same question as <see cref="Differs"/>, asked of an area whose size has nothing to do
    /// with the size of the thing being looked for: the share is taken over the busiest
    /// <see cref="LocalWindowRows"/> rows of the grid rather than over every cell compared.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Differs"/> divides by everything it compared, which is right for the text
    /// strips — those <em>are</em> the text, so a line changing moves a good fraction of them
    /// (measured at 16–20%, against a 5% bar). It is wrong for a whole watched region, because the
    /// denominator there is whatever rectangle the user happened to draw: the same subtitle-sized
    /// change is divided by a whole dialogue box, and the bigger the box the smaller it looks.</para>
    ///
    /// <para>Measured over rendered dialogue boxes, comparing the whole region — which is what the
    /// search path and the full rescan both do:</para>
    ///
    /// <code>
    ///                                                    whole region   busiest 2 rows
    ///   10 characters appearing in an empty 1200x300 box         2.3%            18.8%
    ///    3 characters appearing in an empty 1200x300 box         0.8%             6.3%
    ///   20 characters appearing in an empty 1200x300 box         5.9%            46.9%
    ///   "そうだね、行こうか。" -> "うん、わかった。"              1.0%             7.8%
    ///   "ずっと前から言おうと…" -> "やっぱり何でもない。"         3.3%            26.6%
    /// </code>
    ///
    /// <para>Every one of those but the twenty-character line is under the 5% bar as a share of the
    /// whole region, which is to say invisible — and invisible here is not "late", it is never: the
    /// picture does not change again on its own, so neither does the answer. That is the reported
    /// bug, a dialogue game whose next line never arrived until the user paused and resumed.
    /// Measured against the rows it actually falls in, the same change clears the bar three to nine
    /// times over.</para>
    ///
    /// <para>What it costs is sensitivity to things that are not text — a health bar moving, an
    /// animated arrow — which now report a change the whole-region share used to swallow. That is
    /// bearable precisely here and nowhere else: both callers answer this question with
    /// <see cref="RealtimeGate"/>, a detection at a third of the size, and a frame the gate turns
    /// away is recorded as looked at. The strips keep <see cref="Differs"/>, where a false positive
    /// is a full recognition and the denominator is already the right one.</para>
    /// </remarks>
    public bool DiffersLocally(FrameFingerprint? other)
    {
        if (other is null || other._cells.Length != _cells.Length) return true;
        if (_cells.Length == 0) return false;

        return MaxLocalChangedShare(other, CellTolerance) * 100 > ChangedCellPercent;
    }

    /// <summary>
    /// How many rows of the grid the busiest window spans. A line of text is a horizontal thing, so
    /// what it moves is a band of rows rather than a share of the picture.
    /// </summary>
    /// <remarks>
    /// Two rows out of the sixteen a whole area is summarised at, so an eighth of the region's
    /// height — about a line of text in a block drawn a few lines tall, and by the table above the
    /// difference between one row and two is nothing these cases care about. Two rather than one so
    /// a single flickering cell cannot carry a window on its own: 2 cells of 64 is under the bar
    /// where 2 of 32 would be over it.
    /// </remarks>
    internal const int LocalWindowRows = 2;

    /// <summary>
    /// The largest share of changed cells in any window of <see cref="LocalWindowRows"/> consecutive
    /// grid rows. Exposed for the same reason as <see cref="ChangedShare"/>: so the bar can be
    /// chosen against measured margins rather than argued about.
    /// </summary>
    internal double MaxLocalChangedShare(FrameFingerprint? other, int tolerance)
    {
        if (other is null || other._cells.Length != _cells.Length || _cells.Length == 0) return 1;

        var rows = (_cells.Length + CellsX - 1) / CellsX;
        if (rows <= LocalWindowRows) return ChangedShare(other, tolerance);

        var changedInRow = new int[rows];
        var cellsInRow = new int[rows];
        for (var i = 0; i < _cells.Length; i++)
        {
            cellsInRow[i / CellsX]++;
            if (Math.Abs(_cells[i] - other._cells[i]) > tolerance) changedInRow[i / CellsX]++;
        }

        double best = 0;
        for (var start = 0; start + LocalWindowRows <= rows; start++)
        {
            int changed = 0, cells = 0;
            for (var row = start; row < start + LocalWindowRows; row++)
            {
                changed += changedInRow[row];
                cells += cellsInRow[row];
            }

            if (cells > 0) best = Math.Max(best, (double)changed / cells);
        }

        return best;
    }

    /// <summary>
    /// Whether this is, cell for cell, the picture <paramref name="other"/> summarises — nothing
    /// moved anywhere, not even by less than the bar the comparisons above have to clear.
    /// </summary>
    /// <remarks>
    /// Asked by the idle scan in <see cref="RealtimeRegionState"/>, which reads a region again when
    /// too long has passed without one. What it separates is "nothing I can see changed" from
    /// "nothing changed": the first deserves another look, because a change under the bar is still a
    /// change, and the second cannot possibly hold anything new.
    /// </remarks>
    public bool IsIdenticalTo(FrameFingerprint? other) =>
        other is not null &&
        _cells.Length == other._cells.Length &&
        ChangedShare(other, CellTolerance) == 0;

    /// <summary>
    /// Whether a picture drawn from <paramref name="other"/> would still look right, as opposed to
    /// whether the words in it have changed.
    /// </summary>
    /// <remarks>
    /// A different question from <see cref="Differs"/>, and deliberately a stricter one.
    /// <see cref="Differs"/> asks whether recognition would read something new, and
    /// <see cref="CellTolerance"/> is tuned to say no when a scene merely brightens behind an
    /// unchanged subtitle — the measured table above is that decision. For a repaired background the
    /// same drift is exactly what matters: the patch was interpolated from pixels that have since
    /// moved, so keeping it leaves a rectangle of the old shade sitting in the new scene.
    ///
    /// Hence its own pair of thresholds. They are reasoned rather than measured, unlike the ones
    /// above: 4 levels is above the dithering and compression noise a still picture produces and far
    /// below anything a reader can see, and one cell in a hundred is enough to catch a change that
    /// touches only part of the band.
    /// </remarks>
    public bool StillLooksLike(FrameFingerprint? other) =>
        other is not null &&
        _cells.Length == other._cells.Length &&
        ChangedShare(other, RepaintTolerance) <= RepaintChangedShare;

    /// <inheritdoc cref="StillLooksLike"/>
    private const int RepaintTolerance = 4;

    /// <inheritdoc cref="StillLooksLike"/>
    private const double RepaintChangedShare = 0.01;

    /// <summary>
    /// The share of cells that moved by more than <paramref name="tolerance"/>, which is the number
    /// <see cref="Differs"/> compares against <see cref="ChangedCellPercent"/>. Exposed so the two
    /// thresholds can be chosen against measured margins rather than argued about.
    /// </summary>
    internal double ChangedShare(FrameFingerprint? other, int tolerance)
    {
        if (other is null || other._cells.Length != _cells.Length || _cells.Length == 0) return 1;

        var changed = 0;
        for (var i = 0; i < _cells.Length; i++)
            if (Math.Abs(_cells[i] - other._cells[i]) > tolerance)
                changed++;

        return (double)changed / _cells.Length;
    }

    private static byte[] Summarise(BitmapData data, Rectangle area, int cellsY)
    {
        var totals = new long[CellsX * cellsY];
        var counts = new int[CellsX * cellsY];

        for (int y = area.Top; y < area.Bottom; y += SampleStep)
        {
            int cellY = (y - area.Top) * cellsY / area.Height;
            nint row = data.Scan0 + y * data.Stride;

            for (int x = area.Left; x < area.Right; x += SampleStep)
            {
                int cellX = (x - area.Left) * CellsX / area.Width;
                int value = Marshal.ReadInt32(row, x * 4);

                // Perceived brightness rather than the raw channels: it is one number instead of
                // three, and it is the axis text actually separates itself from its background on.
                int b = value & 0xFF;
                int g = (value >> 8) & 0xFF;
                int r = (value >> 16) & 0xFF;
                int luminance = (r * 299 + g * 587 + b * 114) / 1000;

                int cell = cellY * CellsX + cellX;
                totals[cell] += luminance;
                counts[cell]++;
            }
        }

        var cells = new byte[totals.Length];
        for (int i = 0; i < totals.Length; i++)
            cells[i] = counts[i] == 0 ? (byte)0 : (byte)(totals[i] / counts[i]);

        return cells;
    }
}
