using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// What the user can actually see of the window being translated: whether it is on the screen at
/// all, and which other applications' windows are sitting over it.
/// </summary>
/// <remarks>
/// Only for 視窗擷取. The capture reads the window itself, so it goes on producing the right picture
/// while the window is covered — which is what the overlays are drawn from, and what used to leave
/// one game's subtitles and repaired background floating over another game the user had switched
/// to. The overlays are topmost and re-asserted every second, so nothing the user did could put
/// them away; this is what tells the session which of them no longer has anything under it.
/// </remarks>
internal static class SourceWindowCoverage
{
    /// <summary>
    /// The source's state as seen from the screen: <paramref name="Hidden"/> when it is minimised,
    /// invisible, cloaked or gone, and otherwise the rectangles of other applications' windows above
    /// it in z-order, in physical pixels.
    /// </summary>
    public readonly record struct View(bool Hidden, IReadOnlyList<Rectangle> Covers);

    /// <summary>The facts about one window that decide whether it covers the source.</summary>
    public readonly record struct WindowFacts(
        uint ProcessId, bool Visible, bool Minimized, bool Cloaked, bool ClickThrough, string ClassName,
        Rectangle Bounds);

    // The taskbar is above every ordinary window by design, so a game window under it is "covered"
    // all the time — and an overlay drawn there has always been drawn over the taskbar as well.
    private static readonly HashSet<string> NeverCovers =
        ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    /// <summary>
    /// Whether a window above the source counts as covering it.
    /// </summary>
    /// <remarks>
    /// Not this application's windows: the overlays themselves, the control bar, and anything the
    /// user opened from it are above the source by design. Not the source's own process either —
    /// a game's popups and child windows are part of what the user is looking at. Not a window the
    /// user cannot see, and not one that clicks pass through: those are other programs' overlays
    /// (a performance counter, a recording indicator), drawn over everything without hiding it.
    /// </remarks>
    public static bool CountsAsCover(WindowFacts window, uint ownProcessId, uint sourceProcessId) =>
        window.ProcessId != ownProcessId
        && window.ProcessId != sourceProcessId
        && window.Visible
        && !window.Minimized
        && !window.Cloaked
        && !window.ClickThrough
        && !NeverCovers.Contains(window.ClassName)
        && window.Bounds.Width > 0
        && window.Bounds.Height > 0;

    /// <summary>
    /// Whether an overlay that is drawing <paramref name="drawn"/> (physical pixels: each line's
    /// band or repaired patch and its text) still has its source under it: the source is on the
    /// screen and no other application's window touches anything the overlay draws.
    /// </summary>
    /// <remarks>
    /// Per overlay, not for the session as a whole. On two monitors the user can be playing something
    /// else on the other screen with the translated window in full view, and a window over one corner
    /// of the source takes away only the blocks under it.
    ///
    /// What the overlay draws, not the block the user framed. Most of a block is transparent most of
    /// the time — a subtitle strip framed generously, a dialogue box between lines — and a small
    /// window parked over an empty part of it (a picture-in-picture video, a chat) hides nothing the
    /// overlay shows. Touching any of it takes the whole overlay away, though, not the lines it
    /// touches: half a translation is worse than none. An overlay drawing nothing has nothing to be
    /// covered, unless the source itself is off the screen.
    /// </remarks>
    public static bool LeavesVisible(View view, IReadOnlyList<Rectangle> drawn) =>
        !view.Hidden
        && !drawn.Any(area => view.Covers.Any(cover => cover.IntersectsWith(area)));

    /// <summary>
    /// Looks at the source and every window above it. Cheap enough to run on the stay-on-top timer:
    /// it walks up the z-order from the source only, so it costs the windows above it and no more.
    /// </summary>
    public static View Look(IntPtr source)
    {
        if (source == IntPtr.Zero || !IsWindow(source)) return new View(true, []);

        var sourceFacts = Facts(source);
        if (!sourceFacts.Visible || sourceFacts.Minimized || sourceFacts.Cloaked) return new View(true, []);

        var own = (uint)Environment.ProcessId;
        var covers = new List<Rectangle>();
        for (var above = GetWindow(source, GW_HWNDPREV); above != IntPtr.Zero; above = GetWindow(above, GW_HWNDPREV))
        {
            var facts = Facts(above);
            if (CountsAsCover(facts, own, sourceFacts.ProcessId)) covers.Add(facts.Bounds);
        }

        return new View(false, covers);
    }

    private static WindowFacts Facts(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        var visible = IsWindowVisible(hwnd);
        var cloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloak, sizeof(int)) == 0 && cloak != 0;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

        // The visible frame rather than the window rectangle, which carries an invisible resize
        // border several pixels wide — enough to count a window beside the block as over it.
        Rectangle bounds;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) == 0)
            bounds = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
        else if (GetWindowRect(hwnd, out var rect))
            bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        else
            bounds = Rectangle.Empty;

        var name = new StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);

        return new WindowFacts(
            processId, visible, IsIconic(hwnd), cloaked, (exStyle & WS_EX_TRANSPARENT) != 0, name.ToString(), bounds);
    }

    private const uint GW_HWNDPREV = 3;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
