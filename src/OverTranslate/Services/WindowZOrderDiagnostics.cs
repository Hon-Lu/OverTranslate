using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OverTranslate.Services;

/// <summary>
/// Read-only questions about where a window sits, for logs that have to explain a layer found
/// behind something it should be above.
/// </summary>
/// <remarks>
/// Diagnostics only: nothing here changes a window. It exists because "the edit layer ended up under
/// the capture source" was seen twice and never reproduced, and the three explanations for it — the
/// stay-on-top timer not running, SetWindowPos not taking, the other window having become topmost —
/// leave different traces only if something writes them down at the time.
/// </remarks>
internal static class WindowZOrderDiagnostics
{
    private const int GWL_EXSTYLE = -20;
    private const uint GW_HWNDPREV = 3;
    private const int WS_EX_TOPMOST = 0x8;
    private const int DWMWA_CLOAKED = 14;

    /// <summary>A visible window of another process sitting above, and overlapping, the one asked about.</summary>
    public readonly record struct CoveringWindow(IntPtr Hwnd, string ClassName, string ProcessName, int ExStyle)
    {
        public bool IsTopmost => (ExStyle & WS_EX_TOPMOST) != 0;
    }

    public static int ExStyle(IntPtr hwnd) => GetWindowLong(hwnd, GWL_EXSTYLE);

    public static bool IsVisible(IntPtr hwnd) => IsWindowVisible(hwnd);

    /// <summary>
    /// The nearest window above <paramref name="hwnd"/> in z-order that belongs to another process,
    /// is visible and overlaps it — or null when there is none within <paramref name="limit"/> steps.
    /// </summary>
    /// <remarks>
    /// This process's own windows are skipped: the control bar and tooltips are meant to be above the
    /// layers. So are cloaked windows, which report visible while DWM draws nothing of them — Windows
    /// keeps several of its own that way. The limit keeps a tick to a handful of calls; a layer that
    /// is where it should be has very few windows above it.
    /// </remarks>
    public static CoveringWindow? FindForeignWindowAbove(IntPtr hwnd, int limit = 64)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var own)) return null;

        int self = Environment.ProcessId;
        var above = hwnd;
        for (int i = 0; i < limit; i++)
        {
            above = GetWindow(above, GW_HWNDPREV);
            if (above == IntPtr.Zero) return null;

            if (!IsWindowVisible(above) || IsCloaked(above)) continue;
            GetWindowThreadProcessId(above, out int pid);
            if (pid == self) continue;
            if (!GetWindowRect(above, out var other) || !Overlaps(own, other)) continue;

            return new CoveringWindow(above, ClassNameOf(above), ProcessNameOf(pid), ExStyle(above));
        }

        return null;
    }

    private static bool Overlaps(Rect a, Rect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) >= 0 && cloaked != 0;

    private static string ClassNameOf(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "?";
    }

    private static string ProcessNameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // Gone already, or not ours to look at — the hwnd and class still say who it was.
            return $"pid {pid}";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
