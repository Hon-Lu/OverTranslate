using System.Windows;
// UseWindowsForms puts System.Drawing in the implicit usings, so these names collide.
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace OverTranslate.Layout;

/// <summary>
/// Where 替換翻譯's hint goes: beside the pointer, inside the monitor the pointer is on.
/// </summary>
/// <remarks>
/// The hint is the only thing telling the user that a shortcut they pressed over someone else's
/// window did anything at all, so it has to land where they are already looking. The pointer is that
/// place: selecting text is a gesture made with the mouse, and it is the one anchor that is always
/// available — the applications that will say where their selected characters are on screen are a
/// minority, and a hint that appeared in two different places depending on which application was in
/// front would be a rule nobody could learn.
///
/// Everything here is physical pixels on one monitor's work area, and every value it is given has
/// already been scaled for that monitor — see <see cref="Services.ScreenGeometry"/>. It computes and
/// does not measure, so the choice it makes can be tested.
/// </remarks>
internal static class HintPlacement
{
    /// <summary>
    /// The hint's top-left corner.
    /// </summary>
    /// <param name="pointer">Where the pointer was when the shortcut was pressed.</param>
    /// <param name="size">The hint window's size.</param>
    /// <param name="workArea">The monitor being placed on, taskbar already taken off.</param>
    /// <param name="gap">
    /// The breathing room between the hint and the pointer, and the closest it may come to the edge
    /// of the screen.
    /// </param>
    /// <remarks>
    /// Down and to the right, the way every tooltip on this platform sits: the pointer's hotspot is
    /// its top-left corner, so that is the one direction where the card cannot come out from under
    /// the cursor itself.
    /// </remarks>
    public static (int Left, int Top) Place(Point pointer, Size size, Rect workArea, double gap) => (
        (int)Math.Round(Clamp(pointer.X + gap * 2, workArea.Left, workArea.Right - size.Width, gap)),
        (int)Math.Round(Clamp(pointer.Y + gap * 2, workArea.Top, workArea.Bottom - size.Height, gap)));

    /// <summary>Keeps a coordinate inside the work area, <paramref name="gap"/> off the edge.</summary>
    /// <remarks>
    /// The lower bound wins when the hint is larger than the space it has to fit in, which
    /// <see cref="Math.Clamp"/> would throw over. Against the near edge is the better of the two
    /// wrong answers: it is the corner the reader is already facing.
    /// </remarks>
    private static double Clamp(double value, double min, double max, double gap) =>
        Math.Max(min + gap, Math.Min(value, Math.Max(min + gap, max - gap)));
}
