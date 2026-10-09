namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// The one frame a capture backend keeps for its next poll, and the gate that lets only one thread
/// read a frame back at a time — both without a lock.
/// </summary>
/// <remarks>
/// The rule this exists for: <b>the frame handler never waits on any lock.</b> On Windows 10 the
/// capture stack raises <c>FrameArrived</c> while it holds the Direct3D device's own lock — the
/// multithread-protection lock, which capture turns on for the device it is given. Any thread
/// calling into that device meanwhile waits for the handler to return. So if the handler in turn
/// waits on something such a thread holds, neither ever moves again.
///
/// That is what froze realtime translation in 2.7.0, about a second into almost every session on
/// Windows 10 (measured in a Windows 10 VM, dumps in hand). A poll reading the kept frame back held
/// our lock and stood in <c>ID3D11DeviceContext::Unmap</c> waiting for the device lock; the handler,
/// called with the device lock held, stood waiting for ours. Making the device multithread-protected
/// changes nothing — it already is, and it is that lock — and taking the kept frame out without a
/// lock is not enough on its own: a handler that waits on the readback's lock instead closes the
/// same circle.
///
/// So nothing here waits. The kept frame changes hands by exchange, and whoever takes it out owns
/// it. The gate is a flag rather than a lock: a handler that finds a poll reading back does not
/// wait for it but keeps its newer frame for the next poll, and a poll that finds the handler
/// reading leaves the kept one alone. That also keeps the two from ever contending for
/// <see cref="WgcSurfaceReader"/>'s lock. A poll's own device calls may still wait for a handler to
/// finish, which is a few milliseconds and never a circle.
///
/// Windows 11 does not raise the event under the device lock, which is why the same code never
/// froze there.
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
