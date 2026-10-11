namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// What a capture backend does with each frame the capture stack hands it, given that a readback is
/// the one expensive step and is held to one per <c>MaxFrameAge</c>.
/// </summary>
internal static class WgcFrameThrottle
{
    internal enum Arrival
    {
        /// <summary>Read it back now.</summary>
        Read,

        /// <summary>Too soon after the last readback: keep it for a timer, in case nothing newer comes.</summary>
        Hold,

        /// <summary>Nobody is polling: let it go.</summary>
        Skip,
    }

    /// <remarks>
    /// A frame that comes too soon used to be dropped, on the theory that another would follow. Over
    /// moving content one always does; over a still picture that changes once — a page scrolled, a
    /// line of dialogue replaced — the frame that changed it can be the last one composed for a
    /// second or more. Measured on a comic page: a repaint elsewhere was read back, the scroll's
    /// frame arrived 68ms after it and was dropped, and every poll for the next 0.8s compared the old
    /// page with itself. So the newest such frame is kept, and read back by a timer on the frame
    /// thread once the limit allows — see <see cref="WgcFrameThread"/>.
    /// </remarks>
    public static Arrival OnArrival(
        bool hasFrame, TimeSpan sinceRead, TimeSpan sinceGrab, TimeSpan maxFrameAge, TimeSpan idleAfter)
    {
        if (!hasFrame) return Arrival.Read;
        if (sinceGrab > idleAfter) return Arrival.Skip;
        return sinceRead < maxFrameAge ? Arrival.Hold : Arrival.Read;
    }

    /// <summary>
    /// How long a held frame waits before it is read back: until the last readback is
    /// <paramref name="maxFrameAge"/> old, so the timer and the frame handler together never read
    /// back more often than the handler alone would.
    /// </summary>
    public static TimeSpan HeldFrameDueIn(TimeSpan sinceRead, TimeSpan maxFrameAge) =>
        sinceRead >= maxFrameAge ? TimeSpan.Zero : maxFrameAge - sinceRead;

    /// <summary>
    /// Whether a frame of <paramref name="content"/> size asks for the pool to be recreated — which
    /// also means it is read back at once, since it cannot be kept past the rebuild.
    /// </summary>
    /// <param name="failedAt">The size a rebuild was last refused at, if the last attempt failed.</param>
    /// <remarks>
    /// Not again for a size the pool has already refused. Every frame of that size would otherwise
    /// count as a resizing frame, read back at once and ask again: a report on 2.7.1 under Windows 10
    /// shows exactly that, <c>received=61908 read=61853 rebuilds=0</c> over 17 minutes, an 8MB
    /// readback for every frame the window composed. Read from the pool as it stands, those frames go
    /// through the ordinary limit instead; the reader clips to the texture. A different size is a
    /// real change and is tried again.
    /// </remarks>
    public static bool NeedsResize(
        Windows.Graphics.SizeInt32 content, Windows.Graphics.SizeInt32 pool, Windows.Graphics.SizeInt32? failedAt) =>
        (content.Width != pool.Width || content.Height != pool.Height)
        && !(failedAt is { } failed && failed.Width == content.Width && failed.Height == content.Height);
}
