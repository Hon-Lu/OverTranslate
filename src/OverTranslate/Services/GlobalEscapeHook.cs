using System.Runtime.InteropServices;
using System.Windows.Threading;
using NLog;

namespace OverTranslate.Services;

// A process-wide low-level keyboard hook that swallows Esc and reports it to a callback.
//
// This belongs to the capture session rather than to any single window. Before a selection
// exists there is no overlay to host a hook, and ScreenCaptureWindow.OnKeyDown only fires while
// that window holds the keyboard focus — which it cannot count on (the tray menu closing right
// after it opens, full-screen games, elevated foreground windows). Owning the hook for the whole
// session is what makes Esc behave identically before and after the selection is drawn.
//
// The hook lives on its own message-pumping thread (see HookThread), and that is what makes Esc
// an escape hatch rather than one more thing that stops working. The capture layers are Topmost
// and cover the whole virtual desktop, so nothing — Task Manager included — can be raised over
// them; Esc is the way out. Installed from the dispatcher, the callback would be delivered to the
// dispatcher too, so any UI thread that stopped pumping would take Esc down with it at exactly the
// moment it was needed, and would stall the entire desktop's keyboard until LowLevelHooksTimeout
// on every key besides.
internal sealed class GlobalEscapeHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate, so letting the GC collect it
    // would corrupt the hook chain.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Action _onEscape;
    private readonly HookThread _thread = new("OverTranslate escape hook");
    private IntPtr _hookId;

    private GlobalEscapeHook(Action onEscape)
    {
        _onEscape = onEscape;
        _proc = HookCallback;
    }

    public static GlobalEscapeHook Install(Action onEscape)
    {
        var hook = new GlobalEscapeHook(onEscape);

        hook._thread.Start();

        // Invoke rather than BeginInvoke: the caller is entitled to have Esc live by the time this
        // returns, and installing a hook is a single syscall.
        hook._thread.Invoke(hook.InstallOnHookThread);

        return hook;
    }

    /// <summary>Runs on the hook thread, which is the only thread the hook can be installed from.</summary>
    private void InstallOnHookThread()
    {
        // GetModuleHandle(null) returns this process's own module handle directly. Resolving it via
        // Process.GetCurrentProcess().MainModule enumerates the process module list and costs
        // milliseconds on a path that runs while the capture window is being presented.
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Log.Warn("Esc hook install failed (win32 error {Error}) — Esc will only cancel while the capture window has focus",
                error);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Typing into one of ours, Esc belongs to the box — see TypingWindows.
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN && Marshal.ReadInt32(lParam) == VK_ESCAPE
            && !TypingWindows.HaveForeground)
        {
            // Post, never wait. A low-level hook callback that runs longer than
            // LowLevelHooksTimeout (5s by default) gets silently dropped from the hook chain by
            // Windows, and from then on Esc would stop cancelling for the rest of the session.
            //
            // Send priority, not the default: a UI thread busy enough to be worth escaping from
            // has a queue of pointer events behind it, and cancelling has to jump that queue
            // rather than take its place at the back of it. Everything the interface queues for
            // itself is Normal or below.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Send, _onEscape);
            return (IntPtr)1; // swallow, so Esc does not also reach the app being translated
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        // On the hook thread: a hook can only be removed from the thread that installed it.
        _thread.Stop(() =>
        {
            if (_hookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        });
    }
}
