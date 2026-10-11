using System.Diagnostics;
using NLog;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Releases a capture backend without the caller waiting for it, and says so when that takes too
/// long.
/// </summary>
/// <remarks>
/// Ending a session used to dispose the backend on the UI thread before anything else was put
/// back. In 2.7.0 on Windows 10 that call did not return — it waited on the lock the deadlocked
/// frame handler and region loop were caught in — and the session's overlays and control bar
/// stayed over the game until the process was killed.
///
/// The first answer moved the release to a pool thread, and Windows 10 refused every one of them:
/// <c>GraphicsCaptureSession.Dispose</c> threw <c>RPC_E_WRONG_THREAD</c>, because there the
/// capture objects belong to the apartment that created them. 2.7.1 answered that by releasing on
/// the UI thread that created them. Now a backend creates them on its own frame thread and closes
/// them there itself (<see cref="WgcFrameThread"/>), so <c>Dispose</c> may be called from any thread
/// and only waits — which is why it is called on a pool thread here, where the wait holds up
/// nobody. If it is slow the log says so at Warn, with the backend's own steps and counters in the
/// line.
/// </remarks>
internal static class RealtimeCaptureRelease
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>How long a release may take before the log says so.</summary>
    public static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(2);

    /// <summary>Releases <paramref name="capture"/> on a pool thread.</summary>
    /// <returns>Completes when the release has been watched to its end; for tests — callers do not wait on it.</returns>
    public static Task Release(IRealtimeCaptureBackend? capture) =>
        Release(capture, release => Task.Run(release), WarnAfter);

    /// <param name="run">Runs the release elsewhere and returns a task for it. Never runs it on the caller's stack.</param>
    internal static Task Release(
        IRealtimeCaptureBackend? capture, Func<Action, Task> run, TimeSpan warnAfter)
    {
        if (capture is null) return Task.CompletedTask;

        var started = Stopwatch.GetTimestamp();
        var release = run(() =>
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
            // Everything needed to name the stuck call is in this one line: the shipped log has
            // nothing below Info in it.
            () => Log.Warn(
                "Realtime capture {Backend} is still being released after {Seconds}s; carrying on " +
                "without it. {Steps} | {Activity}",
                capture.Name, warnAfter.TotalSeconds, capture.DescribeSteps(), capture.DescribeActivity()),
            () => Log.Info(
                "Realtime capture {Backend} released late, after {Ms}ms",
                capture.Name, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }
}
