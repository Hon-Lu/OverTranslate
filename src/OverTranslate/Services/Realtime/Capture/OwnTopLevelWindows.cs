using System.Runtime.InteropServices;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// The top-level windows this process has on the screen, front to back.
/// </summary>
/// <remarks>
/// Asked of the system rather than of WPF, for two reasons. The monitor capture asks from its polling
/// thread, where WPF windows may not be touched. And the windows that matter here are not all
/// <see cref="System.Windows.Window"/>s: a list dropped down from 取詞翻譯's language box, or a
/// tooltip, is a popup window of its own that no window collection lists.
/// </remarks>
internal static class OwnTopLevelWindows
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;

    /// <summary>Every visible one, in z-order from the front.</summary>
    public static List<IntPtr> Visible()
    {
        var own = (uint)Environment.ProcessId;
        var found = new List<IntPtr>();

        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out var processId);
                if (processId == own) found.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    public static bool IsTopmost(IntPtr hwnd) =>
        (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
