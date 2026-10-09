using System.Diagnostics;
using NLog;
using OverTranslate.Services.Realtime.Capture;

namespace OverTranslate.Services.Realtime;

/// <summary>One region's poll loop, as the session that started it keeps track of it.</summary>
internal sealed record RegionLoop(int RegionId, Task Task, StepMarker Step);

/// <summary>
/// Says so, at Warn, when region loops told to stop are still running a moment later.
/// </summary>
/// <remarks>
/// A loop only notices its cancellation between calls. One stuck inside a call — the 2.7.0 freeze
/// had them inside the capture's readback — never notices, and until now the only trace of that was
/// a "Realtime region N stopped" line that did not appear. Users run with nothing below Info in the
/// log, so the warning carries the whole of the evidence itself: which region, which step and for
/// how long, and the backend's own steps and counters.
/// </remarks>
internal static class RealtimeLoopWatch
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>How long loops may take to stop before the log says so.</summary>
    public static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(2);

    /// <returns>Completes when the watching does; for tests — callers do not wait on it.</returns>
    public static Task WatchStop(
        IReadOnlyList<RegionLoop> loops, IRealtimeCaptureBackend? capture, TimeSpan after)
    {
        if (loops.Count == 0) return Task.CompletedTask;

        var started = Stopwatch.GetTimestamp();
        return StallWatch.Watch(
            Task.WhenAll(loops.Select(loop => loop.Task)),
            after,
            () => Log.Warn(
                "Realtime region loop(s) still running {Seconds}s after being told to stop: {Loops}. " +
                "Capture {Backend}: {Steps} | {Activity}",
                after.TotalSeconds,
                DescribeUnfinished(loops),
                capture?.Name ?? "none",
                capture?.DescribeSteps() ?? "-",
                capture?.DescribeActivity() ?? "-"),
            () => Log.Info(
                "Realtime region loop(s) stopped late, after {Ms}ms",
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    }

    /// <summary>"region 0 at grab for 5012ms on thread 14; region 2 at ocr for 2210ms on thread 9".</summary>
    internal static string DescribeUnfinished(IEnumerable<RegionLoop> loops) =>
        string.Join("; ", loops
            .Where(loop => !loop.Task.IsCompleted)
            .Select(loop => $"region {loop.RegionId} at {loop.Step.Describe()}"));
}
