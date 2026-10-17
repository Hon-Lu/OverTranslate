using System.Diagnostics;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// When a region may ask again for a translation that did not reach the screen.
/// </summary>
/// <remarks>
/// <para>A retry forgets what the region shows, so the next poll reads the whole region again and
/// sends whatever is not cached. Over a still picture whose translation keeps failing — a provider
/// rate-limiting, a local model server that is not running, an engine that answers a line with the
/// original every time — that was a full recognition and another request every other poll, for as
/// long as the picture stayed up, and a rate limit asked that often never lifts.</para>
///
/// <para>So a retry after a failure waits, the same 1→2→4→8→16 seconds a failed read does (see
/// <see cref="RealtimeRegionState.FirstWaitAfterFailure"/>); a retry for any other reason — a pass
/// dropped or abandoned rather than refused — is taken at once, as before. Only the retry is held:
/// a picture that changes is read and translated as any change is, since the new words are the
/// ones the reader is waiting for. The count starts over once a translation is drawn whole.</para>
/// </remarks>
internal sealed class RealtimeRetryBackoff
{
    private readonly object _gate = new();
    private readonly Func<TimeSpan> _now;

    private bool _requested;
    private int _failures;
    private TimeSpan _notBefore;

    /// <param name="now">The clock; elapsed time since the process started unless a test gives one.</param>
    public RealtimeRetryBackoff(Func<TimeSpan>? now = null)
    {
        _now = now ?? (() => Stopwatch.GetElapsedTime(0));
    }

    /// <summary>Asks for a retry with no failure behind it, taken at the next poll.</summary>
    public void Request()
    {
        lock (_gate) _requested = true;
    }

    /// <summary>
    /// Asks for a retry after a translation failed or came back with lines untranslated, taken once
    /// the wait for this many failures in a row has passed.
    /// </summary>
    public void RequestAfterFailure()
    {
        lock (_gate)
        {
            _requested = true;
            _failures++;
            _notBefore = _now() + Wait(_failures);
        }
    }

    /// <summary>A translation was drawn with every line translated: the next failure waits the first wait.</summary>
    public void Succeeded()
    {
        lock (_gate)
        {
            _failures = 0;
            _notBefore = TimeSpan.Zero;
        }
    }

    /// <summary>Whether a retry is asked for and due; true clears the request.</summary>
    public bool TakeRequest()
    {
        lock (_gate)
        {
            if (!_requested || _now() < _notBefore) return false;
            _requested = false;
            return true;
        }
    }

    internal static TimeSpan Wait(int failures) => TimeSpan.FromTicks(Math.Min(
        RealtimeRegionState.LongestWaitAfterFailure.Ticks,
        RealtimeRegionState.FirstWaitAfterFailure.Ticks << Math.Min(failures - 1, 8)));
}
