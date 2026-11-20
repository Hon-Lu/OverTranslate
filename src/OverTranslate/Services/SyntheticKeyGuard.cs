using NLog;

namespace OverTranslate.Services;

/// <summary>
/// Keeps keyboard shortcuts from acting while this application is typing into the system itself,
/// and for a moment after.
/// </summary>
/// <remarks>
/// <para>取詞翻譯 and 替換翻譯 work by sending Ctrl+C and Ctrl+V — see <see cref="KeyboardInput"/>.
/// A key sent that way is, to Windows, a key: RegisterHotKey answers it exactly as it answers the
/// user's own, and nothing in WM_HOTKEY says where the press came from. A shortcut whose combination
/// is one of the keys its own action sends therefore fires itself, and every run sends the key again.
/// That happened with 取詞翻譯 on Ctrl+C: hundreds of copies a second, the input queue full, the mouse
/// dead, and the process had to be killed.</para>
///
/// <para>The settings refuse those combinations (<see cref="HotkeyBindings.IsBindable"/>), but that
/// only covers the combinations known today. This is the half that does not depend on knowing them:
/// while a send is in progress — and while a selection is being read or replaced, which is the stretch
/// a second press would trample — a keyboard shortcut is dropped, and so is one that arrives within
/// <see cref="Grace"/> of the last send ending, which is when a WM_HOTKEY the send itself produced
/// can still be waiting in the queue. A loop then stops after one turn, whatever the key.</para>
///
/// <para>Only keyboard shortcuts consult this. A mouse or controller button cannot be produced by a
/// synthesised key, so there is nothing for it to protect them from.</para>
/// </remarks>
internal sealed class SyntheticKeyGuard
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>The one guard the application's own sends and shortcuts share.</summary>
    public static SyntheticKeyGuard Shared { get; } = new(() => Environment.TickCount64);

    /// <summary>How long after a send ends a keyboard shortcut is still not acted on.</summary>
    /// <remarks>
    /// Longer than the gap between a send and the WM_HOTKEY it causes, which is posted while the key
    /// is still held and normally handled within the same few milliseconds; short enough that a
    /// deliberate second press by a person, which takes well over this, is not lost.
    /// </remarks>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    private readonly Func<long> _clockMs;
    private int _active;
    private long _lastEndMs = long.MinValue / 2;

    /// <param name="clockMs">Milliseconds from any fixed origin; replaced by tests.</param>
    internal SyntheticKeyGuard(Func<long> clockMs) => _clockMs = clockMs;

    /// <summary>
    /// Marks a send, or a read or replace built on one, as in progress until the result is disposed.
    /// </summary>
    /// <remarks>Scopes may nest; the guard holds until the outermost one ends, plus <see cref="Grace"/>.</remarks>
    public IDisposable Enter()
    {
        Interlocked.Increment(ref _active);
        return new Scope(this);
    }

    /// <summary>Whether a keyboard shortcut arriving now should be dropped.</summary>
    /// <remarks>Read from the hook thread as well as the interface, hence the interlocked state.</remarks>
    public bool IsSuppressing =>
        Volatile.Read(ref _active) > 0 ||
        _clockMs() - Interlocked.Read(ref _lastEndMs) < (long)Grace.TotalMilliseconds;

    /// <summary>
    /// Whether a keyboard shortcut may act now; logs the one that may not, which is the only trace a
    /// dropped press leaves.
    /// </summary>
    public bool AllowsShortcut(string what)
    {
        if (!IsSuppressing) return true;

        Log.Info("Shortcut {Shortcut} ignored: it arrived while this application was sending keys itself", what);
        return false;
    }

    private void Exit()
    {
        // The end time first, so there is no instant in which neither the count nor the grace holds.
        Interlocked.Exchange(ref _lastEndMs, _clockMs());
        Interlocked.Decrement(ref _active);
    }

    private sealed class Scope(SyntheticKeyGuard owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Exit();
        }
    }
}
