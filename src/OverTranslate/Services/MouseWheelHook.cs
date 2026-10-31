using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Takes the mouse wheel for itself while <see cref="IsTaking"/> is set, anywhere on the desktop.
/// </summary>
/// <remarks>
/// <para>For 標記 (#373): with a stroke being drawn, or a 文字 box open, the wheel sets how wide the
/// mark is. A low-level hook rather than WPF's own wheel event, because nothing in a capture session
/// can be relied on to receive it — the wheel goes to the window with the keyboard, or under the
/// pointer, depending on a system setting — and because a notch meant for the pen must not also
/// scroll whatever is under the capture.</para>
///
/// <para>Whether to take a notch has to be decided inside the callback, on the hook's thread, so
/// that is a flag the UI thread sets rather than anything asked of the UI. Notches not taken are
/// passed on untouched.</para>
/// </remarks>
internal sealed class MouseWheelHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate.
    private readonly LowLevelMouseProc _proc;
    private readonly Action<int> _onNotches;
    private readonly HookThread _thread = new("OverTranslate mouse-wheel hook");
    private IntPtr _hookId;
    private volatile bool _isTaking;

    /// <summary>
    /// What has turned but not yet made a whole notch, on the hook's thread. A precision touchpad
    /// sends the wheel in small pieces, and one size step per piece would race through the range.
    /// </summary>
    private int _remainder;

    private MouseWheelHook(Action<int> onNotches)
    {
        _onNotches = onNotches;
        _proc = HookCallback;
    }

    /// <summary>Whether the wheel is being taken right now. Set from the UI thread.</summary>
    public bool IsTaking
    {
        get => _isTaking;
        set => _isTaking = value;
    }

    /// <summary>
    /// Starts watching. <paramref name="onNotches"/> runs on the application's dispatcher with whole
    /// notches turned, positive away from the user.
    /// </summary>
    public static MouseWheelHook Install(Action<int> onNotches)
    {
        var hook = new MouseWheelHook(onNotches);
        hook._thread.Start();
        hook._thread.Invoke(() =>
        {
            hook._hookId = SetWindowsHookEx(WH_MOUSE_LL, hook._proc, GetModuleHandle(null), 0);
            if (hook._hookId == IntPtr.Zero)
                Log.Warn("Mouse-wheel hook install failed (win32 error {Error})", Marshal.GetLastWin32Error());
        });
        return hook;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_MOUSEWHEEL)
        {
            if (!_isTaking)
            {
                _remainder = 0;
            }
            else
            {
                // MSLLHOOKSTRUCT: the point, then mouseData, whose high word is the signed delta.
                int delta = (short)(Marshal.ReadInt32(lParam, 8) >> 16);
                int notches = Accumulate(ref _remainder, delta);
                if (notches != 0)
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _onNotches(notches));
                return 1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>Adds <paramref name="delta"/> to what is pending and takes out the whole notches it makes.</summary>
    internal static int Accumulate(ref int remainder, int delta)
    {
        const int Notch = 120;
        remainder += delta;
        int notches = remainder / Notch;
        remainder -= notches * Notch;
        return notches;
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
