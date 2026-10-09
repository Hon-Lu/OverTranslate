using System.Diagnostics;
using NLog;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Releases a capture backend on a pool thread, never on the caller's.
/// </summary>
/// <remarks>
/// Ending a session used to dispose the backend on the UI thread before anything else was put
/// back. In 2.7.0 on Windows 10 that call did not return — it waited on the lock the deadlocked
/// frame handler and region loop were caught in (see <see cref="WgcFrameHandoff{T}"/>) — and the
/// session's overlays and control bar stayed over the game until the process was killed.
///
/// The deadlock itself is fixed; this is so that a release which still hangs, for whatever reason
/// comes next, costs a thread and a line in the log rather than the user's screen. A backend owns
/// its own device, pool and session, so the old one going at its own pace cannot touch a new one
/// built meanwhile. Pool threads are background threads, so one stuck here does not keep the
/// application from exiting either.
/// </remarks>
internal static class RealtimeCaptureRelease
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>How long a release may take before the log says so.</summary>
    public static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(2);

    /// <returns>Completes when the release has been watched to its end; for tests — callers do not wait on it.</returns>
    public static Task Release(IRealtimeCaptureBackend? capture) => Release(capture, WarnAfter);

    internal static Task Release(IRealtimeCaptureBackend? capture, TimeSpan warnAfter)
    {
        if (capture is null) return Task.CompletedTask;

        var started = Stopwatch.GetTimestamp();
        var release = Task.Run(() =>
        {
            try
            {
                capture.Dispose();
            }
            catch (Exception ex)
            {
                // A capture source can hold graphics resources whose release can fail on its own.
                Log.Error(ex, "Failed to dispose the {Backend} capture backend", capture.Name);
            }
        });

        return StallWatch.Watch(
            release,
            warnAfter,
            () => Log.Warn(
                "Realtime capture {Backend} is still being released after {Seconds}s; carrying on " +
                "without it ({Activity})",
                capture.Name, warnAfter.TotalSeconds, capture.DescribeActivity()),
            () => Log.Info(
                "Realtime capture {Backend} released late, after {Ms}ms",
                capture.Name, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }
}
