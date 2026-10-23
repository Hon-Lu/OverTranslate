using System.Runtime.InteropServices;
using System.Windows.Threading;
using NLog;
using OverTranslate.Models;

namespace OverTranslate.Services;

/// <summary>
/// Observes non-keyboard shortcut inputs. It never swallows the input: the mouse and controller
/// buttons continue to reach the foreground application/game after OverTranslate reacts to them.
/// </summary>
/// <remarks>
/// The hook and the controller poll live on a private message-pumping thread, and the hook callback
/// does nothing but hand the action over to the interface. Both follow from what a WH_MOUSE_LL
/// callback costs: Windows holds every mouse event in the system — including the ones the game the
/// user is playing is waiting for — until the callback returns, and a callback that outstays
/// <c>LowLevelHooksTimeout</c> is silently dropped from the hook chain, after which the shortcut is
/// dead for the rest of the session and nothing says why.
///
/// Running the action inside the callback is what made that reachable rather than theoretical: the
/// realtime shortcut builds a control bar and one overlay window per region before it returns.
/// Posting the action to the dispatcher answers that; keeping the hook itself off the dispatcher is
/// <see cref="HookThread"/>'s job, and the controller poll comes off the dispatcher with it.
///
/// The key sequences (Ctrl twice, Ctrl+C+C) are watched by a keyboard hook under the same rules,
/// installed only while one of them is bound: every key on the system passes through it, so nobody
/// who has not chosen one pays for it. Its callback compares a key code and a time and returns. Our
/// own synthesised keys — the copy and paste 取詞翻譯 and 替換翻譯 send — are left out of it, or the
/// Ctrl in them could complete a sequence the user never pressed.
/// </remarks>
internal sealed class GlobalAuxiliaryHotkeys : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmMouseHWheel = 0x020E;

    // Which side button an XBUTTON message is about is in the HIGH word of mouseData, not the low one.
    private const int Xbutton1 = 0x0001;
    private const int Xbutton2 = 0x0002;

    // Fast enough that a button press does not feel late, slow enough to stay invisible beside a
    // game. On the hook thread, so it neither waits for the interface nor holds it up.
    private static readonly TimeSpan GamepadPollInterval = TimeSpan.FromMilliseconds(35);

    // Replaced wholesale rather than mutated, so the hook thread can read it without a lock while
    // the interface is saving new settings.
    private Dictionary<ShortcutTrigger, HotkeyAction> _bindings = [];

    // Everything below is touched only on the hook thread.
    private readonly ushort[] _previousGamepadButtons = new ushort[4];
    private DispatcherTimer? _gamepadTimer;
    private LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook;
    private LowLevelKeyboardProc? _keyboardProc;
    private IntPtr _keyboardHook;
    private KeySequenceDetector? _sequences;

    private readonly HookThread _hookThread = new("OverTranslate auxiliary shortcuts");

    /// <summary>
    /// A shortcut fired. The second argument is, for Ctrl+C+C, the clipboard's sequence number from
    /// before the user's own copy — see <see cref="KeySequenceHit.ClipboardBefore"/>.
    /// </summary>
    public event Action<HotkeyAction, uint?>? ShortcutPressed;

    /// <summary>
    /// Set while the settings page is recording a shortcut, so pressing the one already bound to
    /// record it again does not also run it.
    /// </summary>
    public static bool Paused
    {
        get => Volatile.Read(ref _paused);
        set => Volatile.Write(ref _paused, value);
    }

    private static bool _paused;

    public void Register(IEnumerable<HotkeyBinding> bindings)
    {
        var map = new Dictionary<ShortcutTrigger, HotkeyAction>();
        foreach (var binding in bindings)
        {
            if (!binding.IsActive || binding.InputKind == ShortcutInputKind.Keyboard) continue;
            map[binding.Trigger] = binding.Action;
        }

        Volatile.Write(ref _bindings, map);

        var needsKeys = map.Keys.Any(trigger => trigger.IsKeySequence);
        // Ctrl twice needs the mouse as well: a Ctrl+click or Ctrl+scroll is not a tap, and only the
        // mouse hook can say one happened.
        var needsMouse = map.Keys.Any(trigger => trigger.IsMouse) ||
                         map.ContainsKey(ShortcutTrigger.KeySequence(ShortcutInputKind.CtrlDoubleTap));
        var needsGamepad = map.Keys.Any(trigger => trigger.Kind == ShortcutInputKind.Gamepad);

        // Nothing to observe: the thread is not merely idled but ended, because a message loop kept
        // alive for a set of bindings that no longer exists is a thread nobody would think to look
        // for while wondering what the process is doing.
        if (!needsMouse && !needsGamepad && !needsKeys)
        {
            StopHookThread();
            return;
        }

        _hookThread.Start();

        // Invoke rather than BeginInvoke: this runs when settings are saved, at human pace, and the
        // caller is entitled to have the shortcuts live by the time it returns.
        _hookThread.Invoke(() => ApplyOnHookThread(needsMouse, needsGamepad, needsKeys));
    }

    public void Dispose()
    {
        StopHookThread();
        Volatile.Write(ref _bindings, []);
    }

    private void StopHookThread()
    {
        _hookThread.Stop(() =>
        {
            UninstallMouseHook();
            UninstallKeyboardHook();
            _gamepadTimer?.Stop();
            _gamepadTimer = null;
            Array.Clear(_previousGamepadButtons, 0, _previousGamepadButtons.Length);
        });
    }

    /// <summary>Brings the hooks into line with the bindings. Runs on the hook thread.</summary>
    private void ApplyOnHookThread(bool needsMouse, bool needsGamepad, bool needsKeys)
    {
        if (needsMouse) InstallMouseHook();
        else UninstallMouseHook();

        if (needsKeys) InstallKeyboardHook();
        else UninstallKeyboardHook();

        // Created here rather than in the constructor: a DispatcherTimer binds to the thread that
        // makes it, and this one has to tick on the hook thread.
        _gamepadTimer ??= CreateGamepadTimer();

        if (needsGamepad)
        {
            SnapshotGamepads();
            _gamepadTimer.Start();
        }
        else
        {
            _gamepadTimer.Stop();
        }
    }

    private DispatcherTimer CreateGamepadTimer()
    {
        var timer = new DispatcherTimer { Interval = GamepadPollInterval };
        timer.Tick += (_, _) => PollGamepads();
        return timer;
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;

        // Kept in a field: Windows holds a raw pointer to this delegate, so letting the GC collect
        // it would corrupt the hook chain.
        _mouseProc = MouseHookCallback;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);

        if (_mouseHook == IntPtr.Zero)
        {
            // Otherwise the mouse-button shortcut is simply inert, which is indistinguishable from
            // a user who mis-set it — and this line is the only place that can tell them apart.
            Log.Warn(
                "Mouse shortcut hook install failed (win32 error {Error}); that shortcut will not fire",
                Marshal.GetLastWin32Error());
            _mouseProc = null;
        }
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _mouseProc = null;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();

            if (PressedKind(message, lParam) is { } kind &&
                Volatile.Read(ref _bindings).TryGetValue(ShortcutTrigger.Mouse(kind), out var action))
            {
                // Post, never run: see the remarks on this class for what a slow callback costs.
                Raise(action, null);
            }

            if (message is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmXButtonDown
                or WmMouseWheel or WmMouseHWheel)
            {
                _sequences?.OnOtherInput();
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;

        _sequences = new KeySequenceDetector(
            GetClipboardSequenceNumber,
            key => (GetAsyncKeyState((int)key) & 0x8000) != 0);

        // Kept in a field: Windows holds a raw pointer to this delegate, so letting the GC collect
        // it would corrupt the hook chain.
        _keyboardProc = KeyboardHookCallback;
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);

        if (_keyboardHook == IntPtr.Zero)
        {
            Log.Warn(
                "Key sequence hook install failed (win32 error {Error}); Ctrl twice and Ctrl+C+C will not fire",
                Marshal.GetLastWin32Error());
            _keyboardProc = null;
            _sequences = null;
        }
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        _keyboardProc = null;
        _sequences = null;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _sequences is { } sequences)
        {
            var message = wParam.ToInt32();
            var down = message is WmKeyDown or WmSysKeyDown;

            if (down || message is WmKeyUp or WmSysKeyUp)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                if (data.dwExtraInfo != KeyboardInput.InjectedMarker &&
                    sequences.OnKey(data.vkCode, down, data.time) is { } hit &&
                    Volatile.Read(ref _bindings).TryGetValue(ShortcutTrigger.KeySequence(hit.Kind), out var action))
                {
                    Raise(action, hit.ClipboardBefore);
                }
            }
        }

        // Never swallowed: the first Ctrl+C of Ctrl+C+C has to reach the application to copy.
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Which bindable mouse button this message is a press of, or null for every other mouse event.
    /// </summary>
    /// <remarks>
    /// The left and right buttons are absent on purpose: they are how the desktop is operated, and a
    /// shortcut on one would fire on every click the user makes anywhere. Middle and the two side
    /// buttons are the ones a game can spare.
    ///
    /// Both side buttons arrive as the same WM_XBUTTONDOWN, told apart only by the high word of
    /// mouseData — so lParam has to be read, unlike the middle button which the message alone
    /// identifies.
    /// </remarks>
    private static ShortcutInputKind? PressedKind(int message, IntPtr lParam)
    {
        if (message == WmMButtonDown) return ShortcutInputKind.MouseMiddle;
        if (message != WmXButtonDown) return null;

        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        return (data.mouseData >> 16) switch
        {
            Xbutton1 => ShortcutInputKind.MouseX1,
            Xbutton2 => ShortcutInputKind.MouseX2,
            _ => null,
        };
    }

    private void SnapshotGamepads()
    {
        for (int i = 0; i < _previousGamepadButtons.Length; i++)
            _previousGamepadButtons[i] = GamepadInput.TryGetButtons(i, out var buttons) ? buttons : (ushort)0;
    }

    private void PollGamepads()
    {
        var bindings = Volatile.Read(ref _bindings);

        for (int i = 0; i < _previousGamepadButtons.Length; i++)
        {
            if (!GamepadInput.TryGetButtons(i, out var current))
            {
                _previousGamepadButtons[i] = 0;
                continue;
            }

            ushort pressed = (ushort)(current & ~_previousGamepadButtons[i]);
            _previousGamepadButtons[i] = current;
            if (pressed == 0) continue;

            // More than one physical button may be pressed in one poll. Fire each supported edge;
            // conflict resolution ensures one button maps to at most one app action.
            foreach (var button in GamepadInput.SupportedButtons)
            {
                if ((pressed & (ushort)button) == 0) continue;
                if (bindings.TryGetValue(ShortcutTrigger.Gamepad(button), out var action))
                    Raise(action, null);
            }
        }
    }

    /// <summary>
    /// Hands an action to the interface. The subscribers open windows and start sessions, so they
    /// run on the dispatcher — and asynchronously, so neither the hook callback nor the poll waits
    /// for any of it.
    /// </summary>
    private void Raise(HotkeyAction action, uint? clipboardBefore)
    {
        if (Paused) return;
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(
            () => ShortcutPressed?.Invoke(action, clipboardBefore));
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>The low-level keyboard hook's payload.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// The low-level mouse hook's payload. Only <c>mouseData</c> is read, but the whole layout has to
    /// be declared for the field to land at the right offset.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public int mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
