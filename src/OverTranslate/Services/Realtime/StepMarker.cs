using System.Diagnostics;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Where a piece of long-running work is right now, readable from another thread.
/// </summary>
/// <remarks>
/// The 2.7.0 freeze left a log that ended at "session ending", and finding which call had stopped
/// took a Windows 10 VM and a debugger. A loop or a frame handler that marks each step as it
/// enters it can say that in one line instead — see <see cref="StallWatch"/> for who asks.
///
/// Last writer wins: when several threads share one marker it says where the most recent of them
/// went, which is enough to name the call that never came back. Cheap enough for every frame — a
/// timestamp and two writes.
/// </remarks>
internal sealed class StepMarker
{
    private string _step;
    private long _since;
    private int _thread;

    public StepMarker(string initial)
    {
        _step = initial;
        _since = Stopwatch.GetTimestamp();
    }

    public void Set(string step)
    {
        Volatile.Write(ref _thread, Environment.CurrentManagedThreadId);
        Volatile.Write(ref _since, Stopwatch.GetTimestamp());
        Volatile.Write(ref _step, step);
    }

    public string Step => Volatile.Read(ref _step);

    /// <summary>"step for 1234ms on thread 7".</summary>
    public string Describe() =>
        $"{Volatile.Read(ref _step)} for {(long)Stopwatch.GetElapsedTime(Volatile.Read(ref _since)).TotalMilliseconds}ms " +
        $"on thread {Volatile.Read(ref _thread)}";
}
