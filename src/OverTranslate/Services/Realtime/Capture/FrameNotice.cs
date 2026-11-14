namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// Tells whoever is waiting that a capture backend has read back a new frame.
/// </summary>
/// <remarks>
/// Raised from the thread that handles frames, which must never wait on anything — see
/// <see cref="WgcFrameThread"/>. So raising only swaps one task for the next, and whatever was
/// waiting carries on elsewhere, on the thread pool.
/// </remarks>
internal sealed class FrameNotice
{
    private TaskCompletionSource _next = New();

    /// <summary>Completes when the next frame has been read back.</summary>
    public Task Next => Volatile.Read(ref _next).Task;

    public void Raise() => Interlocked.Exchange(ref _next, New()).TrySetResult();

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
