using System.Drawing;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Keeps 視窗擷取's blocks on the window they were drawn over rather than at a fixed place on the
/// screen.
/// </summary>
/// <remarks>
/// A block is drawn in screen pixels, over the window as it was when the session started — the
/// anchor. A game that is dragged, or that switches to fullscreen and back and comes back somewhere
/// else, used to leave every block reading a patch of whatever was now at the old place — the block
/// stopped translating for good and nothing on the screen said why.
///
/// So the blocks move with the window, which is what the user drew them against. That holds only
/// while the window is the size it was: a game in fullscreen at another resolution, or a window the
/// user has resized, lays its picture out again, and the block no longer covers the same words in
/// any position. Then there is no answer, and the blocks wait for the window to come back to its
/// size rather than read the wrong part of it.
/// </remarks>
internal static class RealtimeSourceAnchor
{
    /// <summary>
    /// How far the window has moved from <paramref name="anchor"/>, or null when it is not the size
    /// it was.
    /// </summary>
    public static Point? Offset(Rectangle anchor, Rectangle current) =>
        current.Size == anchor.Size
            ? new Point(current.X - anchor.X, current.Y - anchor.Y)
            : null;

    /// <summary>
    /// Where a block drawn at <paramref name="region"/> over the window at <paramref name="anchor"/>
    /// is now, or null when the window is not the size it was.
    /// </summary>
    public static Rectangle? Follow(Rectangle region, Rectangle anchor, Rectangle current) =>
        Offset(anchor, current) is { } offset
            ? region with { X = region.X + offset.X, Y = region.Y + offset.Y }
            : null;
}
