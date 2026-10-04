using System.Drawing;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// One block as 對照顯示 sees it: the rectangle being read, which way its text runs, the offset the
/// user dragged its compare box to — null when they never did — how large they made it, as a
/// fraction of the block (see <see cref="RealtimeBlockPlacement.CompareScale"/>), and whether they
/// turned its compare box off (see <see cref="RealtimeBlockPlacement.CompareHidden"/>).
/// </summary>
public readonly record struct RealtimeCompareSlot(
    Rectangle Bounds, RealtimeTextOrientation Orientation, Point? Offset = null, double Scale = 1.0,
    bool Hidden = false);

/// <summary>
/// Where 對照顯示 puts each block's translation: a copy of the block, moved beside it so the source
/// stays readable — the same size, unless the user has shrunk it.
/// </summary>
/// <remarks>
/// <para>The compare box is the block itself displaced, not a box of its own. Everything the
/// overlay already knows about laying a translation over its source — the band, the wrapped panel,
/// the vertical grid — then works unchanged, just drawn somewhere else; a box sized to the
/// translation instead would need every one of those layouts rewritten for a shape the source never
/// had.</para>
///
/// <para>Shrinking it keeps that true. The box only ever changes size in proportion, and the
/// overlay lays the translation out exactly as it would at the block's size and then scales the
/// whole of it — so a smaller copy is the same picture, smaller, rather than a new layout.</para>
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

    /// <summary>
    /// The smallest a compare box can be made, as a fraction of its block. Below half, a translation
    /// set at the size picked to match the source stops being readable. There is no upper end above
    /// 1.0: a copy larger than its block would be a new layout, not a copy.
    /// </summary>
    public const double MinScale = 0.5;

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
    ///
    /// A hidden slot gets no box and takes no room — its entry in the result means nothing — but its
    /// block is still kept clear of, because its source is still being read.
    /// </remarks>
    public static IReadOnlyList<Point> Place(
        IReadOnlyList<RealtimeCompareSlot> slots, Rectangle screen, int gap = DefaultGap)
    {
        var result = new Point[slots.Count];
        var placed = new List<Rectangle>(slots.Count);
        var blocks = slots.Select(slot => slot.Bounds).ToArray();

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Hidden || slots[i].Offset is not { } offset) continue;
            var box = ClampInto(Displace(slots[i].Bounds, offset, slots[i].Scale), screen);
            result[i] = new Point(box.X - slots[i].Bounds.X, box.Y - slots[i].Bounds.Y);
            placed.Add(box);
        }

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Hidden || slots[i].Offset is not null) continue;
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
        var candidates = order.Select(side => Beside(slot.Bounds, slot.Scale, side, gap)).ToArray();

        // Clear of everything first, then clear of what matters: the blocks and the screen edge.
        foreach (var candidate in candidates)
            if (Fits(candidate, blocks, screen) && !placed.Any(other => Overlaps(candidate, other)))
                return candidate;

        foreach (var candidate in candidates)
            if (Fits(candidate, blocks, screen))
                return candidate;

        return ClampInto(candidates[0], screen);
    }

    // Lined up with the block's top-left corner on the axis it is not beside it on.
    private static Rectangle Beside(Rectangle block, double scale, Side side, int gap)
    {
        var size = Scaled(block.Size, scale);
        return side switch
        {
            Side.Above => Displace(block, new Point(0, -(size.Height + gap)), scale),
            Side.Below => Displace(block, new Point(0, block.Height + gap), scale),
            Side.Left => Displace(block, new Point(-(size.Width + gap), 0), scale),
            _ => Displace(block, new Point(block.Width + gap, 0), scale),
        };
    }

    private static bool Fits(Rectangle box, Rectangle[] blocks, Rectangle screen) =>
        screen.Contains(box) && !blocks.Any(block => Overlaps(box, block));

    /// <summary>
    /// Whether two rectangles share any area. Touching edges do not count — a compare box laid
    /// flush against a block covers none of it.
    /// </summary>
    public static bool Overlaps(Rectangle a, Rectangle b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>The compare box at an offset from its block: the block's size, times the scale.</summary>
    public static Rectangle Displace(Rectangle block, Point offset, double scale = 1.0)
    {
        var size = Scaled(block.Size, scale);
        return new(block.X + offset.X, block.Y + offset.Y, size.Width, size.Height);
    }

    /// <summary>
    /// A block's size times a compare box's scale, in whole pixels and never empty — exactly the
    /// block's size at 1.0, so a box nobody has shrunk matches its block to the pixel.
    /// </summary>
    public static Size Scaled(Size block, double scale) => new(
        Math.Max(1, (int)Math.Round(block.Width * scale)),
        Math.Max(1, (int)Math.Round(block.Height * scale)));

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
    ///
    /// <para>A shrunk box stays shrunk by the same fraction: its new size is the new block's times
    /// <paramref name="scale"/>, so the translation keeps the proportion to its source the user
    /// chose.</para>
    /// </remarks>
    public static System.Windows.Rect Resized(
        System.Windows.Rect oldBlock, System.Windows.Rect oldBox, System.Windows.Rect newBlock,
        System.Windows.Rect screen, double scale = 1.0)
    {
        double width = newBlock.Width * scale;
        double height = newBlock.Height * scale;
        double x = Along(oldBlock.Left, oldBlock.Right, oldBox.Left, oldBox.Right,
            newBlock.Left, newBlock.Right, width);
        double y = Along(oldBlock.Top, oldBlock.Bottom, oldBox.Top, oldBox.Bottom,
            newBlock.Top, newBlock.Bottom, height);
        return ClampInto(new System.Windows.Rect(x, y, width, height), screen);

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

    /// <summary>
    /// Where a compare box goes, and at what scale, when one of its corners is dragged: shrunk or
    /// grown in proportion about the opposite corner, which stays put.
    /// </summary>
    /// <remarks>
    /// <para>The same feel as resizing a block — the corner pulled moves, the far one does not — but
    /// with the proportion locked, because the box is the block's shape by definition. The pointer's
    /// movement is projected onto the box's diagonal, so a pull that does not quite follow the
    /// diagonal neither stalls nor jumps.</para>
    ///
    /// <para>Held between <see cref="MinScale"/> and 1.0, and to whatever keeps the box on the
    /// screen with the far corner where it is. That last never forces it below the minimum: the box
    /// it starts from is already on the screen, so anything up to its present size fits.</para>
    ///
    /// <para>In the edit layer's own units, like <see cref="Drag"/> and for the same reason.</para>
    /// </remarks>
    /// <param name="corner">0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right.</param>
    public static (System.Windows.Rect Box, double Scale) ScaleFromCorner(
        System.Windows.Rect box, System.Windows.Size block, int corner, System.Windows.Vector delta,
        System.Windows.Rect screen)
    {
        bool movesLeft = corner is 0 or 2;
        bool movesTop = corner is 0 or 1;

        // The corner that stays put, and the box's size if the pointer were followed freely.
        double anchorX = movesLeft ? box.Right : box.Left;
        double anchorY = movesTop ? box.Bottom : box.Top;
        double width = box.Width + (movesLeft ? -delta.X : delta.X);
        double height = box.Height + (movesTop ? -delta.Y : delta.Y);

        // The scale whose box is nearest that one: its projection onto the diagonal.
        double scale = (width * block.Width + height * block.Height)
            / Math.Max(Epsilon, block.Width * block.Width + block.Height * block.Height);

        double fits = Math.Min(
            (movesLeft ? anchorX - screen.Left : screen.Right - anchorX) / Math.Max(Epsilon, block.Width),
            (movesTop ? anchorY - screen.Top : screen.Bottom - anchorY) / Math.Max(Epsilon, block.Height));
        scale = Math.Clamp(scale, MinScale, Math.Max(MinScale, Math.Min(1.0, fits)));

        width = block.Width * scale;
        height = block.Height * scale;
        return (new System.Windows.Rect(
            movesLeft ? anchorX - width : anchorX,
            movesTop ? anchorY - height : anchorY,
            width, height), scale);
    }

    private static System.Windows.Rect ClampInto(System.Windows.Rect box, System.Windows.Rect screen) => new(
        Math.Clamp(box.X, screen.Left, Math.Max(screen.Left, screen.Right - box.Width)),
        Math.Clamp(box.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - box.Height)),
        box.Width, box.Height);

    // Flush in floating point is a hair either side.
    private const double Epsilon = 0.01;
}
