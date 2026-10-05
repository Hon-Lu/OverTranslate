using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Reports every mouse button press anywhere on the desktop, for as long as it is installed.
/// </summary>
/// <remarks>
/// <para>For the 標記 形狀 tray, which closes the moment the user presses any button anywhere. A
/// low-level hook because nothing in this session takes focus — the panel is a no-activate window,
/// so no deactivation or lost-focus event will ever say the user has moved on — and because the
/// press that ends the choice is as likely to land on the drawing surface or on another application
/// as on the panel.</para>
///
/// <para>Watches only. A press is passed on untouched: the tray closing is a side effect of a click
/// that still has to do whatever it was aimed at.</para>
///
/// <para>On its own <see cref="HookThread"/> rather than the dispatcher, because a low-level mouse
/// hook holds every pointer move in the system until its callback returns — on the UI thread that
/// would put a stroke being drawn behind its own rendering.</para>
/// </remarks>
internal sealed class MouseDownHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate, so letting the GC collect it
    // would corrupt the hook chain.
    private readonly LowLevelMouseProc _proc;
    private readonly Action<System.Windows.Point> _onPress;
    private readonly HookThread _thread = new("OverTranslate mouse-down hook");
    private IntPtr _hookId;

    private MouseDownHook(Action<System.Windows.Point> onPress)
    {
        _onPress = onPress;
        _proc = HookCallback;
    }

    /// <summary>
    /// Starts reporting presses. <paramref name="onPress"/> runs on the application's dispatcher and
    /// is given where the press was, in physical screen pixels.
    /// </summary>
    public static MouseDownHook Install(Action<System.Windows.Point> onPress)
    {
        var hook = new MouseDownHook(onPress);
        hook._thread.Start();
        hook._thread.Invoke(() =>
        {
            hook._hookId = SetWindowsHookEx(WH_MOUSE_LL, hook._proc, GetModuleHandle(null), 0);
            if (hook._hookId == IntPtr.Zero)
                Log.Warn("Mouse-down hook install failed (win32 error {Error})", Marshal.GetLastWin32Error());
        });
        return hook;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = (int)wParam;
            if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
            {
                // MSLLHOOKSTRUCT starts with the point.
                var point = new System.Windows.Point(Marshal.ReadInt32(lParam), Marshal.ReadInt32(lParam, 4));
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _onPress(point));
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _thread.Stop(() =>
        {
            if (_hookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        });
    }
}
