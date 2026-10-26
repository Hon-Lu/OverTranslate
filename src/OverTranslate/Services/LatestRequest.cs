namespace OverTranslate.Services;

/// <summary>
/// The one translation a window is still waiting for: starting another, or giving up on it, cancels
/// the one before and marks its answer as out of date.
/// </summary>
/// <remarks>
/// <para>Two guards rather than one. The cancellation is what stops a superseded request running to
/// the end behind the user's back — a local model works through every request it is handed in turn,
/// so a window that only ignores stale answers still makes the next one queue behind them. But a
/// cancellation is only a request: an engine can be past the point of noticing it, and its answer
/// then arrives anyway. The number is what keeps that answer off the screen.</para>
///
/// <para>Superseding always does both, so a request that ends in a cancellation is always one whose
/// number is out of date. Callers check the number first and return, which is how a cancellation
/// never reaches the error line: from the screen, a cancelled request looks exactly like the stale
/// answer it used to be.</para>
///
/// <para>UI thread only, as the windows using it are.</para>
/// </remarks>
internal sealed class LatestRequest
{
    private int _seq;
    private CancellationTokenSource? _cts;

    /// <summary>Supersedes whatever is in flight and starts the request that is now the latest.</summary>
    public (int Seq, CancellationToken Token) Start()
    {
        Supersede();
        _cts = new CancellationTokenSource();
        return (_seq, _cts.Token);
    }

    /// <summary>Cancels whatever is in flight, and makes sure its answer will not count.</summary>
    public void Supersede()
    {
        _seq++;

        // Cancelled and let go, not disposed: the request it was handed to may still be looking at
        // the token on its way out, and a source with no timer holds nothing that needs releasing.
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>Whether <paramref name="seq"/> is still the latest request.</summary>
    public bool IsCurrent(int seq) => seq == _seq;
}
