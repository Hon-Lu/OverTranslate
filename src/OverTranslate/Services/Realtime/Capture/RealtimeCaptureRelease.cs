using System.Diagnostics;
using System.Windows.Threading;
using NLog;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Releases a capture backend on the thread that built it, after everything the user can see has
/// been put away, and says so when that takes too long — without the caller waiting for it.
/// </summary>
/// <remarks>
/// Ending a session used to dispose the backend on the UI thread before anything else was put
/// back. In 2.7.0 on Windows 10 that call did not return — it waited on the lock the deadlocked
/// frame handler and region loop were caught in (see <see cref="WgcFrameHandoff{T}"/>) — and the
/// session's overlays and control bar stayed over the game until the process was killed.
///
/// The first answer moved the release to a pool thread, and Windows 10 refused every one of them:
/// <c>GraphicsCaptureSession.Dispose</c> threw <c>RPC_E_WRONG_THREAD</c>, because there the
/// capture objects belong to the apartment that created them — the UI thread — and each session
/// ended that way was left running. So the release goes back to that thread, queued behind the
/// teardown the user sees rather than in front of it, and only the watching happens elsewhere: if
/// it is slow the log says so at Warn, with the backend's own steps and counters in the line.
///
/// It is safe on the UI thread again because the deadlock is gone — the frame handler never waits
/// on anything a thread inside the device can hold, so closing the pool cannot wait on a handler
/// that waits on us. A release queued when the application is already shutting down is dropped: the
/// process is about to take every capture object with it, and running it inline there is the one
/// way a release could still keep the application from exiting.
/// </remarks>
internal static class RealtimeCaptureRelease
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>How long a release may take before the log says so.</summary>
    public static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Queues the release on <paramref name="owner"/> — the dispatcher the backend was created on —
    /// below rendering, so the overlays and the control bar are gone from the screen first.
    /// </summary>
    /// <returns>Completes when the release has been watched to its end; for tests — callers do not wait on it.</returns>
    public static Task Release(IRealtimeCaptureBackend? capture, Dispatcher? owner) =>
        Release(capture, OnDispatcher(owner), WarnAfter);

    /// <param name="runOnOwner">
    /// Runs the release on the thread that owns the backend and returns a task for it. Never runs
    /// it on the caller's stack.
    /// </param>
    internal static Task Release(
        IRealtimeCaptureBackend? capture, Func<Action, Task> runOnOwner, TimeSpan warnAfter)
    {
        if (capture is null) return Task.CompletedTask;

        var started = Stopwatch.GetTimestamp();
        var release = runOnOwner(() =>
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

    /// <summary>
    /// Runs a release on <paramref name="owner"/> at <see cref="DispatcherPriority.Background"/> —
    /// after layout and rendering, so whatever was closed just before is already off the screen.
    /// Dropped when there is no dispatcher left to run it on.
    /// </summary>
    internal static Func<Action, Task> OnDispatcher(Dispatcher? owner) => release =>
    {
        if (owner is null || owner.HasShutdownStarted)
        {
            Log.Info("Realtime capture release skipped: the application is shutting down");
            return Task.CompletedTask;
        }

        return owner.InvokeAsync(release, DispatcherPriority.Background).Task;
    };
}
