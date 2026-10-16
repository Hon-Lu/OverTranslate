using System.Diagnostics;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// When the bar says 載入模型中…: from the press of 開始翻譯 until the models that session found
/// missing have all loaded — or failed to, so a hint is never left up over a load that is over.
/// </summary>
/// <remarks>
/// Waits on the session's loads (<see cref="Services.Realtime.RealtimeTranslationSession.ModelsLoading"/>)
/// rather than listening to each engine: one task for both, so it cannot drop back to the pair
/// between two loads, and a load that ended before anyone listened is already done. Used on the UI
/// thread only; the end of a load reaches it through <c>post</c>.
///
/// Once up, it stays up for <see cref="MinShown"/>: the recogniser alone loads in about 110ms, and
/// a hint gone that fast reads as a flicker rather than as a message. 結束 takes it down at once.
/// </remarks>
internal sealed class RealtimeModelLoadingHint : IDisposable
{
    internal static readonly TimeSpan MinShown = TimeSpan.FromMilliseconds(500);

    private readonly Action<bool> _show;
    private readonly Action<Action> _post;
    private Task? _waiting;
    private bool _shown;
    private long _shownAt;
    private bool _disposed;

    /// <param name="show">Puts the hint up or takes it down; called only when that changes.</param>
    /// <param name="post">Runs an action on the UI thread, from the thread a load ended on.</param>
    internal RealtimeModelLoadingHint(Action<bool> show, Action<Action> post)
    {
        _show = show;
        _post = post;
    }

    internal bool IsShown => _shown;

    /// <summary>Shows the hint until <paramref name="loading"/> ends; a later call takes over.</summary>
    internal void Wait(Task loading)
    {
        if (_disposed) return;

        _waiting = loading;
        if (loading.IsCompleted)
        {
            Ended(loading);
            return;
        }

        Show(true);
        loading.ContinueWith(_ => _post(() => Ended(loading)), TaskScheduler.Default);
    }

    // Down now, or once it has been up for MinShown — unless a later Wait or 結束 has taken over.
    private void Ended(Task loading)
    {
        if (_disposed || _waiting != loading || !_shown) return;

        var left = MinShown - Stopwatch.GetElapsedTime(_shownAt);
        if (left <= TimeSpan.Zero)
        {
            Show(false);
            return;
        }

        // Asked again rather than taken down: the timer counts in coarse ticks and can come back a
        // few milliseconds short.
        Task.Delay(left).ContinueWith(_ => _post(() => Ended(loading)), TaskScheduler.Default);
    }

    private void Show(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        if (shown) _shownAt = Stopwatch.GetTimestamp();
        _show(shown);
    }

    /// <summary>Takes the hint down if it is up, and never puts it up again.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Show(false);
        _disposed = true;
        _waiting = null;
    }
}
