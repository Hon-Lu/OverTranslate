using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ToolTip = System.Windows.Controls.ToolTip;

namespace OverTranslate.Services;

/// <summary>
/// Puts a window back on top of the topmost band without taking the focus off whatever the user is
/// actually using.
/// </summary>
/// <remarks>
/// Topmost is a band, not a position: every topmost window in the system shares it, and activating
/// one moves it to the front of the band. That is the whole reason the screenshot overlay is never
/// covered — it calls <c>Activate()</c>, because the user is about to draw on it.
///
/// The realtime layers cannot do that. They sit over a game or a video and must not take its input,
/// so they carry WS_EX_NOACTIVATE and are never activated. The cost is that they enter the band
/// where they were put and never move up again: anything topmost that gets activated afterwards —
/// a chat overlay, a capture tool, another utility — goes above them and stays there.
///
/// SetWindowPos re-inserts a window at the front of the band directly, and SWP_NOACTIVATE means it
/// does so without the focus change that would break the application underneath. Called on a timer,
/// it wins that competition against anything that is not re-asserting itself just as often.
///
/// It does not win against a game in exclusive fullscreen, which bypasses the compositor entirely
/// and cannot be drawn over by any window at all. That case needs borderless windowed mode, and no
/// amount of z-order work substitutes for it.
/// </remarks>
internal static class AlwaysOnTop
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static void Reassert(Window window) => Reassert(new WindowInteropHelper(window).Handle);

    /// <summary>The same, for a window WPF does not expose as a <see cref="Window"/> — a popup's.</summary>
    public static void Reassert(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// <see cref="Reassert(Window)"/>, then the same for any tooltip the window has open, so the
    /// tooltip stays in front of it.
    /// </summary>
    /// <remarks>
    /// A tooltip is a popup window of its own, topmost and owned by nothing — not by the window it
    /// belongs to — so Windows does not keep it above that window the way it would an owned one.
    /// Re-asserting the window alone put it over the tooltip it had just opened, a second after the
    /// tooltip appeared. The tooltips are raised after the window rather than the window being
    /// skipped while one is open: hovering is exactly when it must not slip behind anything else.
    /// </remarks>
    public static void ReassertWithToolTips(Window window)
    {
        Reassert(window);
        ReassertToolTips(window);
    }

    /// <summary>
    /// Only the tooltips half of <see cref="ReassertWithToolTips"/>, for a caller that has to raise
    /// several windows first and their tooltips after all of them.
    /// </summary>
    /// <remarks>
    /// Raising an owned window brings its owner up with it, to just beneath it. So re-asserting the
    /// realtime control bar, which the edit layer owns, put the whole edit layer back over a tooltip
    /// the layer had just raised — the tooltip ended up behind the trays and the block it belonged
    /// to. Raising every window and then every tooltip leaves the tooltips on top whatever owns what.
    /// </remarks>
    public static void ReassertToolTips(Window window)
    {
        foreach (PresentationSource source in PresentationSource.CurrentSources)
        {
            if (source is HwndSource { RootVisual: { } root } popup && root is not Window
                && FindToolTip(root) is { IsOpen: true, PlacementTarget: { } target }
                && window.IsAncestorOf(target))
                Reassert(popup.Handle);
        }
    }

    // A popup's root is WPF's own PopupRoot, with the tooltip a few decorators down, each of them
    // holding it as their first child — an adorner decorator's second is its adorner layer.
    private static ToolTip? FindToolTip(Visual root)
    {
        DependencyObject node = root;
        for (int depth = 0; depth < 6 && VisualTreeHelper.GetChildrenCount(node) > 0; depth++)
        {
            node = VisualTreeHelper.GetChild(node, 0);
            if (node is ToolTip tip) return tip;
        }
        return null;
    }

    /// <summary>
    /// Puts a window directly behind another one, wherever that one is. False when it did not take.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="Reassert"/>, for stepping aside rather than coming forward.
    /// Clearing Topmost alone does not do it: a window leaving the topmost band arrives at the
    /// front of the ordinary band, which is still in front of the window it was making room for.
    ///
    /// The result is worth reading for that reason. Stepping aside is those two moves together, and
    /// the window this one is aiming behind can be gone by the time it gets here — half of it done
    /// is worse than none of it: out of the topmost band and still covering what it made room for.
    /// </remarks>
    public static bool PlaceBehind(Window window, IntPtr other)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || other == IntPtr.Zero) return false;

        return SetWindowPos(hwnd, other, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
