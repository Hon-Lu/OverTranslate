using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Puts the window a session is about to read in front of everything else.
/// </summary>
/// <remarks>
/// 視窗擷取 asks the user to draw blocks over a window, and gives them a full-screen layer to draw on.
/// Nothing about that arrangement guarantees the window is visible: they chose it from a list, which
/// they could reach with the window buried behind the shell, a browser and a file manager. Framing
/// then means dismissing this application's own layer to go and find the thing they just named,
/// which is the sort of step a program should take rather than ask for.
///
/// Only in 視窗擷取. 螢幕擷取 reads the screen as it is arranged, so rearranging it is precisely the
/// wrong thing to do — whatever the user left on top is what they meant to translate.
/// </remarks>
internal static class SourceWindowFocus
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Raises and activates <paramref name="hwnd"/>, as far as Windows allows.
    /// </summary>
    /// <remarks>
    /// <c>SetWindowPos</c> alone is not enough and <c>SetForegroundWindow</c> alone is not reliable.
    /// Windows refuses foreground changes from a process that does not currently own the foreground,
    /// to stop applications stealing focus — and by the time this runs, the shell window that was
    /// foreground when the user pressed the button may already be hidden, so this process no longer
    /// qualifies. Attaching to the target's input queue for the duration is the long-standing way
    /// through: while attached, the two threads share a foreground state, and the call is allowed.
    ///
    /// Restoring a minimised window first, because a minimised window cannot be raised and the
    /// picker's own list excludes them — but the user can minimise one between choosing it and
    /// pressing 選取翻譯區塊, and that is not a reason to leave them looking at a desktop.
    ///
    /// Best effort throughout. Every step here can legitimately fail — the window can close between
    /// two calls, and a full-screen exclusive game can decline to be reordered — and none of that is
    /// worth stopping a session for: the user still has the framing layer and can put the window in
    /// front themselves, which is what they had to do before this existed.
    /// </remarks>
    public static void Raise(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

        // Every step below runs on the UI thread and some wait on the target's: a log seen once had
        // the edit layer arrive 13 seconds after the press, with this as the likeliest stall. The
        // marks say which step it was if it happens again.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan restored = default, attachedAt = default, broughtToTop = default, foreground = default;

        try
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            restored = clock.Elapsed;

            var target = GetWindowThreadProcessId(hwnd, out _);
            var current = GetCurrentThreadId();
            var attached = target != current && AttachThreadInput(current, target, true);
            attachedAt = clock.Elapsed;

            try
            {
                BringWindowToTop(hwnd);
                broughtToTop = clock.Elapsed;
                SetForegroundWindow(hwnd);
                foreground = clock.Elapsed;
            }
            finally
            {
                if (attached) AttachThreadInput(current, target, false);
            }

            Log.Debug("Realtime capture source hwnd={Hwnd:X} raised for framing", hwnd);

            if (clock.Elapsed > TimeSpan.FromMilliseconds(500))
                Log.Warn(
                    "Raising the capture source hwnd={Hwnd:X} took {Total:F0}ms (restored at {Restored:F0}, attached at {Attached:F0}, brought to top at {Top:F0}, foreground at {Foreground:F0})",
                    hwnd, clock.Elapsed.TotalMilliseconds, restored.TotalMilliseconds, attachedAt.TotalMilliseconds,
                    broughtToTop.TotalMilliseconds, foreground.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Could not raise the capture source window hwnd={Hwnd:X}", hwnd);
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
}
