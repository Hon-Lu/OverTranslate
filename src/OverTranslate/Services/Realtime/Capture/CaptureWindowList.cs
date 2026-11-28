using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using NLog;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// The windows a user can point 即時翻譯 at, as offered in the picker.
/// </summary>
/// <remarks>
/// This is the deliberate opposite of <see cref="SourceWindowResolver"/>. That one infers the source
/// from where the blocks were drawn, which is invisible, occasionally wrong, and — the thing that
/// decided it — impossible to correct: a user whose session resolved to the wrong window, or to no
/// window at all, has no control that says which one they meant. Asking outright costs one field and
/// answers exactly what the whole inference was guessing at.
///
/// What is on this list is therefore a product question, not a technical one. Everything the user
/// can see and would recognise, nothing they cannot: a list that includes the invisible half of the
/// shell is a list they have to read past every time.
/// </remarks>
public static class CaptureWindowList
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <param name="Hwnd">The window itself. Dead the moment the application closes it.</param>
    /// <param name="Title">What the window calls itself — the only part the user recognises.</param>
    /// <param name="ProcessName">Which application it belongs to, for telling two alike titles apart.</param>
    /// <param name="Bounds">Where it sits, in physical pixels. Decides which screen framing happens on.</param>
    public readonly record struct CaptureWindow(
        IntPtr Hwnd, string Title, string ProcessName, Rectangle Bounds);

    /// <summary>
    /// Every window worth offering, front to back.
    /// </summary>
    /// <remarks>
    /// Z-order, not alphabetical: <c>EnumWindows</c> hands them over topmost-first, which puts the
    /// application the user just came back from near the top of the list. That is nearly always the
    /// one they mean, and sorting by name would bury it.
    /// </remarks>
    public static IReadOnlyList<CaptureWindow> Enumerate()
    {
        var found = new List<CaptureWindow>();

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (DescribeWindow(hwnd) is { } window) found.Add(window);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            // An enumeration that failed halfway still lists what it reached, and an empty list has
            // its own message on the page. Neither is worth an exception reaching the user.
            Log.Warn(ex, "Could not finish enumerating capturable windows");
        }

        Log.Debug("Capturable windows offered: {Count}", found.Count);
        return found;
    }

    /// <summary>
    /// The listed window a stored choice refers to, or null when nothing here is recognisably it.
    /// </summary>
    /// <remarks>
    /// A handle cannot be stored, so what comes back is a pair — which application, and what its
    /// window was called — and neither half is reliable alone. The title is what identifies one
    /// window of an application among its others, and it is also the half that changes constantly:
    /// a browser retitles itself with every video, so an exact match would restore almost nothing
    /// for exactly the case this feature is most used on.
    ///
    /// So: the exact pair first, and failing that, the application alone — but only when it has one
    /// window open. Two windows of the same application and there is no way to tell which was meant,
    /// and quietly picking one would point a session at the wrong thing while looking like it
    /// remembered correctly. That case falls back to asking, which is the honest answer.
    /// </remarks>
    public static CaptureWindow? FindStored(
        IReadOnlyList<CaptureWindow> windows, string processName, string title)
    {
        if (processName.Length == 0) return null;

        foreach (var window in windows)
        {
            if (window.ProcessName == processName && window.Title == title)
                return window;
        }

        CaptureWindow? sole = null;
        foreach (var window in windows)
        {
            if (window.ProcessName != processName) continue;
            if (sole is not null) return null;
            sole = window;
        }

        return sole;
    }

    /// <summary>Whether a window from an earlier listing is still there to be captured.</summary>
    public static bool StillAvailable(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd);

    /// <summary>
    /// The rectangle window capture reads <paramref name="hwnd"/> at, in physical pixels: its DWM
    /// extended frame bounds, or its window rectangle where those are unavailable — the same pair
    /// <c>WgcWindowCaptureBackend.TryGetWindowBounds</c> takes its frame origin from, and the frame a
    /// region is cut from is that rectangle.
    /// </summary>
    /// <remarks>
    /// A window preset's blocks are kept relative to this rectangle's top-left, so that they follow a
    /// window that has moved. Empty when the handle is gone or the window cannot be measured.
    /// </remarks>
    public static Rectangle CaptureBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return Rectangle.Empty;

        var window = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0
            ? Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom)
            : window;
    }

    /// <summary>
    /// One window by handle — the same pair the list offers, for a caller that holds a live handle
    /// and has no list: a running session naming what it is reading, so that its framing can be kept
    /// against that window rather than against the whole screen.
    /// </summary>
    /// <returns>The window, or null when it is gone, minimised, or not something the list would offer.</returns>
    public static CaptureWindow? Of(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            return DescribeWindow(hwnd);
        }
        catch (Exception)
        {
            // A window that died between the handle being held and being asked about. Its owner has
            // nothing to name, which the callers already handle.
            return null;
        }
    }

    /// <summary>
    /// One window, or null when it is not something to offer.
    /// </summary>
    /// <remarks>
    /// Each exclusion earns its place by what it removes from the list:
    ///
    /// <list type="bullet">
    /// <item>Invisible, untitled and zero-sized windows are the message-only windows every process
    /// keeps. There are dozens and none of them can be pointed at.</item>
    /// <item>Cloaked windows are the reason a plain visible-and-titled filter is not enough. UWP
    /// keeps closed apps around as windows that report themselves visible and titled while being
    /// drawn nowhere, so the list fills with 設定 and 小算盤 the user has not got open.</item>
    /// <item>Tool windows are palettes and floating bars, which are not what anyone means by "the
    /// window with the subtitles in it".</item>
    /// <item>Minimised windows cannot be captured — window capture produces nothing for them — and
    /// the user could not frame blocks over one anyway.</item>
    /// <item>This process's own windows, for the reason the whole feature exists.</item>
    /// <item>The desktop, which has no application behind it. A user who wants the wallpaper wants
    /// 螢幕擷取.</item>
    /// </list>
    /// </remarks>
    private static CaptureWindow? DescribeWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return null;

        var title = TextOf(hwnd, GetWindowText);
        if (title.Length == 0) return null;

        var exStyle = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return null;

        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return null;

        var className = TextOf(hwnd, GetClassName);
        if (className is "Progman" or "WorkerW") return null;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return null;

        if (!GetWindowRect(hwnd, out var rect)) return null;
        var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        return new CaptureWindow(hwnd, title, ProcessNameOf(pid), bounds);
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
            // Exited between the enumeration and this call, or running at a level this process
            // cannot ask about. The title is what the user reads; this only tells two alike ones
            // apart, and doing that imperfectly beats dropping the window from the list.
            return "";
        }
    }

    /// <summary>
    /// The full path of the executable behind <paramref name="hwnd"/>, or "" when it cannot be read.
    /// </summary>
    /// <remarks>
    /// What tells two applications of one process name apart — every RPG Maker game is Game.exe — so
    /// that a framing kept for one is not put on another. Asked with the limited query right, which a
    /// process running as administrator still answers; "" is the caller's cue to fall back to the name.
    /// </remarks>
    public static string ExecutablePathOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return "";

        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return "";
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : "";
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string TextOf(IntPtr hwnd, Func<IntPtr, StringBuilder, int, int> read)
    {
        var buffer = new StringBuilder(256);
        var length = read(hwnd, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : "";
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int capacity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int attribute, out int value, int size);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeRect(
        IntPtr hwnd, int attribute, out RECT value, int size);
}
