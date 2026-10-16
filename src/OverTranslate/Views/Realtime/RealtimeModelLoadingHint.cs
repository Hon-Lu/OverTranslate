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
/// </remarks>
internal sealed class RealtimeModelLoadingHint : IDisposable
{
    private readonly Action<bool> _show;
    private readonly Action<Action> _post;
    private Task? _waiting;
    private bool _shown;
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
            Show(false);
            return;
        }

        Show(true);
        loading.ContinueWith(_ => _post(() =>
        {
            if (!_disposed && _waiting == loading) Show(false);
        }), TaskScheduler.Default);
    }

    private void Show(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
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
