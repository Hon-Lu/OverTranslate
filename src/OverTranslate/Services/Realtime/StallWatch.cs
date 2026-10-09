namespace OverTranslate.Services.Realtime;

/// <summary>
/// Watches a piece of work nobody is going to wait for, and says so when it takes too long.
/// </summary>
/// <remarks>
/// Ending a realtime session used to wait, on the UI thread, for the capture to be released and
/// took for granted that the region loops would stop when told. On Windows 10 in 2.7.0 neither
/// held: a loop stuck inside the capture never saw its cancellation, the release queued behind it,
/// and the session's overlays stayed on screen over a game the user could no longer reach — with
/// nothing in the log past "session ending".
///
/// So the waits went, and this is what stands in for them: the caller carries on at once, and if
/// the work is still running after <c>after</c> a line says so, naming whatever the caller knows
/// about where it is. A stall that does clear later says that too, so the log tells a slow release
/// from one that never came back.
/// </remarks>
internal static class StallWatch
{
    /// <param name="onStalled">Called once, on a pool thread, if <paramref name="work"/> is still running after <paramref name="after"/>.</param>
    /// <param name="onLateFinish">Called once <paramref name="work"/> does finish, only if it had stalled.</param>
    /// <returns>Completes when the watching does; for tests — callers do not wait on it.</returns>
    public static Task Watch(Task work, TimeSpan after, Action onStalled, Action? onLateFinish = null) =>
        WatchAsync(work, after, onStalled, onLateFinish);

    private static async Task WatchAsync(Task work, TimeSpan after, Action onStalled, Action? onLateFinish)
    {
        if (await Task.WhenAny(work, Task.Delay(after)).ConfigureAwait(false) == work) return;

        onStalled();
        if (onLateFinish is null) return;

        try
        {
            await work.ConfigureAwait(false);
        }
        catch
        {
            // Whatever it ended with is the work's own business; what is being reported is that it
            // ended at all.
        }

        onLateFinish();
    }
}
