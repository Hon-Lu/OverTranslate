using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OverTranslate.Services;

/// <summary>
/// Hands the keyboard to one of this application's windows for a spell of typing, and back again.
/// </summary>
/// <remarks>
/// For the 標記 panel's size box and the 文字 mark being typed (#362): both sit in a capture session
/// where no window takes focus, so the foreground is still the application under the capture. See
/// QuickLookupWindow.TakeForeground for why the foreground thread's input queue is shared for the
/// call — <c>Activate</c> alone is refused without a word, and the box shows a caret while every key
/// goes to the application underneath.
/// </remarks>
internal static class KeyboardForeground
{
    /// <summary>Makes <paramref name="window"/> the one the keyboard talks to.</summary>
    /// <returns>Whatever had the foreground before, to give it back to; zero if it was this window.</returns>
    /// <remarks>
    /// The no-activate bit comes off first: Windows will not hand the foreground to a window
    /// carrying it, however it is asked. Put back by <see cref="Release"/>.
    /// </remarks>
    public static IntPtr Take(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        var foreground = GetForegroundWindow();
        var previous = foreground == hwnd ? IntPtr.Zero : foreground;

        WindowStyles.SetNoActivate(window, enabled: false);
        TypingWindows.Add(hwnd);

        var owner = GetWindowThreadProcessId(foreground, out _);
        var self = GetCurrentThreadId();
        var attached = owner != 0 && owner != self && AttachThreadInput(self, owner, true);

        try
        {
            SetForegroundWindow(hwnd);
            window.Activate();
        }
        finally
        {
            if (attached) AttachThreadInput(self, owner, false);
        }

        return previous;
    }

    /// <summary>
    /// Ends the spell: the window stops taking activation and the keyboard goes back to
    /// <paramref name="previous"/>, if this window still holds it.
    /// </summary>
    /// <param name="keepNoActivate">False for a window that was never no-activate to begin with.</param>
    public static void Release(Window window, IntPtr previous, bool keepNoActivate = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        TypingWindows.Remove(hwnd);
        if (keepNoActivate) WindowStyles.SetNoActivate(window, enabled: true);

        // Allowed without ceremony: the holder of the foreground may pass it on. Not taken from
        // anyone else, though — a click on another application has already moved it, and pulling it
        // back would undo the click.
        if (hwnd == IntPtr.Zero || GetForegroundWindow() != hwnd) return;
        if (previous != IntPtr.Zero && previous != hwnd && IsWindow(previous)) SetForegroundWindow(previous);
    }

    /// <summary>Whether the foreground is a window of this process.</summary>
    public static bool IsOwnForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>The foreground window's class and title, for the log.</summary>
    public static string Describe()
    {
        var foreground = GetForegroundWindow();
        var cls = new System.Text.StringBuilder(128);
        var title = new System.Text.StringBuilder(128);
        GetClassName(foreground, cls, cls.Capacity);
        GetWindowText(foreground, title, title.Capacity);

        // Our own windows all have the same WPF class and most have no title, so which one it is
        // is said by type — "lost the keyboard to HwndWrapper" told nobody which window took it.
        string? own = null;
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            foreach (Window w in System.Windows.Application.Current.Windows)
                if (new WindowInteropHelper(w).Handle == foreground) { own = w.GetType().Name; break; }

        return own is null ? $"0x{foreground:X} {cls} \"{title}\"" : $"0x{foreground:X} {own}";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);
}
