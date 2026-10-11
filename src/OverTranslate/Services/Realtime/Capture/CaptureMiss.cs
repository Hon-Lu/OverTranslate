using System.Drawing;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Why a capture backend had no picture for a region on a poll — see
/// <see cref="IRealtimeCaptureBackend.DescribeMiss"/>.
/// </summary>
public enum CaptureMiss
{
    /// <summary>
    /// The region is not over the source as the source is now: the window is minimised, hidden or
    /// gone, or no longer under the region at all; the region is off the captured monitor. What it
    /// last showed belongs to nothing on the screen.
    /// </summary>
    NotOnSource,

    /// <summary>
    /// The region is over the source, but the last frame does not reach it yet — a frame older than
    /// the window's current size, no frame so far, frames held back for a moment. A newer frame is
    /// coming or has been asked for, and what the region last showed may well still be right.
    /// </summary>
    Waiting,
}

/// <summary>The geometry behind <see cref="CaptureMiss"/>: which kind a miss is.</summary>
internal static class CaptureMissRules
{
    /// <summary>
    /// A miss in 視窗擷取, judged against the window as it is now rather than against the last
    /// frame — which can be the size the window was before it was resized, and on a still window
    /// stays that size until something repaints.
    /// </summary>
    /// <param name="windowBounds">The window on the screen now, in physical pixels.</param>
    public static CaptureMiss ForWindow(bool onScreen, Rectangle windowBounds, Rectangle region) =>
        onScreen && windowBounds.IntersectsWith(region) ? CaptureMiss.Waiting : CaptureMiss.NotOnSource;

    /// <summary>A miss in 螢幕擷取: only a region off the monitor as it is now is off the source.</summary>
    public static CaptureMiss ForMonitor(Rectangle? monitorBounds, Rectangle region) =>
        monitorBounds is { } bounds && !bounds.IntersectsWith(region) ? CaptureMiss.NotOnSource : CaptureMiss.Waiting;

    /// <summary>
    /// Whether a frame of <paramref name="frame"/> size was taken of the window as it is now. A
    /// window's frame is the size of one of its two rectangles — the extended frame bounds, or the
    /// window rectangle with its invisible resize border; anything else is a frame of the window at
    /// another size.
    /// </summary>
    public static bool MatchesWindow(Size frame, Size extendedBounds, Size windowRect) =>
        frame == extendedBounds || frame == windowRect;
}

/// <summary>
/// When to ask a window for a frame of the size it is now, while the last frame is of another.
/// </summary>
/// <remarks>
/// Capture produces a frame when the window's content changes, and a frame that resizes the pool is
/// read back at the old pool's size. On a still window — a page, a chat, a paused video, a visual
/// novel waiting for a click — nothing changes after a resize, so the last frame stays the old size
/// for as long as nobody touches the window, and every region outside it has no picture.
///
/// So the window is asked first, cheaply: invalidated, which most windows answer by painting and so
/// by producing a frame. Not every source repaints on request, so if that has not produced one the
/// capture is started again, which always opens with a frame. Both are bounded per mismatch: a
/// source whose frames never match its rectangles (none is known) must not be invalidated and
/// restarted forever.
///
/// Thread-safe: every region's loop asks.
/// </remarks>
internal sealed class FreshFrameRequest
{
    public enum Ask
    {
        None,
        Redraw,
        Restart,
    }

    /// <summary>The least time between two asks.</summary>
    public static readonly TimeSpan AskEvery = TimeSpan.FromMilliseconds(300);

    /// <summary>How long a redraw is given to bring a frame before the capture is restarted.</summary>
    public static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(1);

    public const int MaxRedraws = 4;
    public const int MaxRestarts = 2;

    private readonly object _lock = new();
    private (Size Frame, Size Window)? _mismatch;
    private TimeSpan _since;
    private TimeSpan _lastAsk;
    private int _redraws;
    private int _restarts;

    /// <summary>
    /// The last frame is <paramref name="frame"/> and the window is <paramref name="window"/>, at
    /// <paramref name="now"/>: what to do about it, if anything. A different pair is a new mismatch.
    /// </summary>
    public Ask Next(Size frame, Size window, TimeSpan now)
    {
        lock (_lock)
        {
            if (_mismatch != (frame, window))
            {
                _mismatch = (frame, window);
                _since = now;
                _lastAsk = now;
                _redraws = 1;
                _restarts = 0;
                return Ask.Redraw;
            }

            if (now - _lastAsk < AskEvery) return Ask.None;

            // Each restart gets as long as the first redraw did before the next one is tried.
            if (_restarts < MaxRestarts && now - _since >= RestartAfter * (_restarts + 1))
            {
                _restarts++;
                _lastAsk = now;
                return Ask.Restart;
            }

            if (_redraws < MaxRedraws)
            {
                _redraws++;
                _lastAsk = now;
                return Ask.Redraw;
            }

            return Ask.None;
        }
    }

    /// <summary>The last frame is of the window as it is now: any later mismatch is a new one.</summary>
    public void Settled()
    {
        lock (_lock) _mismatch = null;
    }
}
