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
}
