using System.Windows.Threading;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// When the bar says 載入模型中…: while the manga models load, once that has taken longer than
/// <see cref="Delay"/> — a load nobody would notice is not announced.
/// </summary>
/// <remarks>
/// <see cref="Refresh"/> reads whether a load is under way rather than being told, so a start and
/// an end raised on another thread and queued to this one cannot arrive in a misleading order.
/// Used on the UI thread only.
/// </remarks>
internal sealed class RealtimeModelLoadingHint : IDisposable
{
    internal static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    private readonly Func<bool> _loading;
    private readonly Action<bool> _show;
    private readonly DispatcherTimer _delay = new() { Interval = Delay };
    private bool _shown;
    private bool _disposed;

    internal RealtimeModelLoadingHint(Func<bool> loading, Action<bool> show)
    {
        _loading = loading;
        _show = show;
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            if (!_disposed && _loading()) Show(true);
        };
    }

    internal bool IsShown => _shown;

    /// <summary>Catches up with whether a load is under way; called whenever one starts or ends.</summary>
    internal void Refresh()
    {
        if (_disposed) return;

        if (_loading())
        {
            if (!_shown && !_delay.IsEnabled) _delay.Start();
            return;
        }

        _delay.Stop();
        Show(false);
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
        _delay.Stop();
        Show(false);
        _disposed = true;
    }
}
