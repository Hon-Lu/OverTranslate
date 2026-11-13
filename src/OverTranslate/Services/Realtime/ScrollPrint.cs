using System.Drawing;
using System.Runtime.InteropServices;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// How far a picture moved between two polls, as a whole, along one axis.
/// </summary>
/// <param name="Dx">Pixels to the right; zero for a move up or down.</param>
/// <param name="Dy">Pixels down; zero for a move left or right.</param>
/// <param name="Share">
/// How much of what could be compared moved by exactly this much: of the lines of the earlier
/// picture that say anything and are still in frame after the move, the share found unchanged at
/// their new place.
/// </param>
/// <param name="Lanes">Which lanes of the print moved with it — see <see cref="ScrollPrint"/>.</param>
internal sealed record ScrollShift(int Dx, int Dy, double Share, IReadOnlyList<bool> Lanes)
{
    /// <summary>Whether the move is up or down, as opposed to left or right.</summary>
    public bool Vertical => Dx == 0;

    public int Distance => Math.Abs(Dx) + Math.Abs(Dy);
}

/// <summary>
/// A captured frame reduced to what is needed to tell that its content has been moved rather than
/// changed: one mark for every row of pixels and one for every column, several times over.
/// </summary>
/// <remarks>
/// <para>A page being scrolled is the one kind of change in which nothing on the screen is new. The
/// pixels are the ones that were there a moment ago, a few rows further up — and that is something
/// which can be known exactly and for very little, where every other question this feature asks of
/// a frame is a judgement. A mark here is a hash of a row's pixels, so two rows carry the same mark
/// only when they are the same pixels; a picture that moved shows up as most of its marks found
/// again at one constant distance from where they were, and nothing else does that. A video does
/// not, however its camera pans, because no two frames of one are the same pixels anywhere. A line
/// of dialogue replaced does not, nor a line typed out, nor a cursor: those are new pixels.</para>
///
/// <para>The picture is cut into lanes and each row marked once per lane, for two reasons. A page
/// rarely fills the block drawn round it — there is a side bar, a header, a window frame — and a
/// row that includes any of that is not the same row after the page moves. And the lanes are how a
/// place in the picture is asked about on its own: see <see cref="Moved"/>. Columns are marked the
/// same way across lanes stacked down the picture, for content that moves sideways.</para>
///
/// <para>A row of one flat colour is given no mark at all. It is the same as every other such row,
/// so it would be found again at every distance at once.</para>
/// </remarks>
internal sealed class ScrollPrint
{
    // Lanes are what a row is cut into. Enough of them that a page taking up a part of the block
    // has several to itself, and each still wide enough that a row of text across one is unlike
    // the rows above and below it.
    private const int MaxLanes = 16;
    private const int MinLaneSize = 48;

    // Pixels read per row of a lane. A scroll moves all of them or none, so reading every one
    // would only say the same thing for more.
    private const int SamplesPerLane = 24;

    // The fewest marks that may speak for a lane or for a box. Below this a coincidence is a
    // sizeable share of the evidence.
    private const int MinMarks = 4;

    /// <summary>
    /// The share of a picture's marks that have to be found again at one distance before that
    /// distance is offered as a move at all.
    /// </summary>
    /// <remarks>
    /// Low on purpose. What scrolls is often a part of the block — a chat panel beside a game, a
    /// page under a fixed header — and the question of whether one particular translation moved
    /// is not answered here but against its own pixels, by <see cref="TextAnchor"/>.
    /// </remarks>
    internal const double MinShare = 0.12;

    private const int MinMovedMarks = 12;

    // What part of a lane's marks, or of the lanes under a box, has to agree.
    private const double LaneAgreement = 0.5;
    private const double BoxAgreement = 0.6;

    private readonly uint[] _rows;
    private readonly uint[] _columns;
    private readonly int _rowLanes;
    private readonly int _columnLanes;

    private ScrollPrint(int width, int height, uint[] rows, int rowLanes, uint[] columns, int columnLanes)
    {
        Width = width;
        Height = height;
        _rows = rows;
        _rowLanes = rowLanes;
        _columns = columns;
        _columnLanes = columnLanes;
    }

    public int Width { get; }

    public int Height { get; }

    /// <param name="previous">
    /// The print of the frame before, if there is one. Handed back as it is when this frame's rows
    /// are that frame's rows: the picture has not changed, and marking its columns again would be
    /// most of the work of looking at it, at every poll of a picture that is standing still.
    /// </param>
    public static unsafe ScrollPrint Capture(FramePixels frame, ScrollPrint? previous = null)
    {
        int width = frame.Width, height = frame.Height;
        var rowLanes = LaneCount(width);
        var columnLanes = LaneCount(height);
        var rows = new uint[rowLanes * height];

        fixed (uint* marks = rows)
        {
            for (var y = 0; y < height; y++)
            {
                var row = frame.Row(y);
                for (var lane = 0; lane < rowLanes; lane++)
                {
                    int from = lane * width / rowLanes, to = (lane + 1) * width / rowLanes;
                    var step = Math.Max(1, (to - from) / SamplesPerLane);
                    var first = row[from] & 0xFFFFFF;
                    uint mark = 0, differs = 0;
                    for (var x = from; x < to; x += step)
                    {
                        var pixel = row[x] & 0xFFFFFF;
                        differs |= pixel ^ first;
                        mark = Mix(mark, pixel);
                    }
                    marks[lane * height + y] = differs == 0 ? 0 : NonZero(mark);
                }
            }
        }

        if (previous is not null && previous.Width == width && previous.Height == height &&
            rows.AsSpan().SequenceEqual(previous._rows))
            return previous;

        // Row by row for the columns too, so the frame is still read in the order it is stored.
        var columns = new uint[columnLanes * width];
        var firsts = new uint[width];
        var differ = new uint[width];
        fixed (uint* marks = columns, start = firsts, changed = differ)
        {
            for (var lane = 0; lane < columnLanes; lane++)
            {
                int from = lane * height / columnLanes, to = (lane + 1) * height / columnLanes;
                var step = Math.Max(1, (to - from) / SamplesPerLane);
                var column = marks + lane * width;
                new Span<uint>(changed, width).Clear();
                new ReadOnlySpan<uint>(frame.Row(from), width).CopyTo(new Span<uint>(start, width));
                for (var y = from; y < to; y += step)
                {
                    var row = frame.Row(y);
                    for (var x = 0; x < width; x++)
                    {
                        var pixel = row[x] & 0xFFFFFF;
                        changed[x] |= pixel ^ (start[x] & 0xFFFFFF);
                        column[x] = Mix(column[x], pixel);
                    }
                }
                for (var x = 0; x < width; x++)
                    column[x] = changed[x] == 0 ? 0 : NonZero(column[x]);
            }
        }

        return new ScrollPrint(width, height, rows, rowLanes, columns, columnLanes);
    }

    private static int LaneCount(int size) => Math.Clamp(size / MinLaneSize, 1, MaxLanes);

    private static uint Mix(uint mark, uint pixel)
    {
        mark = (mark + pixel) * 2654435761u;
        return mark ^ (mark >> 15);
    }

    private static uint NonZero(uint mark) => mark == 0 ? 1 : mark;

    /// <summary>Whether this is the same picture, mark for mark.</summary>
    public bool SameAs(ScrollPrint? other) =>
        ReferenceEquals(this, other) || other is not null && other.Width == Width && other.Height == Height &&
        _rows.AsSpan().SequenceEqual(other._rows) && _columns.AsSpan().SequenceEqual(other._columns);

    /// <summary>
    /// The moves that explain how <paramref name="before"/> became <paramref name="after"/>: at most
    /// one up or down and one sideways, the stronger first; none when the picture did not move, or
    /// changed in some other way.
    /// </summary>
    public static IReadOnlyList<ScrollShift> FindShifts(ScrollPrint before, ScrollPrint after)
    {
        if (before.Width != after.Width || before.Height != after.Height) return [];

        var down = FindShift(before._rows, after._rows, before._rowLanes, before.Height);
        var across = FindShift(before._columns, after._columns, before._columnLanes, before.Width);

        var shifts = new List<ScrollShift>(2);
        if (down is { } y) shifts.Add(new ScrollShift(0, y.Distance, y.Share, y.Lanes));
        if (across is { } x) shifts.Add(new ScrollShift(x.Distance, 0, x.Share, x.Lanes));
        if (shifts.Count == 2 && shifts[1].Share > shifts[0].Share) shifts.Reverse();
        return shifts;
    }

    private static (int Distance, double Share, bool[] Lanes)? FindShift(
        uint[] before, uint[] after, int lanes, int length)
    {
        // Each mark that is the only one of its kind in its lane, before and after, votes for the
        // distance between its two places. Marks that repeat — the rows down the side of a frame,
        // a tiled background — would vote for every distance between any two of them.
        var votes = new Dictionary<int, int>();
        var places = new Dictionary<uint, int>(length);
        var seen = new Dictionary<uint, int>(length);
        for (var lane = 0; lane < lanes; lane++)
        {
            Index(before.AsSpan(lane * length, length), places);
            Index(after.AsSpan(lane * length, length), seen);
            foreach (var (mark, place) in seen)
            {
                if (place < 0 || !places.TryGetValue(mark, out var was) || was < 0) continue;
                CollectionsMarshal.GetValueRefOrAddDefault(votes, place - was, out _)++;
            }
        }

        int distance = 0, most = 0;
        foreach (var (candidate, count) in votes)
        {
            if (candidate == 0 || count <= most) continue;
            distance = candidate;
            most = count;
        }
        if (most < MinMovedMarks) return null;

        // The votes only nominate. What decides is every mark that says anything, repeated or
        // not: whether the one that was here is the one that is there now.
        var moved = new bool[lanes];
        int compared = 0, agreed = 0;
        for (var lane = 0; lane < lanes; lane++)
        {
            var (laneCompared, laneAgreed) = Compare(before, after, lane, length, 0, length, distance);
            compared += laneCompared;
            agreed += laneAgreed;
            moved[lane] = laneCompared >= MinMarks && laneAgreed >= laneCompared * LaneAgreement;
        }

        var share = compared == 0 ? 0 : (double)agreed / compared;
        return share >= MinShare ? (distance, share, moved) : null;
    }

    // Each mark's place in its lane, or -1 for one that is there more than once.
    private static void Index(ReadOnlySpan<uint> marks, Dictionary<uint, int> places)
    {
        places.Clear();
        for (var i = 0; i < marks.Length; i++)
        {
            if (marks[i] == 0) continue;
            ref var place = ref CollectionsMarshal.GetValueRefOrAddDefault(places, marks[i], out var known);
            place = known ? -1 : i;
        }
    }

    // Of the marks one lane had between two places, how many are found again `distance` further on.
    private static (int Compared, int Agreed) Compare(
        uint[] before, uint[] after, int lane, int length, int from, int to, int distance)
    {
        int compared = 0, agreed = 0;
        from = Math.Max(Math.Max(from, 0), -distance);
        to = Math.Min(Math.Min(to, length), length - distance);
        for (var i = from; i < to; i++)
        {
            var mark = before[lane * length + i];
            if (mark == 0) continue;
            compared++;
            if (after[lane * length + i + distance] == mark) agreed++;
        }
        return (compared, agreed);
    }

    /// <summary>
    /// Whether what was inside <paramref name="box"/> in <paramref name="before"/> is found, mark
    /// for mark, <paramref name="shift"/> away in <paramref name="after"/> — judged over the part
    /// of the box that is still in frame after the move.
    /// </summary>
    public static bool Moved(ScrollPrint before, ScrollPrint after, Rectangle box, ScrollShift shift)
    {
        if (before.Width != after.Width || before.Height != after.Height) return false;

        double judged = 0, agreed = 0;
        foreach (var (lane, weight) in before.LanesUnder(box, shift.Vertical))
        {
            var (compared, same) = shift.Vertical
                ? Compare(before._rows, after._rows, lane, before.Height, box.Top, box.Bottom, shift.Dy)
                : Compare(before._columns, after._columns, lane, before.Width, box.Left, box.Right, shift.Dx);
            if (compared < MinMarks) continue;
            judged += weight;
            if (same >= compared * 0.9) agreed += weight;
        }
        return judged > 0 && agreed >= judged * BoxAgreement;
    }

    /// <summary>
    /// Whether the part of the picture <paramref name="box"/> sits in went with
    /// <paramref name="shift"/> — the lanes it lies across, not the box itself, so this can be
    /// asked of a box the move has carried out of frame.
    /// </summary>
    public bool CarriedBy(ScrollShift shift, Rectangle box)
    {
        double all = 0, moved = 0;
        foreach (var (lane, weight) in LanesUnder(box, shift.Vertical))
        {
            all += weight;
            if (lane < shift.Lanes.Count && shift.Lanes[lane]) moved += weight;
        }
        return all > 0 && moved >= all * BoxAgreement;
    }

    // The lanes a box lies across, each with how much of it they hold. Row lanes are cut across
    // the width, so a move up or down is asked of the lanes the box spans from left to right.
    private IEnumerable<(int Lane, int Weight)> LanesUnder(Rectangle box, bool vertical)
    {
        var (lanes, size, from, to) = vertical
            ? (_rowLanes, Width, box.Left, box.Right)
            : (_columnLanes, Height, box.Top, box.Bottom);
        for (var lane = 0; lane < lanes; lane++)
        {
            var overlap = Math.Min(to, (lane + 1) * size / lanes) - Math.Max(from, lane * size / lanes);
            if (overlap > 0) yield return (lane, overlap);
        }
    }
}
