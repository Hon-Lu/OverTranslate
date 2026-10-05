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

        /// <summary>Too soon after the last readback: keep it, in case nothing newer comes.</summary>
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
    /// page with itself. So the newest such frame is kept until a poll can take it.
    /// </remarks>
    public static Arrival OnArrival(
        bool hasFrame, TimeSpan sinceRead, TimeSpan sinceGrab, TimeSpan maxFrameAge, TimeSpan idleAfter)
    {
        if (!hasFrame) return Arrival.Read;
        if (sinceGrab > idleAfter) return Arrival.Skip;
        return sinceRead < maxFrameAge ? Arrival.Hold : Arrival.Read;
    }

    /// <summary>
    /// Whether a poll may read back the frame being held. Under the same limit as the frame
    /// handler, so the two paths together never read back more often than one alone did.
    /// </summary>
    public static bool ReadsHeldFrame(TimeSpan sinceRead, TimeSpan maxFrameAge) => sinceRead >= maxFrameAge;
}
