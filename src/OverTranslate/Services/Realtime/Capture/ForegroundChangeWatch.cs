using System.Runtime.InteropServices;
using System.Windows.Threading;
using NLog;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Raises an action on the dispatcher each time another application's window comes to the
/// foreground, so a 視窗擷取 session can put away the overlays of a source the user has just
/// switched away from — see the session controller's UpdateBlockVisibility.
/// </summary>
/// <remarks>
/// The stay-on-top tick asks the same question once a second, so on its own a covered source's
/// subtitles stayed over the game the user switched to for up to a second. Switching is the moment
/// that matters, and the system says when it happens: this answers it then, without a faster timer.
///
/// Out of context and installed on the dispatcher's own thread. Unlike the low-level hooks (see
/// <see cref="HookThread"/>) a WinEvent hook out of context holds nothing up while it waits: Windows
/// posts the event to this thread's queue and moves on, so a busy interface only delays the answer
/// here, which the tick would have given anyway. Several events before the dispatcher gets to them
/// make one call.
/// </remarks>
internal sealed class ForegroundChangeWatch : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    // This application's own windows coming forward — the control bar clicked — cover nothing.
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private delegate void WinEventProc(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    // Kept in a field: Windows holds a raw pointer to this delegate, so letting the GC collect it
    // would corrupt the hook chain.
    private readonly WinEventProc _proc;
    private readonly Action _onChanged;
    private readonly Dispatcher _dispatcher;
    private IntPtr _hookId;
    private bool _queued;

    private ForegroundChangeWatch(Action onChanged)
    {
        _onChanged = onChanged;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _proc = OnForegroundChanged;
    }

    /// <summary>
    /// Starts watching from the calling thread, which must be the dispatcher thread that
    /// <paramref name="onChanged"/> belongs on.
    /// </summary>
    public static ForegroundChangeWatch Install(Action onChanged)
    {
        var watch = new ForegroundChangeWatch(onChanged);
        watch._hookId = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, watch._proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        if (watch._hookId == IntPtr.Zero)
            Log.Warn("Foreground watch install failed — covered overlays are put away on the stay-on-top tick only");

        return watch;
    }

    private void OnForegroundChanged(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Posted rather than run here: the event can be delivered inside any message pump on this
        // thread, including one nested in the middle of laying the overlays out.
        if (_queued || _hookId == IntPtr.Zero) return;
        _queued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            _queued = false;
            if (_hookId == IntPtr.Zero) return;
            try
            {
                _onChanged();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Foreground change could not be applied to the overlays — left to the stay-on-top tick");
            }
        });
    }

    public void Dispose()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWinEvent(_hookId);
        _hookId = IntPtr.Zero;
    }
}
