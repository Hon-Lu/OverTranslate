using System.Drawing;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// One block as 對照顯示 sees it: the rectangle being read, which way its text runs, and the offset
/// the user dragged its compare box to — null when they never did.
/// </summary>
public readonly record struct RealtimeCompareSlot(
    Rectangle Bounds, RealtimeTextOrientation Orientation, Point? Offset = null);

/// <summary>
/// Where 對照顯示 puts each block's translation: a copy of the block, the same size, moved beside it
/// so the source stays readable.
/// </summary>
/// <remarks>
/// <para>The compare box is the block itself displaced, not a box of its own. Everything the
/// overlay already knows about laying a translation over its source — the band, the wrapped panel,
/// the vertical grid — then works unchanged, just drawn somewhere else; a box sized to the
/// translation instead would need every one of those layouts rewritten for a shape the source never
/// had.</para>
///
/// <para>Beside it in the direction the eye goes next. Horizontal text is read in rows, and a row
/// above is where a subtitle's second language traditionally sits, so above first and below when
/// there is no room. Vertical text is read in columns from right to left, so the next column — the
/// left — first and the right second. Whatever is left is tried after that, in one fixed order, so
/// two sittings over the same layout land in the same place.</para>
///
/// <para>"Room" is the hard part: the box has to lie entirely on the session's one screen and must
/// not cover any block being read — its own included, which is the whole point — because covering a
/// block puts the translation back over source text. Covering another block's compare box only
/// stacks one translation on another, so that is avoided where it can be and accepted where it
/// cannot. When nothing fits, the first choice is pulled onto the screen and left overlapping; the
/// user can drag it, and a box placed somewhere visible is a better starting point than one placed
/// nowhere.</para>
///
/// <para>Pure, and in physical pixels, so it can be tested without a window and so the edit layer
/// and the running overlays reach the same answer from the same inputs.</para>
/// </remarks>
public static class RealtimeComparePlacement
{
    /// <summary>
    /// Space left between a block and its compare box, in physical pixels. Enough that the two
    /// outlines read as two things in the edit layer, small enough that the translation still reads
    /// as belonging to the line next to it.
    /// </summary>
    public const int DefaultGap = 6;

    private enum Side { Above, Below, Left, Right }

    private static readonly Side[] HorizontalOrder = [Side.Above, Side.Below, Side.Left, Side.Right];
    private static readonly Side[] VerticalOrder = [Side.Left, Side.Right, Side.Above, Side.Below];

    /// <summary>
    /// The offset of every block's compare box from the block, in the same order as
    /// <paramref name="slots"/>.
    /// </summary>
    /// <remarks>
    /// A slot that carries an offset keeps it — the user put it there — and is only pulled back onto
    /// the screen if moving the block has pushed it off. Those go first, so the boxes placed
    /// automatically can avoid them; the automatic ones follow in order and avoid each other too.
    /// </remarks>
    public static IReadOnlyList<Point> Place(
        IReadOnlyList<RealtimeCompareSlot> slots, Rectangle screen, int gap = DefaultGap)
    {
        var result = new Point[slots.Count];
        var placed = new List<Rectangle>(slots.Count);
        var blocks = slots.Select(slot => slot.Bounds).ToArray();

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Offset is not { } offset) continue;
            var box = ClampInto(Displace(slots[i].Bounds, offset), screen);
            result[i] = new Point(box.X - slots[i].Bounds.X, box.Y - slots[i].Bounds.Y);
            placed.Add(box);
        }

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Offset is not null) continue;
            var box = Automatic(slots[i], blocks, placed, screen, gap);
            result[i] = new Point(box.X - slots[i].Bounds.X, box.Y - slots[i].Bounds.Y);
            placed.Add(box);
        }

        return result;
    }

    /// <summary>The compare box a block gets from <see cref="Place"/> before it is ever dragged.</summary>
    private static Rectangle Automatic(
        RealtimeCompareSlot slot, Rectangle[] blocks, List<Rectangle> placed, Rectangle screen, int gap)
    {
        var order = slot.Orientation == RealtimeTextOrientation.Vertical ? VerticalOrder : HorizontalOrder;
        var candidates = order.Select(side => Beside(slot.Bounds, side, gap)).ToArray();

        // Clear of everything first, then clear of what matters: the blocks and the screen edge.
        foreach (var candidate in candidates)
            if (Fits(candidate, blocks, screen) && !placed.Any(other => Overlaps(candidate, other)))
                return candidate;

        foreach (var candidate in candidates)
            if (Fits(candidate, blocks, screen))
                return candidate;

        return ClampInto(candidates[0], screen);
    }

    private static Rectangle Beside(Rectangle block, Side side, int gap) => side switch
    {
        Side.Above => Displace(block, new Point(0, -(block.Height + gap))),
        Side.Below => Displace(block, new Point(0, block.Height + gap)),
        Side.Left => Displace(block, new Point(-(block.Width + gap), 0)),
        _ => Displace(block, new Point(block.Width + gap, 0)),
    };

    private static bool Fits(Rectangle box, Rectangle[] blocks, Rectangle screen) =>
        screen.Contains(box) && !blocks.Any(block => Overlaps(box, block));

    /// <summary>
    /// Whether two rectangles share any area. Touching edges do not count — a compare box laid
    /// flush against a block covers none of it.
    /// </summary>
    public static bool Overlaps(Rectangle a, Rectangle b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>The block moved by an offset, the same size.</summary>
    public static Rectangle Displace(Rectangle block, Point offset) =>
        new(block.X + offset.X, block.Y + offset.Y, block.Width, block.Height);

    /// <summary>
    /// Pulls a box onto the screen, keeping its size. A box larger than the screen is pinned to the
    /// screen's top-left corner, which only happens for a block drawn across the whole screen — and
    /// that one has nowhere beside it to go anyway.
    /// </summary>
    public static Rectangle ClampInto(Rectangle box, Rectangle screen) => new(
        Math.Clamp(box.X, screen.Left, Math.Max(screen.Left, screen.Right - box.Width)),
        Math.Clamp(box.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - box.Height)),
        box.Width, box.Height);

    /// <summary>
    /// Where a compare box being dragged ends up: moved as far as the pointer asks, kept on the
    /// screen, and free to go over any block — the user's own included.
    /// </summary>
    /// <remarks>
    /// <para>Not stopped at blocks any more. It was, on the reasoning that a copy over a block puts
    /// the translation back over source text; but that is the user's call to make, not a mistake to
    /// prevent. Someone comparing a single word may well want the translation half over its line,
    /// and a box that refused to go there read as the edit layer being stuck. It costs recognition
    /// nothing: the overlays are left out of every frame the session reads, wherever they are.
    /// Only the automatic placement keeps clear of the blocks — see <see cref="Place"/> — because
    /// that one is the program choosing, and it should choose somewhere the source stays readable.</para>
    ///
    /// <para>In the edit layer's own units rather than physical pixels: the drag arrives as fractions
    /// of a device-independent pixel, and rounding each step would stall a slow drag entirely.</para>
    /// </remarks>
    public static System.Windows.Rect Drag(
        System.Windows.Rect current, System.Windows.Vector delta, System.Windows.Rect screen) =>
        ClampInto(new System.Windows.Rect(
            current.X + delta.X, current.Y + delta.Y, current.Width, current.Height), screen);

    /// <summary>
    /// Where a dragged compare box goes when its block is resized: wherever keeps it in the same
    /// relation to the block, worked out for each axis on its own.
    /// </summary>
    /// <remarks>
    /// <para>The box is the block's size by definition, so a resize changes both — and a fixed
    /// offset from the top-left corner, which is what moving the block uses, is the wrong thing to
    /// keep. A copy parked above a subtitle stays at the same offset while the subtitle is pulled
    /// taller at the bottom only by luck; pulled taller at the top, or with the copy below or to the
    /// left, the copy grows into the block or drifts away from it.</para>
    ///
    /// <para>So on each axis: a box wholly on one side of the block keeps its gap to that side —
    /// above stays the same distance above the top edge, left the same distance left of the left
    /// edge, and so on. A box that overlaps the block on that axis has no side to keep, and keeps its
    /// offset from the block's top-left corner instead, which is what the user last set. Either way
    /// the result is held on the screen.</para>
    /// </remarks>
    public static System.Windows.Rect Resized(
        System.Windows.Rect oldBlock, System.Windows.Rect oldBox, System.Windows.Rect newBlock,
        System.Windows.Rect screen)
    {
        double x = Along(oldBlock.Left, oldBlock.Right, oldBox.Left, oldBox.Right,
            newBlock.Left, newBlock.Right, newBlock.Width);
        double y = Along(oldBlock.Top, oldBlock.Bottom, oldBox.Top, oldBox.Bottom,
            newBlock.Top, newBlock.Bottom, newBlock.Height);
        return ClampInto(new System.Windows.Rect(x, y, newBlock.Width, newBlock.Height), screen);

        // One axis: the box's new near edge, from where it stood against the block before.
        static double Along(
            double blockStart, double blockEnd, double boxStart, double boxEnd,
            double newStart, double newEnd, double newSize)
        {
            if (boxEnd <= blockStart + Epsilon) return newStart - (blockStart - boxEnd) - newSize;
            if (boxStart >= blockEnd - Epsilon) return newEnd + (boxStart - blockEnd);
            return newStart + (boxStart - blockStart);
        }
    }

    private static System.Windows.Rect ClampInto(System.Windows.Rect box, System.Windows.Rect screen) => new(
        Math.Clamp(box.X, screen.Left, Math.Max(screen.Left, screen.Right - box.Width)),
        Math.Clamp(box.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - box.Height)),
        box.Width, box.Height);

    // Flush in floating point is a hair either side.
    private const double Epsilon = 0.01;
}
