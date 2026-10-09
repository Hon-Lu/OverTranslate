namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// The one frame a capture backend keeps for its next poll, and the gate that lets only one thread
/// read a frame back at a time — both without a lock.
/// </summary>
/// <remarks>
/// Two threads meet here: the capture stack's, raising <c>FrameArrived</c>, and a region loop's,
/// polling. Until 2.7.0 they met under a lock, and the lock was held across the calls into the frame
/// and the device — <c>Surface</c>, the readback, <c>Dispose</c>. On Windows 10 that froze realtime
/// translation a second into a session: the poll sat inside the lock in a call the capture stack
/// would not finish, the frame handler queued behind the lock, and no frame was delivered again.
///
/// So nothing here waits. The kept frame changes hands by exchange, and whoever takes it out owns
/// it — reads it, releases it — with nothing held. The gate is a flag rather than a lock: a thread
/// that finds a readback already under way does not wait for it but keeps or leaves its frame for
/// the next turn, which is also what keeps two readbacks from overlapping and the older one landing
/// last.
/// </remarks>
internal sealed class WgcFrameHandoff<T> where T : class, IDisposable
{
    private T? _held;
    private int _reading;

    /// <summary>Whether a frame is being kept.</summary>
    public bool HasHeld => Volatile.Read(ref _held) is not null;

    /// <summary>Keeps <paramref name="frame"/>, releasing the one it displaces.</summary>
    public void Hold(T frame) => Interlocked.Exchange(ref _held, frame)?.Dispose();

    /// <summary>Takes the kept frame out, if any. The caller owns it from here and must dispose it.</summary>
    public T? Take() => Interlocked.Exchange(ref _held, null);

    /// <summary>Releases the kept frame, if any.</summary>
    public void Release() => Take()?.Dispose();

    /// <summary>
    /// Claims the right to read a frame back. False means another thread is reading one now; the
    /// caller must not wait for it. A true return must be paired with <see cref="EndRead"/>.
    /// </summary>
    public bool TryBeginRead() => Interlocked.CompareExchange(ref _reading, 1, 0) == 0;

    public void EndRead() => Volatile.Write(ref _reading, 0);
}

/// <summary>A captured frame kept for a later poll, with the content size that came with it.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.18362.0")]
internal sealed record HeldCaptureFrame(
    Windows.Graphics.Capture.Direct3D11CaptureFrame Frame, Windows.Graphics.SizeInt32 Size) : IDisposable
{
    public void Dispose() => Frame.Dispose();
}
