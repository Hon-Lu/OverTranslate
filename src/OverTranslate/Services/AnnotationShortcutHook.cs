using System.Runtime.InteropServices;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Catches Ctrl+Z and Ctrl+Y for 標記, for as long as 標記 is on.
/// </summary>
/// <remarks>
/// <para>A process-wide low-level hook for the same reason <see cref="GlobalEscapeHook"/> is one:
/// nothing in this session holds the keyboard focus. The capture toolbar, the 標記 panel and the
/// overlay are all deliberately no-activate windows so that showing them cannot pull focus off the
/// application being captured — which means no ordinary key handler in this process will ever
/// fire.</para>
///
/// <para>Ctrl+Y and Ctrl+Shift+Z are both redo. Windows applications are split on which one they
/// use and users are split on which one they reach for; accepting both costs one comparison.</para>
///
/// <para>The keys are swallowed rather than passed on. While 標記 is on, the user is drawing on a
/// layer over someone else's window, and Ctrl+Z reaching that window would undo something there
/// instead — an edit in a document they cannot even see behind the capture. The hook lives exactly
/// as long as the panel does, so outside that mode the keys are the other application's again.</para>
/// </remarks>
internal sealed class AnnotationShortcutHook : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_Y = 0x59;
    private const int VK_Z = 0x5A;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Kept in a field: Windows holds a raw pointer to this delegate, so letting the GC collect it
    // would corrupt the hook chain.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Action _onUndo;
    private readonly Action _onRedo;
    private IntPtr _hookId;

    private AnnotationShortcutHook(Action onUndo, Action onRedo)
    {
        _onUndo = onUndo;
        _onRedo = onRedo;
        _proc = HookCallback;
    }

    public static AnnotationShortcutHook Install(Action onUndo, Action onRedo)
    {
        var hook = new AnnotationShortcutHook(onUndo, onRedo);
        hook._hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hook._proc, GetModuleHandle(null), 0);

        if (hook._hookId == IntPtr.Zero)
        {
            Log.Warn("標記 shortcut hook install failed (win32 error {Error}) — 復原 and 重做 stay on the panel only",
                Marshal.GetLastWin32Error());
        }

        return hook;
    }

    private static bool IsDown(int key) => (GetKeyState(key) & 0x8000) != 0;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || (wParam != (IntPtr)WM_KEYDOWN && wParam != (IntPtr)WM_SYSKEYDOWN))
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        int key = Marshal.ReadInt32(lParam);
        if ((key != VK_Z && key != VK_Y) || !IsDown(VK_CONTROL))
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        // Typing into one of ours, the shortcut is the box's own undo — see TypingWindows.
        if (TypingWindows.HaveForeground)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        // Ctrl+Y, or Ctrl+Shift+Z, is redo; plain Ctrl+Z is undo.
        var action = key == VK_Y || IsDown(VK_SHIFT) ? _onRedo : _onUndo;

        // Post, never wait. A low-level hook callback that outruns LowLevelHooksTimeout is silently
        // dropped from the chain by Windows, and the shortcut would stop working for the rest of the
        // session with nothing to show why.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
        return (IntPtr)1;
    }

    public void Dispose()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }
}
