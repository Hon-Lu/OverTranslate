using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Catches Ctrl+C for 截圖 while Ctrl+C 複製截圖 is on and a selection is drawn (#445).
/// </summary>
/// <remarks>
/// <para>A low-level hook for the reason <see cref="AnnotationShortcutHook"/> is one: nothing in the
/// capture session holds the keyboard focus, so no key handler in this process would ever see the
/// key. A class of its own rather than a third key on that one, because the two live for different
/// spans — that one as long as the 標記 panel, this one from the selection being finished to the
/// capture ending — and sharing a hook would mean installing it for either and asking at every key
/// which of the two it is up for.</para>
///
/// <para>On its own thread, as <see cref="GlobalEscapeHook"/> is, not the dispatcher's: it is up for
/// the rest of the capture, translation included, and a hook on a busy interface thread holds every
/// key on the desktop until the thread gets round to it. The callback only reads a struct and the
/// modifiers and posts — waiting on the interface from it would risk outrunning
/// <c>LowLevelHooksTimeout</c>, after which Windows silently drops the hook and the key stops
/// working for the rest of the session.</para>
///
/// <para>The C is swallowed, not passed on. The application behind the capture is covered by its
/// frozen picture; a Ctrl+C reaching it would replace the screenshot on the clipboard with whatever
/// it has selected. Esc and Ctrl+Z are swallowed in this session for the same reason. Installed
/// after 取詞翻譯's sequence hook, this one is asked first, so a Ctrl+C+C here is one screenshot
/// and nothing for 取詞翻譯 to count.</para>
///
/// <para>Our own synthesised keys go past: 取詞翻譯 and 替換翻譯 send a Ctrl+C of their own, which
/// must copy the selection they were asked about, not take a screenshot.</para>
/// </remarks>
internal sealed class CaptureCopyKeyHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate, so letting the GC collect it
    // would corrupt the hook chain.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Action _onCopy;
    private readonly HookThread _thread = new("OverTranslate capture copy key");

    // Touched only on the hook thread.
    private readonly CaptureCopyKey _keys = new();
    private IntPtr _hookId;

    private CaptureCopyKeyHook(Action onCopy)
    {
        _onCopy = onCopy;
        _proc = HookCallback;
    }

    /// <param name="onCopy">Run on the dispatcher for each Ctrl+C taken.</param>
    public static CaptureCopyKeyHook Install(Action onCopy)
    {
        var hook = new CaptureCopyKeyHook(onCopy);
        hook._thread.Start();

        // Invoke rather than BeginInvoke: switched on mid-capture, the key works from the moment
        // the switch shows it on. Installing a hook is a single syscall.
        hook._thread.Invoke(hook.InstallOnHookThread);
        return hook;
    }

    private void InstallOnHookThread()
    {
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hookId == IntPtr.Zero)
        {
            Log.Warn("Ctrl+C 複製截圖 hook install failed (win32 error {Error}) — the 截圖 button still works",
                Marshal.GetLastWin32Error());
        }
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int message = wParam.ToInt32();
        bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        if (nCode < 0 || !(down || message is WM_KEYUP or WM_SYSKEYUP))
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if (data.dwExtraInfo == KeyboardInput.InjectedMarker)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var verdict = _keys.OnKey(
            data.vkCode, down,
            ctrl: IsDown(VK_CONTROL),
            shift: IsDown(VK_SHIFT),
            alt: IsDown(VK_MENU),
            win: IsDown(VK_LWIN) || IsDown(VK_RWIN),
            typing: down && TypingWindows.HaveForeground);

        switch (verdict)
        {
            case CaptureCopyKeyVerdict.Copy:
                // Post, never wait — see the remarks.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(_onCopy);
                return (IntPtr)1;
            case CaptureCopyKeyVerdict.Swallow:
                return (IntPtr)1;
            default:
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }
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
