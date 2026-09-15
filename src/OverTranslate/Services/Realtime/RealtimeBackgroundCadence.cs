namespace OverTranslate.Services.Realtime;

/// <summary>Target 10 refreshes/sec; overruns start the next fresh frame without catch-up ticks.</summary>
internal static class RealtimeBackgroundCadence
{
    public static readonly TimeSpan TargetInterval = TimeSpan.FromMilliseconds(100);

    public static TimeSpan RestAfter(TimeSpan elapsed) =>
        TimeSpan.FromTicks(Math.Max(0, TargetInterval.Ticks - elapsed.Ticks));
}
