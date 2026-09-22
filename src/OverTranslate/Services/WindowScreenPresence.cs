using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Removes a window from the screen at a point in time the caller can rely on.
/// <para>
/// <see cref="Window.Hide"/> alone is not enough before a screen capture. It reaches the window
/// manager synchronously, but the pixels only leave the display when DWM composes its next frame,
/// and DWM additionally fades a window out rather than cutting it. A capture taken in between
/// therefore catches the window either fully painted or half transparent. Deferring the capture to
/// a later dispatcher pass does not help either: dispatcher priority orders WPF's own queue and
/// says nothing about what the compositor has presented.
/// </para>
/// </summary>
internal static class WindowScreenPresence
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int DwmwaTransitionsForcedisabled = 3;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>Blocks until DWM has composed and presented its next frame.</summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();

    /// <summary>
    /// Hides <paramref name="window"/> and does not return until it is genuinely off the screen:
    /// the fade-out is suppressed first so the hide is a clean cut, then the call waits for the
    /// composition that removes it. Costs at most one display frame.
    /// </summary>
    public static void HideAndWaitForScreen(Window window) => HideAndWaitForScreen([window]);

    /// <summary>
    /// The same for a set of windows that have to leave together — a realtime session's control bar
    /// and its block layers, say. The wait for the composition is paid once for all of them rather
    /// than once each, which matters because it is a display frame per call and the user is standing
    /// on a shortcut waiting for the screen to freeze.
    /// </summary>
    public static void HideAndWaitForScreen(IReadOnlyList<Window> windows)
    {
        if (windows.Count == 0) return;

        var handles = new nint[windows.Count];
        for (int i = 0; i < windows.Count; i++)
            handles[i] = new WindowInteropHelper(windows[i]).Handle;

        // Only for the duration of this hide. Restoring it immediately afterwards keeps the
        // window's ordinary show/close animations intact, and means there is no paired "undo"
        // call elsewhere that a future code path could forget.
        foreach (var hwnd in handles) SetTransitionsEnabled(hwnd, enabled: false);
        try
        {
            // Every hide first, then one flush: each Hide reaches the window manager synchronously,
            // so by the time DWM composes again none of them is left to draw.
            for (int i = 0; i < windows.Count; i++) windows[i].Hide();

            if (Array.Exists(handles, h => h != nint.Zero))
            {
                int hr = DwmFlush();
                if (hr < 0)
                    Log.Warn("DwmFlush failed (0x{0:X8}); the capture may catch the window mid-hide", hr);
            }
        }
        finally
        {
            foreach (var hwnd in handles) SetTransitionsEnabled(hwnd, enabled: true);
        }
    }

    private static void SetTransitionsEnabled(nint hwnd, bool enabled)
    {
        if (hwnd == nint.Zero) return;

        // The attribute is "forced disabled", so its value is the inverse of what we want.
        int disable = enabled ? 0 : 1;
        int hr = DwmSetWindowAttribute(hwnd, DwmwaTransitionsForcedisabled, ref disable, sizeof(int));
        if (hr < 0)
            Log.Warn("Could not {0} window transitions (0x{1:X8})", enabled ? "restore" : "suppress", hr);
    }
}
